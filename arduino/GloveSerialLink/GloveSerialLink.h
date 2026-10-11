#pragma once
// ---------------------------------------------------------------------------
// GloveSerialLink -- receives the glove's force targets from the PC relay
// (tools/glove_serial_bridge.py) over USB serial. Drop this file next to your
// sketch's .ino and #include "GloveSerialLink.h".
//
// One text line per update, 50 per second, at 115200 baud:
//
//   G,<seq>,<thP>,<thU>,<thV>,<thT>,<ixP>,<ixU>,<ixV>,<ixT>*<cs>\n
//
//   per finger (th = thumb, ix = index):
//     P  pressing force on the fingertip pad, N (>= 0)
//     U  tilt along the pad's u axis, N    (in-plane part of the force;
//     V  tilt along the pad's v axis, N     angle = atan2(sqrt(U^2+V^2), P))
//     T  dorsal EXTENSION torque about the MCP joint, N*m (>= 0);
//        dorsal actuator force = T / moment arm
//   cs = XOR of every character before '*', as two hex digits
//
// Values are already scaled in Unity to the hardware (_padForceGain, _dorsalGain).
// When Unity goes quiet the relay itself sends zeros. If the RELAY dies, nothing
// arrives at all -- so check alive() and go to rest when it is false.
//
// Usage:
//   GloveSerialLink glove;
//   void setup() { Serial.begin(115200); }
//   void loop() {
//     glove.poll(Serial);                       // non-blocking, call every loop
//     if (!glove.alive()) { /* go to rest: no force on any actuator */ }
//     float pressThumb  = glove.finger[0].pressN;
//     float torqueIndex = glove.finger[1].torqueNm;
//     ...
//   }
// Anything else your sketch prints on Serial still shows up in the relay's console.
// ---------------------------------------------------------------------------
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

struct GloveFingerTarget {
  float pressN;     // pressing force on the pad, N
  float tiltU;      // N
  float tiltV;      // N
  float torqueNm;   // dorsal extension torque, N*m
};

class GloveSerialLink {
 public:
  static const uint8_t THUMB = 0;
  static const uint8_t INDEX = 1;

  GloveFingerTarget finger[2] = {};   // latest valid targets (zeros until the first line)
  uint32_t seq = 0;                   // Unity's sequence number of that line
  uint32_t goodLines = 0;             // counters, for debugging the link
  uint32_t badLines = 0;

  // Feed one received character. Returns true when it completed a VALID line,
  // which updates finger[], seq and the alive() timer. Pure C++ (no Arduino
  // calls), so it can be tested on a PC.
  bool feed(char c, uint32_t nowMs)
  {
    if (c == '\r') return false;
    if (c != '\n') {
      if (len_ < sizeof(buf_) - 1) buf_[len_++] = c;
      else overflow_ = true;                       // too long: drop this line
      return false;
    }
    buf_[len_] = 0;
    const bool ok = !overflow_ && parse(buf_);
    len_ = 0;
    overflow_ = false;
    if (ok) { lastMs_ = nowMs; haveAny_ = true; goodLines++; }
    else if (buf_[0] == 'G') badLines++;           // other text is not ours: ignore quietly
    return ok;
  }

  // True if a valid line arrived within timeoutMs. The relay sends 50 per second,
  // so 200 ms is ~10 lost lines.
  bool alive(uint32_t nowMs, uint32_t timeoutMs = 200) const
  {
    return haveAny_ && (uint32_t)(nowMs - lastMs_) < timeoutMs;
  }

#ifdef ARDUINO
  // Read everything waiting on the port. Call every loop(); never blocks.
  // Returns true if at least one new valid line arrived.
  bool poll(Stream& s)
  {
    bool any = false;
    while (s.available() > 0) any |= feed((char)s.read(), millis());
    return any;
  }
  bool alive(uint32_t timeoutMs = 200) const { return alive(millis(), timeoutMs); }
#endif

 private:
  char     buf_[112];
  uint8_t  len_ = 0;
  bool     overflow_ = false;
  bool     haveAny_ = false;
  uint32_t lastMs_ = 0;

  static int hexVal(char h)
  {
    if (h >= '0' && h <= '9') return h - '0';
    if (h >= 'A' && h <= 'F') return h - 'A' + 10;
    if (h >= 'a' && h <= 'f') return h - 'a' + 10;
    return -1;
  }

  // Valid only if: starts with "G,", has "*hh" with a matching XOR checksum, and
  // has exactly 9 fields after the G, all numbers.
  bool parse(char* line)
  {
    if (line[0] != 'G' || line[1] != ',') return false;
    char* star = strchr(line, '*');
    if (!star || hexVal(star[1]) < 0 || hexVal(star[2]) < 0 || star[3] != 0) return false;
    uint8_t cs = 0;
    for (char* p = line; p < star; p++) cs ^= (uint8_t)*p;
    if (cs != (uint8_t)(hexVal(star[1]) * 16 + hexVal(star[2]))) return false;
    *star = 0;

    float v[8];
    uint32_t s = 0;
    char* p = line + 2;
    for (int i = 0; i < 9; i++) {
      char* end;
      if (i == 0) s = strtoul(p, &end, 10);
      else v[i - 1] = (float)strtod(p, &end);
      if (end == p) return false;                  // empty or not a number
      if (i < 8 && *end != ',') return false;      // wrong field count
      if (i == 8 && *end != 0) return false;
      p = end + 1;
    }
    for (int i = 0; i < 8; i++)
      if (!(v[i] == v[i]) || v[i] > 1e6f || v[i] < -1e6f) return false;   // NaN / absurd

    for (int f = 0; f < 2; f++) {
      finger[f].pressN   = v[4 * f + 0] > 0 ? v[4 * f + 0] : 0;
      finger[f].tiltU    = v[4 * f + 1];
      finger[f].tiltV    = v[4 * f + 2];
      finger[f].torqueNm = v[4 * f + 3] > 0 ? v[4 * f + 3] : 0;
    }
    seq = s;
    return true;
  }
};
