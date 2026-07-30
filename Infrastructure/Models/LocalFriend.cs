using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Infrastructure.Models;

public class LocalFriend : INotifyPropertyChanged
{
    public int Id { get; set; }

    public long OwnerUserId { get; set; }

    public long FriendId { get; set; }
    public string? FriendName { get; set; } = string.Empty;

    public string? AvatarUrl { get; set; }

    public string? Note { get; set; }
    public FriendshipStatus Status { get; set; }

    public bool IsDeleted { get; set; }

    public int? GroupId { get; set; }

    private string _displayName = string.Empty;
    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    public DateTime LastSynced { get; set; }

    /// <summary>用于头像圆圈显示的首字符。</summary>
    public string Initial =>
        !string.IsNullOrEmpty(DisplayName) ? DisplayName[..1] :
        !string.IsNullOrEmpty(FriendName) ? FriendName[..1] : "?";

    public DateTime CreatedAt { get; set; }
    public string? GroupName { get; set; }

    private bool _isPinned;
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (SetField(ref _isPinned, value))
                OnPropertyChanged(nameof(PinGlyph));
        }
    }

    private long? _pinnedAtMs;
    public long? PinnedAtMs
    {
        get => _pinnedAtMs;
        set => SetField(ref _pinnedAtMs, value);
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetField(ref _isMuted, value))
            {
                OnPropertyChanged(nameof(MuteGlyph));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    private long? _mutedUntilMs;
    public long? MutedUntilMs
    {
        get => _mutedUntilMs;
        set => SetField(ref _mutedUntilMs, value);
    }

    private string? _lastMessagePreview;
    public string? LastMessagePreview
    {
        get => _lastMessagePreview;
        set
        {
            if (SetField(ref _lastMessagePreview, value))
                OnPropertyChanged(nameof(Subtitle));
        }
    }

    public string PinGlyph => IsPinned ? "📌" : string.Empty;
    public string MuteGlyph => IsMuted ? "🔕" : string.Empty;

    private bool _isOnline;
    public bool IsOnline
    {
        get => _isOnline;
        set
        {
            if (SetField(ref _isOnline, value))
            {
                OnPropertyChanged(nameof(OnlineDotColor));
                OnPropertyChanged(nameof(IsOffline));
            }
        }
    }

    public bool IsOffline => !IsOnline;

    public string OnlineDotColor => IsOnline ? "#22C55E" : "#D1D5DB";

    public string Title =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName :
        !string.IsNullOrWhiteSpace(FriendName) ? FriendName! :
        FriendId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Subtitle =>
        !string.IsNullOrWhiteSpace(LastMessagePreview)
            ? LastMessagePreview!
            : (IsMuted ? "消息免打扰" : "点击开始聊天");

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum FriendshipStatus : byte
{
    None = 0,
    Pending = 1,
    Approved = 2,
    Rejected = 5,
}
