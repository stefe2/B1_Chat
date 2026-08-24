// Pure trajectory tests for ServoEngine — no ESP32 board involved (env:native,
// see test/native/shim/Arduino.h). Covers the Stage 5 exit-gate properties:
// ease-in/out shape, velocity/mechanical clamping, calibration semantics and
// the boot-recenter regression fixed in main.cpp (see KNOWN-PITFALLS.md).

#include <unity.h>

#include "config.h"
#include "servo_engine.h"

namespace {

// Mirrors ServoEngine's private usToDuty()/writeServos() formula so tests can
// predict the exact PWM duty a given angle must produce.
uint32_t dutyForAngle(float angleDeg) {
    const float span = SERVO_MAX_US - SERVO_MIN_US;
    const float us = SERVO_MIN_US + (angleDeg / 180.0f) * span;
    const float periodUs = 1000000.0f / SERVO_UPDATE_HZ == 0 ? 20000.0f : 20000.0f;  // 50 Hz period
    (void)periodUs;
    return (uint32_t)((us * 65535.0f) / 20000.0f);
}

// Mirrors ServoEngine::setTargetNormalized()'s angle-from-percentage formula.
float angleForPct(int pct, uint8_t lo, uint8_t center, uint8_t hi) {
    const float span = pct < 0 ? (float)(center - lo) : (float)(hi - center);
    return center + span * pct / 100.0f;
}

// Advances the virtual clock in SERVO_UPDATE_HZ-sized steps, pumping
// update() each time, until totalMs has elapsed.
void pump(ServoEngine& s, uint32_t totalMs) {
    const uint32_t stepMs = 1000 / SERVO_UPDATE_HZ;
    for (uint32_t elapsed = 0; elapsed <= totalMs; elapsed += stepMs) {
        native_clock::value() += stepMs;
        s.update();
    }
}

ServoEngine fresh() {
    native_clock::value() = 0;
    native_pwm::reset();
    ServoEngine s;
    s.begin();
    return s;
}

}  // namespace

void test_disabled_engine_emits_no_pwm() {
    ServoEngine s = fresh();
    s.update();
    TEST_ASSERT_TRUE_MESSAGE(native_pwm::writes().empty(),
                              "a virgin/disabled board must not emit PWM before setEnabled(true)");
}

void test_enable_attaches_both_pins_and_writes_default_center() {
    ServoEngine s = fresh();
    s.setEnabled(true);
    TEST_ASSERT_EQUAL_UINT32(2, native_pwm::attaches().size());
    TEST_ASSERT_EQUAL_UINT32(2, native_pwm::writes().size());
    for (const auto& w : native_pwm::writes())
        TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_PAN_CENTER), w.duty);
}

void test_interpolation_reaches_exact_target_and_stops() {
    ServoEngine s = fresh();
    s.setEnabled(true);
    s.setTarget(SERVO_PAN_MAX, SERVO_TILT_MAX, 400);
    TEST_ASSERT_TRUE(s.isMoving());
    pump(s, 500);
    TEST_ASSERT_FALSE_MESSAGE(s.isMoving(), "interpolation must end once its duration elapses");
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_PAN_MAX), native_pwm::writes()[native_pwm::writes().size() - 2].duty);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_TILT_MAX), native_pwm::writes().back().duty);
}

void test_move_never_exceeds_a_slower_than_ceiling_speed() {
    // A long-travel move requested with an unrealistically short duration must
    // be silently stretched to respect SERVO_MAX_DEGREES_PER_SECOND (180 deg/s)
    // rather than jump — the single physical safety bound every trajectory
    // must obey.
    ServoEngine s = fresh();
    s.setEnabled(true);
    native_pwm::reset();
    s.setTarget(SERVO_PAN_MAX, SERVO_TILT_CENTER, 1);  // travel 30deg, 1ms requested
    // 30 degrees at 180 deg/s takes >= ~167ms: after only 50ms it must still
    // be under way and far short of the target duty.
    pump(s, 50);
    TEST_ASSERT_TRUE_MESSAGE(s.isMoving(), "a 30-degree move must not complete in 50ms");
    const uint32_t targetDuty = dutyForAngle(SERVO_PAN_MAX);
    const uint32_t centerDuty = dutyForAngle(SERVO_PAN_CENTER);
    const uint32_t currentDuty = native_pwm::writes()[native_pwm::writes().size() - 2].duty;  // PAN, not TILT
    TEST_ASSERT_TRUE_MESSAGE(currentDuty < targetDuty && currentDuty > centerDuty,
                              "the move must still be in flight, not clamped straight to target");
}

void test_easing_is_slower_at_the_start_than_mid_move() {
    // smootherstep has zero derivative at t=0: the first tick of a move must
    // advance far less than a tick of equal size taken mid-trajectory.
    ServoEngine s = fresh();
    s.setEnabled(true);
    s.setTarget(SERVO_PAN_MAX, SERVO_TILT_CENTER, 400);
    native_pwm::reset();
    const uint32_t stepMs = 1000 / SERVO_UPDATE_HZ;

    // Both writes below index PAN (size()-2), the axis actually in motion —
    // TILT stays at its unchanged target and would make a useless probe.
    native_clock::value() += stepMs;
    s.update();
    const uint32_t afterFirstStep = native_pwm::writes()[native_pwm::writes().size() - 2].duty;
    const uint32_t startDuty = dutyForAngle(SERVO_PAN_CENTER);
    const uint32_t firstStepDelta = afterFirstStep - startDuty;

    // Walk to just past the midpoint (t ~= 0.5).
    native_clock::value() = 200 - stepMs;
    native_pwm::reset();
    native_clock::value() += stepMs;
    s.update();
    const uint32_t beforeMidDuty = native_pwm::writes()[native_pwm::writes().size() - 2].duty;
    native_clock::value() += stepMs;
    s.update();
    const uint32_t midStepDelta = native_pwm::writes()[native_pwm::writes().size() - 2].duty - beforeMidDuty;

    TEST_ASSERT_TRUE_MESSAGE(firstStepDelta < midStepDelta,
                              "the very first step of a move must be smaller than a mid-move step "
                              "(smootherstep ease-in), not linear or ease-out");
}

void test_setTargetNormalized_clamps_and_reports_clipping() {
    ServoEngine s = fresh();
    s.setEnabled(true);
    const bool clipped = s.setTargetNormalized(127, -127, 400);
    TEST_ASSERT_TRUE_MESSAGE(clipped, "out-of-range +/-100 percentages must report clipping");
    pump(s, 500);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_PAN_MAX), native_pwm::writes()[native_pwm::writes().size() - 2].duty);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_TILT_MIN), native_pwm::writes().back().duty);
}

void test_setTargetNormalized_in_range_reports_no_clipping() {
    ServoEngine s = fresh();
    s.setEnabled(true);
    const bool clipped = s.setTargetNormalized(50, -50, 400);
    TEST_ASSERT_FALSE(clipped);
    pump(s, 500);
    const float expectedPan = angleForPct(50, SERVO_PAN_MIN, SERVO_PAN_CENTER, SERVO_PAN_MAX);
    const float expectedTilt = angleForPct(-50, SERVO_TILT_MIN, SERVO_TILT_CENTER, SERVO_TILT_MAX);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(expectedPan), native_pwm::writes()[native_pwm::writes().size() - 2].duty);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(expectedTilt), native_pwm::writes().back().duty);
}

void test_setLimits_alone_does_not_move_the_physical_position() {
    // Regression guard for the exact defect fixed in main.cpp: setLimits()
    // recalibrates the range/center bookkeeping only. Without an explicit
    // center()/setTarget() afterward, the servo must keep sitting exactly
    // where it physically already was.
    ServoEngine s = fresh();
    s.setEnabled(true);
    pump(s, 10);
    native_pwm::reset();

    s.setLimits(10, 40, 70, 50, 80, 110);  // a very different calibration
    s.update();
    TEST_ASSERT_TRUE_MESSAGE(native_pwm::writes().empty() ||
                                  native_pwm::writes().back().duty == dutyForAngle(SERVO_PAN_CENTER),
                              "setLimits() must not move the servo by itself");

    s.center();
    pump(s, 900);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(40), native_pwm::writes()[native_pwm::writes().size() - 2].duty);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(80), native_pwm::writes().back().duty);
}

void test_reversed_axis_mirrors_around_the_unchanged_center() {
    ServoEngine s = fresh();
    s.setReversed(true, false);
    s.setEnabled(true);
    native_pwm::reset();

    // Center must stay physically at the center pulse even when reversed.
    s.center();
    pump(s, 900);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_PAN_CENTER), native_pwm::writes()[native_pwm::writes().size() - 2].duty);

    // Commanding the logical max must produce the MIN pulse once reversed.
    s.setTarget(SERVO_PAN_MAX, SERVO_TILT_CENTER, 400);
    pump(s, 500);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_PAN_MIN), native_pwm::writes()[native_pwm::writes().size() - 2].duty);
}

void test_out_of_range_raw_degrees_are_clamped_to_the_mechanical_limit() {
    ServoEngine s = fresh();
    s.setEnabled(true);
    s.setTarget(999.0f, -999.0f, 400);
    pump(s, 500);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_PAN_MAX), native_pwm::writes()[native_pwm::writes().size() - 2].duty);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_TILT_MIN), native_pwm::writes().back().duty);
}

void test_disabling_stops_further_pwm_writes() {
    ServoEngine s = fresh();
    s.setEnabled(true);
    pump(s, 10);
    s.setEnabled(false);
    TEST_ASSERT_EQUAL_UINT32(2, native_pwm::detaches().size());
    native_pwm::reset();
    pump(s, 100);
    TEST_ASSERT_TRUE_MESSAGE(native_pwm::writes().empty(), "a disabled engine must never write PWM");
}

int main(int argc, char** argv) {
    (void)argc;
    (void)argv;
    UNITY_BEGIN();
    RUN_TEST(test_disabled_engine_emits_no_pwm);
    RUN_TEST(test_enable_attaches_both_pins_and_writes_default_center);
    RUN_TEST(test_interpolation_reaches_exact_target_and_stops);
    RUN_TEST(test_move_never_exceeds_a_slower_than_ceiling_speed);
    RUN_TEST(test_easing_is_slower_at_the_start_than_mid_move);
    RUN_TEST(test_setTargetNormalized_clamps_and_reports_clipping);
    RUN_TEST(test_setTargetNormalized_in_range_reports_no_clipping);
    RUN_TEST(test_setLimits_alone_does_not_move_the_physical_position);
    RUN_TEST(test_reversed_axis_mirrors_around_the_unchanged_center);
    RUN_TEST(test_out_of_range_raw_degrees_are_clamped_to_the_mechanical_limit);
    RUN_TEST(test_disabling_stops_further_pwm_writes);
    return UNITY_END();
}
