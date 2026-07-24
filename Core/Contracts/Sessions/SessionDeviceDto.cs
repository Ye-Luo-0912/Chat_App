namespace Core.Contracts.Sessions;

public sealed class SessionDeviceDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? DeviceType { get; set; }
    public string? ClientIp { get; set; }
    public string? UserAgent { get; set; }
    public DateTime LoginAt { get; set; }
    public DateTime LastActiveAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? SessionId { get; set; }
    public int RefreshCount { get; set; }
    public bool IsCurrent { get; set; }

    public string Title =>
        !string.IsNullOrWhiteSpace(DeviceName) ? DeviceName! :
        !string.IsNullOrWhiteSpace(DeviceType) ? DeviceType! :
        DeviceId;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (IsCurrent) parts.Add("当前设备");
            if (!string.IsNullOrWhiteSpace(ClientIp)) parts.Add(ClientIp!);
            parts.Add($"活跃 {LastActiveAt.ToLocalTime():MM-dd HH:mm}");
            return string.Join(" · ", parts);
        }
    }
}
