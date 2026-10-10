// PC tests of the Arduino-side parser, arduino/GloveSerialLink/GloveSerialLink.h.
//   g++ -std=c++17 -Wall -Wextra -I../arduino/GloveSerialLink test_link.cpp -o test_link
//   ./test_link              unit tests
//   ./test_link fake <log>   behave like an Arduino running the link sketch on stdin/stdout:
//                            parse every line, log what was understood, echo status back
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <sys/select.h>
#include <sys/time.h>
#include <unistd.h>

#include "GloveSerialLink.h"

static int g_pass = 0, g_fail = 0;
#define CHECK(c, ...) do { if (c) ++g_pass; else { ++g_fail; std::printf("FAIL %d: ", __LINE__); std::printf(__VA_ARGS__); std::printf("\n"); } } while (0)

static std::string withChecksum(const std::string& body)
{
  unsigned cs = 0;
  for (char c : body) cs ^= (unsigned char)c;
  char tail[8];
  std::snprintf(tail, sizeof tail, "*%02X\n", cs);
  return body + tail;
}

static int feedAll(GloveSerialLink& g, const std::string& s, uint32_t t)
{
  int ok = 0;
  for (char c : s) ok += g.feed(c, t);
  return ok;
}

static void unitTests()
{
  GloveSerialLink g;
  CHECK(!g.alive(0), "not alive before any line");

  // The exact line the relay produces (glove_serial_bridge.make_line).
  const std::string good = withChecksum("G,42,0.7516,0.2736,0.0000,0.01235,1.5000,-0.1000,0.2000,0.09000");
  CHECK(feedAll(g, good, 1000) == 1, "valid line rejected");
  CHECK(g.seq == 42, "seq");
  CHECK(std::fabs(g.finger[0].pressN - 0.7516f) < 1e-5f && std::fabs(g.finger[0].tiltU - 0.2736f) < 1e-5f &&
        g.finger[0].tiltV == 0.0f && std::fabs(g.finger[0].torqueNm - 0.01235f) < 1e-6f, "thumb values");
  CHECK(std::fabs(g.finger[1].pressN - 1.5f) < 1e-5f && std::fabs(g.finger[1].tiltU + 0.1f) < 1e-5f &&
        std::fabs(g.finger[1].tiltV - 0.2f) < 1e-5f && std::fabs(g.finger[1].torqueNm - 0.09f) < 1e-6f, "index values");
  CHECK(g.alive(1150) && !g.alive(1250), "alive window is 200 ms");

  // Windows line ending, and text from elsewhere on the port, are tolerated.
  std::string crlf = good;
  crlf.insert(crlf.size() - 1, "\r");
  CHECK(feedAll(g, crlf, 2000) == 1, "CRLF line rejected");
  CHECK(feedAll(g, "hello there\n", 2000) == 0 && g.badLines == 0, "plain text must be ignored quietly");

  // Everything below must be rejected AND leave the last good values alone.
  const uint32_t goodBefore = g.goodLines;
  const char* bad[] = {
    "G,42,0.7516,0.2736,0.0000,0.01235,1.5000,-0.1000,0.2000,0.09000*00\n",   // wrong checksum
    "G,42,0.7516,0.2736,0.0000,0.01235,1.5000,-0.1000,0.2000,0.09000\n",      // no checksum
  };
  for (const char* b : bad) CHECK(feedAll(g, b, 3000) == 0, "accepted: %s", b);
  CHECK(feedAll(g, withChecksum("G,42,0.75,0.27,0.0,0.01,1.5,-0.1,0.2"), 3000) == 0, "8 fields accepted");
  CHECK(feedAll(g, withChecksum("G,42,0.75,0.27,0.0,0.01,1.5,-0.1,0.2,0.09,7"), 3000) == 0, "10 fields accepted");
  CHECK(feedAll(g, withChecksum("G,42,0.75,abc,0.0,0.01,1.5,-0.1,0.2,0.09"), 3000) == 0, "non-number accepted");
  CHECK(feedAll(g, withChecksum("G,42,0.75,,0.0,0.01,1.5,-0.1,0.2,0.09"), 3000) == 0, "empty field accepted");
  CHECK(feedAll(g, withChecksum("G,42,nan,0,0,0,0,0,0,0"), 3000) == 0, "NaN accepted");
  CHECK(feedAll(g, std::string(300, 'G') + "\n", 3000) == 0, "overlong line accepted");
  CHECK(g.goodLines == goodBefore && std::fabs(g.finger[1].pressN - 1.5f) < 1e-5f, "a bad line changed the values");
  CHECK(feedAll(g, good, 3000) == 1, "parser did not recover after bad lines");

  // A line cut in half by a reset is dropped, the next one is fine.
  CHECK(feedAll(g, "G,43,0.75,0.2", 4000) == 0 && feedAll(g, "\n", 4000) == 0, "half line accepted");
  CHECK(feedAll(g, good, 4000) == 1, "line after a half line rejected");

  // Negative force or torque is clamped to zero (the relay never sends one, but still).
  CHECK(feedAll(g, withChecksum("G,44,-1.0,0,0,-0.5,0,0,0,0"), 5000) == 1 &&
        g.finger[0].pressN == 0.0f && g.finger[0].torqueNm == 0.0f, "negatives not clamped");
}

static uint32_t nowMs()
{
  timeval tv;
  gettimeofday(&tv, nullptr);
  return (uint32_t)(tv.tv_sec * 1000ULL + tv.tv_usec / 1000);
}

// Stand-in for the Mega running GloveSerialLink.ino, on stdin/stdout (a pty).
static int fake(const char* logPath)
{
  FILE* log = std::fopen(logPath, "w");
  std::fprintf(log, "ms,seq,thP,thU,thV,thT,ixP,ixU,ixV,ixT\n");
  GloveSerialLink g;
  const uint32_t t0 = nowMs();
  uint32_t lastEcho = 0;
  for (;;) {
    fd_set rd;
    FD_ZERO(&rd);
    FD_SET(0, &rd);
    timeval tv = { 0, 5000 };
    if (select(1, &rd, nullptr, nullptr, &tv) > 0) {
      char buf[256];
      const ssize_t n = read(0, buf, sizeof buf);
      if (n <= 0) break;
      for (ssize_t i = 0; i < n; i++)
        if (g.feed(buf[i], nowMs())) {
          std::fprintf(log, "%u,%u", nowMs() - t0, g.seq);
          for (auto& f : g.finger) std::fprintf(log, ",%.5f,%.5f,%.5f,%.6f", f.pressN, f.tiltU, f.tiltV, f.torqueNm);
          std::fprintf(log, "\n");
          std::fflush(log);
        }
    }
    if (nowMs() - lastEcho >= 200) {
      lastEcho = nowMs();
      char line[128];
      const int k = std::snprintf(line, sizeof line, "%s seq %u good %u bad %u\r\n",
                                  g.alive(nowMs()) ? "alive" : "NO LINK", g.seq, g.goodLines, g.badLines);
      if (write(1, line, k) < 0) break;
    }
  }
  std::fclose(log);
  return 0;
}

int main(int argc, char** argv)
{
  if (argc == 3 && std::string(argv[1]) == "fake") return fake(argv[2]);
  unitTests();
  std::printf("%d passed, %d failed\n", g_pass, g_fail);
  return g_fail ? 1 : 0;
}
