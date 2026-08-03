using Core.Models.DTO;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Chat_App.Presentation.ViewModels.Chat;

/// <summary>创建群聊 / 添加成员时好友勾选条目。</summary>
public sealed class GroupMemberSelectionItem : INotifyPropertyChanged
{
    public long UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
                OnPropertyChanged(nameof(SelectionGlyph));
        }
    }

    public string Initial => DisplayName.Length > 0 ? DisplayName[..1] : "?";

    public string SelectionGlyph => IsSelected ? "☑" : "☐";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>群成员列表条目（成员面板数据源）。</summary>
public sealed class GroupMemberUiItem : INotifyPropertyChanged
{
    public long UserId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public bool IsSelf { get; init; }

    private ConversationMemberRole _role;
    public ConversationMemberRole Role
    {
        get => _role;
        set
        {
            if (SetField(ref _role, value))
            {
                OnPropertyChanged(nameof(RoleText));
                OnPropertyChanged(nameof(CanPromote));
                OnPropertyChanged(nameof(CanDemote));
            }
        }
    }

    public string Initial => DisplayName.Length > 0 ? DisplayName[..1] : "?";

    public string RoleText => _role switch
    {
        ConversationMemberRole.Owner => "群主",
        ConversationMemberRole.Admin => "管理员",
        _ => "成员"
    };

    /// <summary>普通成员可被提升为管理员（自己除外）。</summary>
    public bool CanPromote => !IsSelf && _role == ConversationMemberRole.Member;

    /// <summary>管理员可被降级为普通成员（自己除外）。</summary>
    public bool CanDemote => !IsSelf && _role == ConversationMemberRole.Admin;

    /// <summary>自己不可移除自己（退出走退群命令）。</summary>
    public bool CanRemove => !IsSelf;

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
