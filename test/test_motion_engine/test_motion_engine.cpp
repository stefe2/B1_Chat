// Pure trajectory tests for MotionEngine — no ESP32 board involved (env:native,
// see test/native/shim/Arduino.h). Covers the Stage 5 exit-gate composition
// contract from docs/GESTURE-CATALOG-SCHEMA-V1.md: a persistent per-axis base
// pose plus an expression overlay per axis, composed into one bounded target,
// with deterministic interruption/end policy per gesture.

#include <unity.h>

#include "config.h"
#include "motion_engine.h"

namespace {

uint32_t dutyForAngle(float angleDeg) {
    const float span = SERVO_MAX_US - SERVO_MIN_US;
    const float us = SERVO_MIN_US + (angleDeg / 180.0f) * span;
    return (uint32_t)((us * 65535.0f) / 20000.0f);
}

float angleForPct(int pct, uint8_t lo, uint8_t center, uint8_t hi) {
    const float span = pct < 0 ? (float)(center - lo) : (float)(hi - center);
    return center + span * pct / 100.0f;
}

uint32_t lastPanDuty() { return native_pwm::writes()[native_pwm::writes().size() - 2].duty; }
uint32_t lastTiltDuty() { return native_pwm::writes().back().duty; }

struct Rig {
    ServoEngine s;
    MotionEngine m;
};

// Advances the virtual clock in SERVO_UPDATE_HZ-sized steps, pumping both the
// gesture scheduler and the physical interpolator each time, until totalMs
// has (at least) elapsed.
void pump(Rig& r, uint32_t totalMs) {
    const uint32_t stepMs = 1000 / SERVO_UPDATE_HZ;
    for (uint32_t elapsed = 0; elapsed <= totalMs; elapsed += stepMs) {
        native_clock::value() += stepMs;
        r.m.update();
        r.s.update();
    }
}

Rig fresh() {
    native_clock::value() = 0;
    native_pwm::reset();
    Rig r;
    r.s.begin();
    r.m.begin(&r.s);
    r.s.setEnabled(true);
    native_pwm::reset();  // drop the initial center write; tests start clean
    return r;
}

}  // namespace

void test_activeGesture_defaults_to_idle_center() {
    Rig r = fresh();
    TEST_ASSERT_EQUAL((int)GESTURE_IDLE_CENTER, (int)r.m.activeGesture());
}

void test_idle_center_clears_every_channel_synchronously() {
    Rig r = fresh();
    r.m.play(GESTURE_ATTENTION_LOOK_RIGHT, 0);
    pump(r, 500);
    r.m.play(GESTURE_COMMUNICATE_NOD, 0);
    pump(r, 300);
    TEST_ASSERT_TRUE(r.m.isPlaying());

    r.m.play(GESTURE_IDLE_CENTER, 0);
    TEST_ASSERT_FALSE_MESSAGE(r.m.isPlaying(), "idle.center must clear every channel synchronously, not on a future tick");

    pump(r, 250);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_PAN_CENTER), lastPanDuty());
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_TILT_CENTER), lastTiltDuty());
}

void test_pan_base_pose_holds_while_a_tilt_overlay_plays_and_clears() {
    Rig r = fresh();
    r.m.play(GESTURE_ATTENTION_LOOK_RIGHT, 0);
    pump(r, 450);  // nominal 400ms
    TEST_ASSERT_FALSE_MESSAGE(r.m.isPlaying(), "a finite base pose must end on its own and hold");
    const float panAngle = angleForPct(55, SERVO_PAN_MIN, SERVO_PAN_CENTER, SERVO_PAN_MAX);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(panAngle), lastPanDuty());

    r.m.play(GESTURE_COMMUNICATE_NOD, 0);
    pump(r, 180);  // partway through nod's first frame
    TEST_ASSERT_TRUE(r.m.isPlaying(GESTURE_COMMUNICATE_NOD));
    TEST_ASSERT_UINT32_WITHIN_MESSAGE(1, dutyForAngle(panAngle), lastPanDuty(),
                                       "PAN must stay exactly at the held base pose while a TILT overlay runs");

    pump(r, 700);  // nominal 800ms total for nod
    TEST_ASSERT_FALSE_MESSAGE(r.m.isPlaying(GESTURE_COMMUNICATE_NOD), "nod must end on its own");
    TEST_ASSERT_UINT32_WITHIN_MESSAGE(1, dutyForAngle(panAngle), lastPanDuty(),
                                       "PAN base pose must survive the overlay clearing");
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_TILT_CENTER), lastTiltDuty());
}

void test_dialogue_talk_loops_until_explicitly_stopped() {
    Rig r = fresh();
    r.m.play(GESTURE_DIALOGUE_TALK, 0);
    pump(r, 900);  // one full 750ms cycle plus margin, into a second lap
    TEST_ASSERT_TRUE_MESSAGE(r.m.isPlaying(GESTURE_DIALOGUE_TALK),
                              "a continuous gesture must keep looping on its own by design, "
                              "until something explicitly stops it");

    r.m.stopGesture(GESTURE_DIALOGUE_TALK);
    TEST_ASSERT_FALSE(r.m.isPlaying());
    pump(r, 200);
    TEST_ASSERT_UINT32_WITHIN(1, dutyForAngle(SERVO_TILT_CENTER), lastTiltDuty());
}

void test_same_channel_interruption_is_reported_and_replaces_cleanly() {
    Rig r = fresh();
    r.m.play(GESTURE_DIALOGUE_TALK, 0);
    pump(r, 50);

    GestureWireId interrupted = GESTURE_COUNT;
    const bool accepted = r.m.play(GESTURE_COMMUNICATE_NOD, 0, &interrupted);
    TEST_ASSERT_TRUE(accepted);
    TEST_ASSERT_EQUAL((int)GESTURE_DIALOGUE_TALK, (int)interrupted);
    TEST_ASSERT_FALSE(r.m.isPlaying(GESTURE_DIALOGUE_TALK));
    TEST_ASSERT_TRUE(r.m.isPlaying(GESTURE_COMMUNICATE_NOD));
}

void test_independent_axes_never_interrupt_each_other() {
    Rig r = fresh();
    r.m.play(GESTURE_ATTENTION_LOOK_RIGHT, 0);  // PAN base
    GestureWireId interrupted = GESTURE_COUNT;
    const bool accepted = r.m.play(GESTURE_DIALOGUE_TALK, 0, &interrupted);  // TILT overlay
    TEST_ASSERT_TRUE(accepted);
    TEST_ASSERT_EQUAL_MESSAGE((int)GESTURE_COUNT, (int)interrupted,
                               "a gesture on a different axis/layer must never report an interruption");
    TEST_ASSERT_TRUE(r.m.isPlaying(GESTURE_ATTENTION_LOOK_RIGHT));
    TEST_ASSERT_TRUE(r.m.isPlaying(GESTURE_DIALOGUE_TALK));
}

void test_activeGesture_prefers_the_overlay_over_the_base() {
    Rig r = fresh();
    r.m.play(GESTURE_ATTENTION_LOOK_RIGHT, 0);
    r.m.play(GESTURE_DIALOGUE_TALK, 0);
    TEST_ASSERT_EQUAL((int)GESTURE_DIALOGUE_TALK, (int)r.m.activeGesture());
}

void test_composed_gestures_never_trip_the_clip_flag() {
    // The current catalog always pre-clamps base+overlay to +/-100% before
    // handing it to ServoEngine, so this must stay false; if it ever flips
    // true, a newly added gesture is exceeding the composed range unnoticed.
    Rig r = fresh();
    r.m.play(GESTURE_ATTENTION_LOOK_RIGHT, 0);
    pump(r, 450);
    r.m.play(GESTURE_DIALOGUE_TALK, 0);
    pump(r, 900);
    TEST_ASSERT_FALSE(r.m.wasClipped());
}

int main(int argc, char** argv) {
    (void)argc;
    (void)argv;
    UNITY_BEGIN();
    RUN_TEST(test_activeGesture_defaults_to_idle_center);
    RUN_TEST(test_idle_center_clears_every_channel_synchronously);
    RUN_TEST(test_pan_base_pose_holds_while_a_tilt_overlay_plays_and_clears);
    RUN_TEST(test_dialogue_talk_loops_until_explicitly_stopped);
    RUN_TEST(test_same_channel_interruption_is_reported_and_replaces_cleanly);
    RUN_TEST(test_independent_axes_never_interrupt_each_other);
    RUN_TEST(test_activeGesture_prefers_the_overlay_over_the_base);
    RUN_TEST(test_composed_gestures_never_trip_the_clip_flag);
    return UNITY_END();
}
