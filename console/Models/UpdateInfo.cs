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
    public string? Notes { get; set; }
}

public class UpdateCheckResult
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public AppUpdateInfo App { get; set; } = new();
    public FirmwareUpdateInfo Fw { get; set; } = new();
}
