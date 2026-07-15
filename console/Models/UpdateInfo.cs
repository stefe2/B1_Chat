namespace b1_chat_console.Models;

public class AppUpdateInfo
{
    public string? Latest { get; set; }
    public string? Url { get; set; }
    public string? Notes { get; set; }
}

public class FirmwareUpdateInfo
{
    public string? Latest { get; set; }
    public string? UrlMaster { get; set; }
    public string? UrlSlave { get; set; }
    public string? Sha256Master { get; set; }
    public string? Sha256Slave { get; set; }
    // Role-independent, shared by both roles — needed to flash a virgin board (offsets
    // 0x1000/0x8000). Absent from older releases (then only an app-only flash is possible).
    public string? UrlBootloader { get; set; }
    public string? UrlPartitions { get; set; }
    public string? Sha256Bootloader { get; set; }
    public string? Sha256Partitions { get; set; }
    public string? Notes { get; set; }
}

public class UpdateCheckResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public AppUpdateInfo App { get; set; } = new();
    public FirmwareUpdateInfo Fw { get; set; } = new();
}
