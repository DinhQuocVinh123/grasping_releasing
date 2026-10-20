// Host simulation of glove_mega_steppers.ino: fake pins, clock, serial.
#define ARDUINO 100
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <cmath>
#include <string>
#include <deque>
typedef uint8_t byte;
#define HIGH 1
#define LOW 0
#define INPUT_PULLUP 2
#define OUTPUT 1
#define F(x) x
typedef const char __FlashStringHelper;
static uint64_t g_us = 0;
unsigned long micros() { g_us += 4; return (unsigned long)g_us; }
unsigned long millis() { return (unsigned long)(g_us / 1000); }
void delayMicroseconds(unsigned us) { g_us += us; }
void delay(unsigned long ms) { g_us += ms * 1000ULL; }
// --- motors: each has a physical position; home switch at phys <= 0
static int dirPin[3] = {9, 11, 13}, stepPin[3] = {8, 10, 12};
static int pinState[64];
static long phys[3] = {300, 250, 400};       // start somewhere away from home
static bool buttons[8];                       // pins 5,6,7 pressed?
void pinMode(int, int) {}
int digitalRead(int p) {
  if (p >= 2 && p <= 4) return phys[p - 2] <= 0 ? LOW : HIGH;
  if (p >= 5 && p <= 7) return buttons[p] ? LOW : HIGH;
  return HIGH;
}
void digitalWrite(int p, int v) {
  for (int i = 0; i < 3; i++)
    if (p == stepPin[i] && v == HIGH && pinState[p] == LOW)
      phys[i] += (pinState[dirPin[i]] == LOW) ? -1 : 1;   // HOME_DIR = LOW = toward home
  pinState[p] = v;
}
long lround(float f) { return std::lround((double)f); }
struct Stream {
  std::deque<char> rx; std::string tx;
  int available() { return (int)rx.size(); }
  int read() { char c = rx.front(); rx.pop_front(); return (unsigned char)c; }
  void begin(long) {}
  template <class T> void print(T v) { tx += std::to_string(v); }
  void print(const char* s) { tx += s; }
  void print(float v, int d) { char b[32]; snprintf(b, 32, "%.*f", d, v); tx += b; }
  template <class T> void println(T v) { print(v); tx += "\n"; }
  void println(const char* s) { print(s); tx += "\n"; }
} Serial;
#include "../../arduino/glove_mega_steppers/glove_mega_steppers.ino"

static unsigned seqN = 0;
static void sendLine(float thBar, float ixBar) {
  char body[160];
  snprintf(body, sizeof body, "H,%u,0.5000,0.0000,0.0000,%.4f,0.5000,0.0000,0.0000,%.4f", seqN++, thBar, ixBar);
  unsigned cs = 0; for (char* c = body; *c; c++) cs ^= (unsigned char)*c;
  char line[200]; snprintf(line, sizeof line, "%s*%02X\n", body, cs);
  for (char* c = line; *c; c++) Serial.rx.push_back(*c);
}
// run loop() for `ms` of simulated time, sending a line every 20 ms
static void runFor(unsigned ms, float thT, float ixT, bool send = true) {
  uint64_t end = g_us + ms * 1000ULL, nextLine = g_us;
  while (g_us < end) {
    if (send && g_us >= nextLine) { sendLine(thT, ixT); nextLine += 20000; }
    loop();
    g_us += 20;   // a real loop() pass takes some microseconds
  }
}
static void press(int pin) { buttons[pin] = true; loop(); runFor(30, 0, 0, false); buttons[pin] = false; loop(); }
static int fails = 0;
static void check(bool c, const char* m) { printf("%s %s\n", c ? "ok  " : "FAIL", m); if (!c) fails++; }

int main() {
  setup();
  press(6);                                  // RUN before HOME -> refused
  check(!running, "RUN refused before homing");
  press(5);                                  // HOME (blocking inside loop)
  check(homed, "homed");
  check(phys[0] == 140 && phys[1] == 140 && phys[2] == 140, "all motors 140 steps forward of the home switch");
  check(pos[1] == 0 && pos[2] == 0, "positions zeroed at the reference");
  press(6);
  check(running, "RUN started after homing");

  const float REST = 1.0f;
  runFor(500, REST, 0.3402f);                // index 1.03 N at L 70 -> 0.3402 bar (model), thumb at rest
  printf("     M2 pos %ld (phys %ld), M3 pos %ld\n", pos[1], phys[1], pos[2]);
  check(pos[1] == -66 && phys[1] == 140 - 66, "index 0.3402 bar -> (1 - 0.3402) x 100 = 66 steps back");
  check(pos[2] == 0 && phys[2] == 140, "thumb at rest pressure -> M3 stays at reference");
  check(phys[0] == 140, "M1 untouched");

  runFor(500, 0.0f, 0.5f);                   // thumb full force (0 bar) -> 100, index eases to 0.5 bar -> 50
  check(pos[2] == -100 && phys[2] == 40, "thumb 0 bar (full force) -> M3 at -100");
  check(pos[1] == -50 && phys[1] == 90, "index rises to 0.5 bar -> M2 comes forward to -50");

  runFor(500, REST, 1.3f);                   // rest, and above rest -> reference
  check(pos[1] == 0 && pos[2] == 0 && phys[1] == 140 && phys[2] == 140, "rest (or above) -> both back to reference");

  runFor(500, -2.0f, -2.0f);                 // nonsense below 0 bar -> treated as 0 bar
  check(pos[1] == -100 && pos[2] == -100, "below 0 bar clamped to 0 bar = 100 steps (never past the 130 guard)");

  runFor(150, REST, 0.0f);
  runFor(600, REST, 0.0f, false);            // relay dies
  check(pos[1] == 0 && pos[2] == 0, "no valid line for 200 ms -> back to reference");

  runFor(200, REST, 0.3402f);
  long before = pos[1];
  buttons[7] = true; runFor(200, REST, 0.0f); buttons[7] = false; loop();
  check(!running && pos[1] == before, "SW6 STOP: motion stops, position held");
  runFor(200, REST, 0.0f);
  check(pos[1] == before, "after STOP it stays stopped until SW5");
  press(6);
  runFor(500, REST, 0.3402f);
  check(running && pos[1] == -66 && phys[1] == 74, "SW5 again resumes from the tracked position");

  // speed: 500 steps/s max per motor (2 ms interval)
  runFor(0, 0, 0, false);
  uint64_t t0 = g_us; runFor(60, REST, REST);
  printf("     after 60 ms heading back to reference, M2 pos %ld\n", pos[1]);
  check(pos[1] >= -66 + 25 && pos[1] <= -66 + 31, "step rate ~1 step / 2 ms while streaming");
  (void)t0;
  printf("     parser: %lu good lines, %lu bad\n", (unsigned long)glove.goodLines, (unsigned long)glove.badLines);
  check(glove.badLines == 0 && glove.seq == seqN - 1, "every line accepted, in order, none mixed up");
  printf("\n--- Arduino prints (first lines) ---\n%.700s\n", Serial.tx.c_str());
  printf("%s\n", fails ? "FAILED" : "ALL PASSED");
  return fails ? 1 : 0;
}
