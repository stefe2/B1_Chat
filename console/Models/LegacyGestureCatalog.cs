namespace b1_chat_console.Models;

/// <summary>
/// Transitional name table for the 18 firmware gestures that remain usable by
/// the Sequencer while Gesture Catalog V2 is designed. This is deliberately
/// not the V2 catalog or a persistence contract.
/// </summary>
public static class LegacyGestureCatalog
{
    public static IReadOnlyList<string> Names { get; } = Array.AsReadOnly(new[]
    {
        "IDLE", "LOOK_AROUND", "NOD_YES", "SHAKE_NO", "CURIOUS_TILT", "SCAN_SLOW",
        "ALERT_SNAP", "TRACK", "GLITCH_STUTTER", "CONFUSED_TILT", "DOUBLE_TAKE",
        "SLEEPY_DROOP", "TARGET_LOCK", "WHIRR_SEARCH", "SIGNAL_GLITCH",
        "GREETING_NOD", "POWER_DOWN", "TALK",
    });
}
