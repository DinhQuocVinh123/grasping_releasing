/*
  Arduino Mega - glove dorsal motors driven from Unity over USB

  SW1 (pin 2): M1 home limit
  SW2 (pin 3): M2 home limit
  SW3 (pin 4): M3 home limit
  SW4 (pin 5): HOME  -> home every motor, then move forward REF_STEPS (140):
                        that is the REFERENCE position (no force)
  SW5 (pin 6): RUN   -> loop: read the USB stream from the PC relay and move
                        M2 (index dorsal) and M3 (thumb dorsal) to match it
  SW6 (pin 7): STOP  -> same as before: stops everything at once, has priority

  M1 STEP/DIR = 8/9    (not driven by the stream yet: stays at reference)
  M2 STEP/DIR = 10/11  <- index dorsal pressure
  M3 STEP/DIR = 12/13  <- thumb dorsal pressure

  Switches connect input to GND when pressed.

  The stream (from tools/glove_serial_bridge.py, 50 lines/s, 115200 baud):
    H,<seq>,<thumb P,U,V,Bar>,<index P,U,V,Bar>*<checksum>
  parsed by GloveSerialLink.h (keep that file in this folder). Bar is the
  dorsal actuator's target PRESSURE in bar, computed on the PC from the wanted
  force and the actuator length with the actuator force model.

  Pressure -> steps (placeholder until a measured steps/pressure curve exists):
    the reference position holds P_REF_BAR (no force); every bar BELOW it is
    STEPS_PER_BAR steps pulled back toward home (the syringe draws fluid out,
    pressure drops, the actuator pulls).
    Example: 0.424 bar -> (1.0 - 0.424) x 100 = 57.6 -> M2 58 steps back.
  Bar >= P_REF_BAR (or no valid line for 200 ms) -> that motor goes back to the reference.
*/
#include "GloveSerialLink.h"

const byte STEP_PIN[3] = {8, 10, 12};
const byte DIR_PIN[3]  = {9, 11, 13};

const byte LIMIT_PIN[3] = {2, 3, 4};
const byte SW_HOME = 5;
const byte SW_RUN  = 6;
const byte SW_STOP = 7;

// Set these according to your physical home direction.
// HIGH = home direction HIGH, LOW = home direction LOW.
// Moving TOWARD home = "pull back" (more force). Away from home = "forward".
const byte HOME_DIR[3] = {LOW, LOW, LOW};

// Reference position: this many steps forward from the home switch.
const long REF_STEPS = 140;

// Pressure -> steps. PLACEHOLDERS until the syringe is calibrated (steps vs. bar):
// P_REF_BAR is the pressure at the reference position = no force. It must match the
// relay's --rest-bar (default 1.0).
const float P_REF_BAR = 1.0f;
// Steps pulled back per bar below P_REF_BAR.
const float STEPS_PER_BAR = 100.0f;
// Never pull back further than this from the reference (keeps clear of the
// home switch: 140 - 10). 0 bar is only 100 steps, so this is only a guard.
const long MAX_PULL_STEPS = 130;

// Normal step pulse timing. Increase STEP_INTERVAL_US to slow down.
const unsigned int STEP_INTERVAL_US = 2000;
const unsigned int STEP_PULSE_US = 10;

// Which motor follows which finger's dorsal pressure (0 = thumb, 1 = index, -1 = none).
const int8_t MOTOR_FINGER[3] = {-1, GloveSerialLink::INDEX, GloveSerialLink::THUMB};

GloveSerialLink glove;

bool running = false;
bool stopRequested = false;
bool homed = false;          // true once SW4 finished: positions are known
bool lastRunPressed = false;
bool lastHomePressed = false;
bool lastStopPressed = false;

// Position of each motor in steps relative to the REFERENCE.
// 0 = reference, negative = pulled back toward home (force), -REF_STEPS = home switch.
long pos[3] = {0, 0, 0};
long target[3] = {0, 0, 0};
unsigned long lastStepUs[3] = {0, 0, 0};

bool homeAllMotors();
void runFromUsb();
long stepsForPressure(float bar);
void pulseStep(byte motor);
void pulseStepNow(byte motor);
void printIdleStatus();

void setup() {
  Serial.begin(115200);

  for (byte i = 0; i < 3; i++) {
    pinMode(STEP_PIN[i], OUTPUT);
    pinMode(DIR_PIN[i], OUTPUT);
    pinMode(LIMIT_PIN[i], INPUT_PULLUP);

    digitalWrite(STEP_PIN[i], LOW);
    digitalWrite(DIR_PIN[i], !HOME_DIR[i]);
  }

  pinMode(SW_HOME, INPUT_PULLUP);
  pinMode(SW_RUN, INPUT_PULLUP);
  pinMode(SW_STOP, INPUT_PULLUP);

  Serial.println(F("Glove dorsal motors ready."));
  Serial.println(F("SW4 = HOME (then reference), SW5 = RUN from USB, SW6 = STOP"));
}

void loop() {
  // Read the USB stream EVERY loop, whatever mode we are in, so the 64-byte
  // serial buffer never overflows and lines stay whole and in order.
  glove.poll(Serial);

  bool runPressed  = digitalRead(SW_RUN) == LOW;
  bool homePressed = digitalRead(SW_HOME) == LOW;
  bool stopPressed = digitalRead(SW_STOP) == LOW;

  // STOP takes priority.
  if (stopPressed) {
    if (running) {
      Serial.println(F("STOP pressed."));
    }
    running = false;
    stopRequested = true;
  } else {
    stopRequested = false;
  }

  // Start the USB loop on a new SW5 press -- only once positions are known.
  if (runPressed && !lastRunPressed && !stopPressed) {
    if (!homed) {
      Serial.println(F("RUN refused: press SW4 (HOME) first so the reference is known."));
    } else {
      running = true;
      Serial.println(F("RUN: following the USB stream (M2 = index dorsal bar, M3 = thumb dorsal bar)."));
    }
  }

  // Home on a new SW4 press.
  if (homePressed && !lastHomePressed && !stopPressed) {
    running = false;
    Serial.println(F("Homing all motors..."));
    if (homeAllMotors()) {
      Serial.println(F("Homing complete. All motors at the reference position."));
    }
  }

  lastRunPressed = runPressed;
  lastHomePressed = homePressed;
  lastStopPressed = stopPressed;

  if (running && !stopPressed) {
    runFromUsb();
  } else {
    printIdleStatus();
  }
}

// Not in RUN: every 2 s say what is arriving, so the link can be checked
// without pressing anything. The motors do not move in this state.
void printIdleStatus() {
  static unsigned long lastIdlePrint = 0;
  if (millis() - lastIdlePrint < 2000) return;
  lastIdlePrint = millis();
  Serial.print(F("IDLE "));
  Serial.print(homed ? F("homed") : F("not homed"));
  if (glove.alive()) {
    Serial.print(F(" | link OK seq "));
    Serial.print(glove.seq);
    Serial.print(F(" | index "));
    Serial.print(glove.finger[GloveSerialLink::INDEX].dorsalBar, 3);
    Serial.print(F(" bar, thumb "));
    Serial.print(glove.finger[GloveSerialLink::THUMB].dorsalBar, 3);
    Serial.print(F(" bar"));
  } else {
    Serial.print(F(" | NO LINK"));
  }
  Serial.print(F(" | bad lines "));
  Serial.print(glove.badLines);
  Serial.println(homed ? F(" | SW5 = run") : F(" | SW4 = home, then SW5 = run"));
}

bool isStopped() {
  return digitalRead(SW_STOP) == LOW;
}

// ---------------------------------------------------------------------------
// HOME: each motor to its limit switch, then REF_STEPS forward = reference.
// Blocking, like before. Lines that arrive meanwhile are lost -- harmless, the
// parser drops broken lines and the relay sends 50 new ones every second.
// ---------------------------------------------------------------------------
bool homeAllMotors() {
  homed = false;

  for (byte i = 0; i < 3; i++) {
    if (isStopped()) {
      Serial.println(F("Homing aborted by STOP."));
      return false;
    }

    Serial.print(F("Homing M"));
    Serial.println(i + 1);

    digitalWrite(DIR_PIN[i], HOME_DIR[i]);

    // Move until this motor's own limit switch is pressed.
    while (digitalRead(LIMIT_PIN[i]) == HIGH) {
      if (isStopped()) {
        Serial.println(F("Homing aborted by STOP."));
        return false;
      }
      pulseStep(i);
    }

    Serial.print(F("M"));
    Serial.print(i + 1);
    Serial.println(F(" reached home."));

    delay(100);
  }

  // All home: now forward REF_STEPS together to the reference position.
  for (byte i = 0; i < 3; i++) {
    digitalWrite(DIR_PIN[i], !HOME_DIR[i]);
  }
  for (long s = 0; s < REF_STEPS; s++) {
    if (isStopped()) {
      Serial.println(F("Move to reference aborted by STOP."));
      return false;
    }
    for (byte i = 0; i < 3; i++) digitalWrite(STEP_PIN[i], HIGH);
    delayMicroseconds(STEP_PULSE_US);
    for (byte i = 0; i < 3; i++) digitalWrite(STEP_PIN[i], LOW);
    delayMicroseconds(STEP_INTERVAL_US - STEP_PULSE_US);
  }

  for (byte i = 0; i < 3; i++) {
    pos[i] = 0;
    target[i] = 0;
  }
  homed = true;
  return true;
}

// ---------------------------------------------------------------------------
// RUN: one pass per loop(), never blocks. Turns the latest line into a target
// for each motor, then lets each motor take at most one step toward it.
// ---------------------------------------------------------------------------
long stepsForPressure(float bar) {
  if (!(bar < P_REF_BAR)) return 0;                      // rest pressure (or NaN) -> reference
  if (bar < 0.0f) bar = 0.0f;                            // the model never asks below 0 bar
  long steps = lround((P_REF_BAR - bar) * STEPS_PER_BAR); // 0.424 bar -> 57.6 -> 58 steps
  if (steps > MAX_PULL_STEPS) steps = MAX_PULL_STEPS;
  return steps;
}

void runFromUsb() {
  const bool alive = glove.alive();   // false: no valid line for 200 ms

  for (byte i = 0; i < 3; i++) {
    const int8_t f = MOTOR_FINGER[i];
    if (f < 0 || !alive) {
      target[i] = 0;                                     // reference (no force)
    } else {
      target[i] = -stepsForPressure(glove.finger[f].dorsalBar);   // pull back = negative
    }
  }

  const unsigned long now = micros();
  for (byte i = 0; i < 3; i++) {
    if (pos[i] == target[i]) continue;
    if (now - lastStepUs[i] < STEP_INTERVAL_US) continue;

    const bool pullBack = target[i] < pos[i];
    // Never step INTO the home switch while pulling back.
    if (pullBack && digitalRead(LIMIT_PIN[i]) == LOW) continue;

    digitalWrite(DIR_PIN[i], pullBack ? HOME_DIR[i] : !HOME_DIR[i]);
    pulseStepNow(i);
    lastStepUs[i] = now;
    pos[i] += pullBack ? -1 : 1;
  }

  // Status for the relay console, twice a second.
  static unsigned long lastPrint = 0;
  if (millis() - lastPrint >= 500) {
    lastPrint = millis();
    Serial.print(alive ? F("RUN seq ") : F("RUN NO LINK seq "));
    Serial.print(glove.seq);
    Serial.print(F(" | M2 index "));
    Serial.print(glove.finger[GloveSerialLink::INDEX].dorsalBar, 3);
    Serial.print(F(" bar"));
    Serial.print(F(" target "));
    Serial.print(target[1]);
    Serial.print(F(" pos "));
    Serial.print(pos[1]);
    Serial.print(F(" | M3 thumb "));
    Serial.print(glove.finger[GloveSerialLink::THUMB].dorsalBar, 3);
    Serial.print(F(" bar"));
    Serial.print(F(" target "));
    Serial.print(target[2]);
    Serial.print(F(" pos "));
    Serial.println(pos[2]);
  }
}

// One step for one motor, blocking for the full step interval (homing only).
void pulseStep(byte motor) {
  digitalWrite(STEP_PIN[motor], HIGH);
  delayMicroseconds(STEP_PULSE_US);
  digitalWrite(STEP_PIN[motor], LOW);
  delayMicroseconds(STEP_INTERVAL_US - STEP_PULSE_US);
}

// One step pulse, without waiting out the interval (RUN mode times its own steps).
void pulseStepNow(byte motor) {
  digitalWrite(STEP_PIN[motor], HIGH);
  delayMicroseconds(STEP_PULSE_US);
  digitalWrite(STEP_PIN[motor], LOW);
}
