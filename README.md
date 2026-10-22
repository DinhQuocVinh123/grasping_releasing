# Grasping and releasing with a haptic glove in VR

A Meta Quest 3 VR study setup: the user wears a pneumatic haptic glove, picks up and squeezes virtual objects with
the thumb and index finger, and feels the contact force through actuators on the glove.

The Quest cannot track a gloved hand well, so the glove hand is rebuilt from the headset's own colour camera:
the camera image goes to a PC, an RTMPose model trained on the glove finds the finger points, and Unity fits a
virtual hand to those points. The force computed from virtual contact goes back out through a PC relay to an
Arduino Mega that drives the actuators.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the data flow and what every script does.

## Repository layout

| Folder | Contents |
|---|---|
| `Assets/Scripts/Tracking/` | Camera streaming, receiving the model's points, building the glove hand from the image |
| `Assets/Scripts/Haptics/` | Contact -> force signals and the haptic outputs (`GloveForceEsp32Output.cs` and `GloveEsp32Protocol.cs` sit in `Assets/Scripts/` because the end-to-end test compiles them from there) |
| `Assets/Scripts/Interaction/` | Squeezable and rigid virtual objects, keeping virtual fingers on their surface, visual cues |
| `Assets/Scripts/Experiments/` | Stiffness discrimination experiment and CSV logging |
| `Assets/Scripts/Measurement/` | Finger length measurement (force model input) and bare-hand pose recording |
| `Assets/Editor/` | `HandImageFitBench`: tests the hand fit in the Editor on simulated data and recordings |
| `Assets/Scenes/SampleScene.unity` | The only scene |
| `arduino/` | Arduino Mega sketch for the actuators and the serial-line parser |
| `tools/` | PC relay to the Arduino (`glove_serial_bridge.py`, `dorsal_pressure.py`, `actuator_model/`), simulators and tests |
| `tools/analysis/` | Scripts that analyse logs pulled from the headset (latency, filter tuning, finger lengths) |

The Python program that runs the hand model (`run_glove_quest_stream.py`, its labelling and training scripts, and the
trained checkpoint) is **not in this repository**; it lives with its own virtual environment on the lab PC.

## Requirements

- Unity 6000.4.2f1 with Android build support, Meta XR SDK 205 (`com.meta.xr.sdk.all`)
- Meta Quest 3 with passthrough camera access enabled for the app
- A PC on the same Wi-Fi network, with an NVIDIA GPU, running the hand-model program
- For the glove: Arduino Mega on USB, Python 3 with `pyserial` for the relay

## Running a session

1. **PC, hand model:** start `run_glove_quest_stream.py` (outside this repo). It broadcasts its address on UDP 5008,
   receives images on TCP 5007 and sends the finger points back on UDP 5005.
2. **PC, glove relay** (only when the glove is used):
   ```
   pip install pyserial
   python tools/glove_serial_bridge.py --list        # find the Mega's COM port
   python tools/glove_serial_bridge.py --port COM5
   ```
   Use `--dry-run` to see the serial lines without an Arduino.
3. **Arduino Mega:** upload `arduino/glove_mega_steppers/glove_mega_steppers.ino`, then press SW4 (HOME) before
   SW5 (RUN). SW6 stops everything.
4. **Headset:** build `SampleScene` for Android, install the APK and start it. The headset finds the PC by itself
   (auto-discovery); the glove hand appears once the model sees the glove.

The headset and the relay use protocol version 3 (16 floats on UDP 5011). An older relay or Arduino sketch ignores
the packets, so rebuild and update all three together.

## Diagnostics

Turn on **Log Diagnostics** on `FingerUDPReceiver` (glove hand object) to write `glove_diag_*.csv` and
`glove_latency_*.csv` on the headset. Copy them with
`adb pull /sdcard/Android/data/<package>/files/` and analyse them with the scripts in `tools/analysis/`
(each script's docstring explains its input and output).

## Tests

- `tools/e2e_test/run_e2e.py`: Unity force output -> relay -> Arduino parser, end to end (Linux only: g++, mono-mcs, pyserial).
- `tools/test_dorsal_pressure.py`, `tools/test_link.cpp`, `tools/stepper_sim/`: relay model, serial parser and stepper sketch on the PC.
