namespace b1_chat_console.Models;

public sealed record FleetUpdateTarget(
    ushort DroidId,
    string Name,
    bool IsMaster,
    string CurrentVersion,
    string CurrentBuildId,
    string TargetVersion,
    string ExpectedBuildId)
{
    public string DroidIdHex => DroidId.ToString("X4");
    public string RoleLabel => IsMaster ? "MASTER" : "SLAVE";
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DroidIdHex : Name;
    public string CurrentIdentity => string.IsNullOrWhiteSpace(CurrentBuildId)
        ? $"v{CurrentVersion}"
        : $"v{CurrentVersion} · {CurrentBuildId}";
    public string TargetIdentity => string.IsNullOrWhiteSpace(ExpectedBuildId)
        ? $"v{TargetVersion}"
        : $"v{TargetVersion} · {ExpectedBuildId}";
}

public sealed record FleetUpdatePlan(
    string TargetVersion,
    FirmwareUpdateInfo Firmware,
    IReadOnlyList<FleetUpdateTarget> Targets,
    IReadOnlyList<string> Notices)
{
    public bool HasTargets => Targets.Count > 0;
}

/// <summary>
/// Produces a safe, immutable startup update plan from the current online roster.
/// Only semantic upgrades are automatic: unknown versions, newer development
/// versions and same-version custom builds are reported but never overwritten.
/// </summary>
public static class FleetUpdatePlanner
{
    public static FleetUpdatePlan Create(IEnumerable<Droid> droids, FirmwareUpdateInfo firmware)
    {
        var targetVersion = firmware.Latest ?? "";
        var targets = new List<FleetUpdateTarget>();
        var notices = new List<string>();
        if (!Version.TryParse(targetVersion, out var latest))
            return new FleetUpdatePlan(targetVersion, firmware, targets, notices);

        foreach (var droid in droids.Where(droid => droid.Online && (droid.IsMaster || droid.Adopted)))
        {
            var expectedBuild = droid.IsMaster ? firmware.BuildIdMaster : firmware.BuildIdSlave;
            if (!Version.TryParse(droid.FwVersion, out var installed))
            {
                notices.Add($"{droid.DisplayLabel}: firmware version unknown; manual review required.");
                continue;
            }

            if (installed > latest)
            {
                notices.Add($"{droid.DisplayLabel}: v{droid.FwVersion} is newer than published v{targetVersion}; no downgrade.");
                continue;
            }

            if (installed == latest)
            {
                if (!string.IsNullOrWhiteSpace(expectedBuild) &&
                    !string.IsNullOrWhiteSpace(droid.BuildId) &&
                    !string.Equals(droid.BuildId, expectedBuild, StringComparison.OrdinalIgnoreCase))
                    notices.Add($"{droid.DisplayLabel}: same version with a different/local build; left unchanged.");
                continue;
            }

            targets.Add(new FleetUpdateTarget(
                droid.Id,
                droid.Name,
                droid.IsMaster,
                droid.FwVersion,
                droid.BuildId,
                targetVersion,
                expectedBuild ?? ""));
        }

        // Slaves depend on the master for OTA transport, so the USB master update is always last.
        targets.Sort((left, right) =>
        {
            var role = left.IsMaster.CompareTo(right.IsMaster);
            return role != 0 ? role : left.DroidId.CompareTo(right.DroidId);
        });
        return new FleetUpdatePlan(targetVersion, firmware, targets, notices);
    }
}
