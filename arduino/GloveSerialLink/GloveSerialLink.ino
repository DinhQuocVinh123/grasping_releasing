// ---------------------------------------------------------------------------
// Link test for the Arduino Mega: no motors, nothing but the serial link.
// Flash it, start the PC relay, pinch in Unity, and watch the relay console:
// it prints what this board received. The LED is on while the link is alive.
//
// To use in your own sketch: copy GloveSerialLink.h next to your .ino, then
// copy the three marked lines below.
// ---------------------------------------------------------------------------
#include "GloveSerialLink.h"

GloveSerialLink glove;                       // <-- 1

void setup()
{
  Serial.begin(115200);                      // must match the relay's --baud
  pinMode(LED_BUILTIN, OUTPUT);
  Serial.println(F("link test ready"));
}

void loop()
{
  glove.poll(Serial);                        // <-- 2: every loop, never blocks

  const bool alive = glove.alive();          // <-- 3: false = no valid line for 200 ms
  digitalWrite(LED_BUILTIN, alive ? HIGH : LOW);

  // In your sketch: if (!alive) go to rest -- no force on any actuator.
  // Otherwise feed glove.finger[0] (thumb) and glove.finger[1] (index) into your
  // force-to-pressure code:
  //   .pressN    pressing force on the pad (N)
  //   .tiltU/V   in-plane part of the force (N)
  //   .torqueNm  dorsal extension torque (N*m); dorsal force = torqueNm / moment arm

  static uint32_t lastPrint = 0;
  if (millis() - lastPrint >= 200) {         // echo back 5x per second
    lastPrint = millis();
    Serial.print(alive ? F("alive") : F("NO LINK"));
    Serial.print(F(" seq ")); Serial.print(glove.seq);
    for (uint8_t f = 0; f < 2; f++) {
      Serial.print(f == 0 ? F(" | thumb P ") : F(" | index P "));
      Serial.print(glove.finger[f].pressN, 3);
      Serial.print(F(" U ")); Serial.print(glove.finger[f].tiltU, 3);
      Serial.print(F(" V ")); Serial.print(glove.finger[f].tiltV, 3);
      Serial.print(F(" T ")); Serial.print(glove.finger[f].torqueNm, 4);
    }
    Serial.print(F(" | good ")); Serial.print(glove.goodLines);
    Serial.print(F(" bad ")); Serial.println(glove.badLines);
  }
}
