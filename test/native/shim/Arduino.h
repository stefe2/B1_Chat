#pragma once

// Minimal host-side stand-in for <Arduino.h>, used only by env:native so
// ServoEngine/MotionEngine's pure trajectory logic can be unit tested without
// ESP32 hardware. It provides exactly the surface servo_engine.cpp and
// motion_engine.cpp actually touch: fixed-width types (via <cstdint>), a
// controllable millis() clock, constrain()/max()/min(), and a recording fake
// of the three native LEDC calls so a test can assert on the exact PWM output
// that would have reached a real servo.

#include <cstdint>
#include <cmath>
#include <vector>

using std::ceilf;
using std::fabsf;

inline int constrain(int x, int lo, int hi) { return x < lo ? lo : (x > hi ? hi : x); }
inline uint32_t max(uint32_t a, uint32_t b) { return a > b ? a : b; }
inline uint32_t min(uint32_t a, uint32_t b) { return a < b ? a : b; }

// Virtual clock: tests advance it explicitly instead of sleeping, so
// interpolation timing is deterministic and instant to run.
namespace native_clock {
inline uint32_t& value() {
    static uint32_t t = 0;
    return t;
}
}  // namespace native_clock
inline uint32_t millis() { return native_clock::value(); }

inline uint32_t esp_random() { return 0; }

// Records every native LEDC call in place of real hardware.
namespace native_pwm {
struct Write {
    uint8_t pin;
    uint32_t duty;
};
inline std::vector<Write>& writes() {
    static std::vector<Write> w;
    return w;
}
inline std::vector<uint8_t>& attaches() {
    static std::vector<uint8_t> a;
    return a;
}
inline std::vector<uint8_t>& detaches() {
    static std::vector<uint8_t> d;
    return d;
}
inline void reset() {
    writes().clear();
    attaches().clear();
    detaches().clear();
}
}  // namespace native_pwm

inline bool ledcAttach(uint8_t pin, uint32_t freq, uint8_t bits) {
    (void)freq;
    (void)bits;
    native_pwm::attaches().push_back(pin);
    return true;
}
inline void ledcDetach(uint8_t pin) { native_pwm::detaches().push_back(pin); }
inline void ledcWrite(uint8_t pin, uint32_t duty) { native_pwm::writes().push_back({pin, duty}); }
