using b1_chat_console.Models;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Tests;

public class FleetUpdatePlannerTests
{
    private static FirmwareUpdateInfo Release() => new()
    {
        Latest = "1.11.0",
        UrlMaster = "https://example.test/master.bin",
        UrlSlave = "https://example.test/slave.bin",
        Sha256Master = new string('a', 64),
        Sha256Slave = new string('b', 64),
        BuildIdMaster = "AAAABBBB",
        BuildIdSlave = "CCCCDDDD",
    };

    [Fact]
    public void Create_IncludesOnlyOlderOnlineAdoptedDroids_AndPutsMasterLast()
    {
        var droids = new[]
        {
            Droid(0x1001, "Master", master: true, online: true, adopted: true, version: "1.10.0", build: "OLDMASTER"),
            Droid(0x1002, "Slave B", master: false, online: true, adopted: true, version: "1.9.0", build: "OLDSLAVE2"),
            Droid(0x1003, "Offline", master: false, online: false, adopted: true, version: "1.9.0", build: "OLD"),
            Droid(0x1004, "Pending", master: false, online: true, adopted: false, version: "1.9.0", build: "OLD"),
            Droid(0x1005, "Slave A", master: false, online: true, adopted: true, version: "1.10.5", build: "OLDSLAVE1"),
        };

        var plan = FleetUpdatePlanner.Create(droids, Release());

        Assert.Equal(new ushort[] { 0x1002, 0x1005, 0x1001 }, plan.Targets.Select(target => target.DroidId));
        Assert.False(plan.Targets[0].IsMaster);
        Assert.False(plan.Targets[1].IsMaster);
        Assert.True(plan.Targets[2].IsMaster);
        Assert.Equal("CCCCDDDD", plan.Targets[0].ExpectedBuildId);
        Assert.Equal("AAAABBBB", plan.Targets[2].ExpectedBuildId);
    }

    [Fact]
    public void Create_NeverDowngradesOrOverwritesSameVersionCustomBuild()
    {
        var droids = new[]
        {
            Droid(0x2001, "Ahead", false, true, true, "1.12.0", "DEVNEWER"),
            Droid(0x2002, "Custom", false, true, true, "1.11.0", "LOCAL123"),
            Droid(0x2003, "Official", false, true, true, "1.11.0", "CCCCDDDD"),
        };

        var plan = FleetUpdatePlanner.Create(droids, Release());

        Assert.Empty(plan.Targets);
        Assert.Contains(plan.Notices, notice => notice.Contains("no downgrade", StringComparison.Ordinal));
        Assert.Contains(plan.Notices, notice => notice.Contains("different/local build", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_LeavesUnknownVersionsForManualReview()
    {
        var plan = FleetUpdatePlanner.Create(
            new[] { Droid(0x3001, "Legacy", false, true, true, "unknown", "") },
            Release());

        Assert.Empty(plan.Targets);
        Assert.Single(plan.Notices);
        Assert.Contains("manual review", plan.Notices[0]);
    }

    [Theory]
    [InlineData("1.10.0", "1.11.0", false)]
    [InlineData("1.11.0", "1.11.0", true)]
    [InlineData("1.12.0", "1.11.0", true)]
    public void DroidUpdateBadge_NeverOffersADowngrade(string installed, string published, bool expected)
    {
        var droid = Droid(0x4001, "Dev", false, true, true, installed, "BUILD");
        droid.LatestFwVersion = published;

        Assert.Equal(expected, droid.FwUpToDate);
    }

    [Theory]
    [InlineData("1.11.0", "CCCCDDDD", true)]
    [InlineData("1.10.0", "CCCCDDDD", false)]
    [InlineData("1.11.0", "WRONGBLD", false)]
    public void Verification_RequiresThePublishedVersionAndBuild(string version, string build, bool expected)
    {
        var target = new FleetUpdateTarget(
            0x5001, "Slave", false, "1.10.0", "OLD", "1.11.0", "CCCCDDDD");

        var result = FleetUpdateViewModel.VerifyIdentity(target, version, build);

        Assert.Equal(expected, result.Ok);
    }

    [Fact]
    public void StartupFingerprint_IgnoresTelemetryRefreshButTracksUpdateRelevantChanges()
    {
        var release = Release();
        var droid = Droid(0x6001, "Slave", false, true, true, "1.10.0", "OLD");
        droid.Rssi = -65;
        var initial = MainViewModel.BuildFleetUpdateFingerprint(new[] { droid }, release);

        droid.Rssi = -82;
        droid.LastSeen = droid.LastSeen.AddSeconds(2);
        var telemetryOnly = MainViewModel.BuildFleetUpdateFingerprint(new[] { droid }, release);
        droid.Online = false;
        var wentOffline = MainViewModel.BuildFleetUpdateFingerprint(new[] { droid }, release);

        Assert.Equal(initial, telemetryOnly);
        Assert.NotEqual(initial, wentOffline);
    }

    private static Droid Droid(
        ushort id,
        string name,
        bool master,
        bool online,
        bool adopted,
        string version,
        string build) => new()
        {
            Id = id,
            Name = name,
            IsMaster = master,
            Online = online,
            Adopted = adopted,
            FwVersion = version,
            BuildId = build,
        };
}
