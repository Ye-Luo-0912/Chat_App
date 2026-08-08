using ChatApp.Contracts.Http.Sessions;

namespace Chat_App.Presentation.ViewModels.Shell;

/// <summary>Presentation adapter for the shared session wire contract.</summary>
public sealed class SessionDeviceListItem(SessionDevice contract)
{
    public SessionDevice Contract { get; } = contract;

    public string DeviceId => Contract.DeviceId;
    public bool IsCurrent => Contract.IsCurrent;

    public string Title =>
        !string.IsNullOrWhiteSpace(Contract.DeviceName) ? Contract.DeviceName! :
        !string.IsNullOrWhiteSpace(Contract.DeviceType) ? Contract.DeviceType! :
        Contract.DeviceId;

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (Contract.IsCurrent) parts.Add("当前设备");
            if (!string.IsNullOrWhiteSpace(Contract.ClientIp)) parts.Add(Contract.ClientIp!);
            parts.Add($"活跃 {Contract.LastActiveAt.ToLocalTime():MM-dd HH:mm}");
            return string.Join(" · ", parts);
        }
    }
}
