# Dorsal actuator force model (copy)

Copied unchanged from `Scripts/ForceModelActuator` (model id `actuator-all-six-v1-045aec8a0a2e`).
`actuator_force_model.py` checks the coefficient file's SHA-256 on load, so an edited or
mismatched `actuator_coefficients.json` is refused.

Forward model: force F (N) = f(pressure P in bar, actuator length L in mm 55-80, branch).
Branch: `pressurization` (pressure rising) or `release` (pressure falling).
See `MODEL_EQUATION.md` for the exact equations.

The relay (`tools/glove_serial_bridge.py`) runs it backwards through `tools/dorsal_pressure.py`:
wanted dorsal force + L from Unity -> target pressure -> Arduino.

If the model is refitted, replace both files here together.
