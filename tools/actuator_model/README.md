# Dorsal actuator model

**In use: `pfl_model.py` + `pfl_coefficients.json`**, the P(F, L) model of the braided (extensile McKibben)
hydraulic actuator by Tran Van Nghia (28 Oct 2026; source files in `Scripts/ForceModelActuator/NewData`).
One set of 8 fitted coefficients for four recordings at 95, 100, 105 and 110 mm.

Inverse used by the relay (L = measured actuator length in mm, F in N, P in bar; s = +1 pressurize, -1 release):

    P = P* + (F_el + F_f0*s - a1*P* - F) / D          above the knee P*
    F_el = 0.03285 L - 13476/L^2   a1 = 23.34/L   P* = 17.09 L^-0.910 + 0.0245 s
    D = 7.854 (1 - 5.76e-5 L^2) / (1 - 1.92e-5 L^2)   F_f0 = 0.0034 N

Below the knee (the flat top ~0.07 N of each length's range) the knee is taken as a hinge, so the pressure
goes continuously from P* to 0 bar; the fit's smooth knee function is not in the files we received.
Valid for L 95-110 mm only (the relay clamps L); `python pfl_model.py` checks the worked example
(L 100, F 1.394 N, s +1 -> 0.400 bar).

The relay (`tools/glove_serial_bridge.py`) uses it through `tools/dorsal_pressure.py`:
wanted dorsal force + L (from Unity, or from another program on UDP 5013) -> target pressure -> Arduino.
If the model is refitted with the same form, only `pfl_coefficients.json` changes.

**No longer used:** `actuator_force_model.py`, `actuator_coefficients.json`, `MODEL_EQUATION.md`: the earlier
empirical lookup model (tether settings 55-80 mm), kept for reference.
