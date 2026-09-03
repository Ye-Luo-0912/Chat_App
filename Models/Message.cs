using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Core.Models;
using Core.Models.DTO;

namespace Chat_App.Models;

/// <summary>消息状态变化类型。</summary>
public enum MessageMutationKind
{
    /// <summary>撤回：置为 Recalled 并清空内容/引用。</summary>
    Recall,

    /// <summary>编辑：版本单调合并。</summary>
    Edit
}

/// <summary>
/// 消息状态变化载荷。所有变化统一经 <see cref="Message.TryApply"/> 应用，
/// 状态、属性通知与版本比较集中在同一处。
/// </summary>
public readonly record struct MessageMutation(
    MessageMutationKind Kind,
    string? Content = null,
    int EditVersion = 0,
    long? EditedAtMs = null,
    long? RecalledAtMs = null);

/// <summary>
/// UI 消息模型：单一状态真相由 Status/RecalledAtMs/EditVersion/EditedAtMs 决定，
/// 不再维护独立的 _isRecalled/_isEdited 布尔通道。
/// </summary>
public class Message : INotifyPropertyChanged
{
    private int _editVersion = 1;
    private long? _editedAtMs;
    private long? _recalledAtMs;
    private string _content = string.Empty;
    private string? _messageId;
    private IReadOnlyList<AttachmentRefDto>? _attachments;
    private IReadOnlyList<ImageThumbnailItem>? _imageThumbnails;

    /// <summary>客户端本地发送 Id，用于匹配 MessageAck。</summary>
    public string? ClientMessageId { get; set; }

    /// <summary>服务端消息 Id（CommandId）；ack 到达后写入。</summary>
    public string? MessageId
    {
        get => _messageId;
        set
        {
            if (_messageId == value)
                return;
            _messageId = value;
            OnPropertyChanged();
        }
    }

    public required string Content
    {
        get => _content;
        set
        {
            if (_content == value)
                return;
            _content = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayContent));
        }
    }

    public DateTime Timestamp { get; set; }

    /// <summary>服务端接收时间（Unix 毫秒），用于历史分页游标。</summary>
    public long ReceivedAtMs { get; set; }
    public bool IsSentByMe { get; set; }

    /// <summary>实际发送者用户 Id（群聊消息的关键身份：回复/转发按此保留真实发送者）。</summary>
    public long SenderUserId { get; set; }

    private MessageStatus _status = MessageStatus.Sent;
    public MessageStatus Status
    {
        get => _status;
        set
        {
            if (_status == value)
                return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFailed));
            OnPropertyChanged(nameof(IsPending));
            OnPropertyChanged(nameof(IsRead));
            OnPropertyChanged(nameof(IsRecalled));
            OnPropertyChanged(nameof(IsSendFailed));
            OnPropertyChanged(nameof(StatusGlyphText));
            OnPropertyChanged(nameof(StatusGlyphColor));
            OnPropertyChanged(nameof(StatusGlyphVisibility));
            OnPropertyChanged(nameof(DisplayContent));
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(HasReply));
            OnPropertyChanged(nameof(HasForward));
            OnPropertyChanged(nameof(IsEdited));
        }
    }

    /// <summary>发送失败原因（服务端错误或本地异常描述），Failed 状态下可查看。</summary>
    private string? _failedReason;
    public string? FailedReason
    {
        get => _failedReason;
        set
        {
            if (_failedReason == value)
                return;
            _failedReason = value;
            OnPropertyChanged();
        }
    }

    public bool IsFailed => Status == MessageStatus.Failed;
    public bool IsPending => Status == MessageStatus.Queued || Status == MessageStatus.Sending;
    public bool IsRead => Status == MessageStatus.Read;

    /// <summary>我方发送且处于失败状态：气泡显示失败标记，菜单提供重试/查看原因/删除。</summary>
    public bool IsSendFailed => IsSentByMe && Status == MessageStatus.Failed;

    /// <summary>状态字形：排队 ⏱ / 发送中 ↻ / 已送达 ✓ / 已读 ✓✓ / 失败 ⚠。</summary>
    public string StatusGlyphText => Status switch
    {
        MessageStatus.Queued => "⏱",
        MessageStatus.Sending => "↻",
        MessageStatus.Sent => "✓",
        MessageStatus.Delivered => "✓✓",
        MessageStatus.Read => "✓✓",
        MessageStatus.Failed => "⚠",
        _ => string.Empty
    };

    public string StatusGlyphColor => Status switch
    {
        MessageStatus.Failed => "#EF4444",
        MessageStatus.Read => "#3B82F6", // 已读用高亮蓝，与已送达的灰色 ✓✓ 形成视觉区分
        _ => "#94A3B8"
    };

    /// <summary>仅我方消息且未撤回时展示状态字形。</summary>
    public bool StatusGlyphVisibility => IsSentByMe && !IsRecalled;

    /// <summary>撤回时间（Unix 毫秒）。RecalledAtMs > 0 时强制 Status=Recalled，保证单一真相。</summary>
    public long? RecalledAtMs
    {
        get => _recalledAtMs;
        set
        {
            if (_recalledAtMs == value)
                return;
            _recalledAtMs = value;
            // 单一真相由 Status 承担：写入撤回时间时同步置为 Recalled。
            if (value is > 0 && Status != MessageStatus.Recalled)
                Status = MessageStatus.Recalled;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRecalled));
            OnPropertyChanged(nameof(StatusGlyphVisibility));
            OnPropertyChanged(nameof(DisplayContent));
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(HasReply));
            OnPropertyChanged(nameof(HasForward));
            OnPropertyChanged(nameof(IsEdited));
        }
    }

    public required User Sender { get; set; }

    public IReadOnlyList<AttachmentRefDto>? Attachments
    {
        get => _attachments;
        set
        {
            if (ReferenceEquals(_attachments, value))
                return;
            _attachments = value;
            // 图片附件同步派生缩略图项（撤回置空时自动清空）。
            ImageThumbnails = value is null
                ? null
                : value
                    .Where(a => Core.Helpers.AttachmentType.IsImage(a.ContentType))
                    .Select(a => new ImageThumbnailItem(a))
                    .ToList();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(AttachmentSummary));
            OnPropertyChanged(nameof(HasImageThumbnails));
        }
    }

    /// <summary>图片附件缩略图项（后台预取成功后填充 ThumbnailPath）。</summary>
    public IReadOnlyList<ImageThumbnailItem>? ImageThumbnails
    {
        get => _imageThumbnails;
        private set
        {
            if (ReferenceEquals(_imageThumbnails, value))
                return;
            _imageThumbnails = value;
            OnPropertyChanged();
        }
    }

    /// <summary>单一真相：仅由 Status 决定，不再依赖独立的 RecalledAtMs 布尔通道。</summary>
    public bool IsRecalled => Status == MessageStatus.Recalled;

    /// <summary>消息含图片附件且未撤回：气泡内联展示缩略图条。</summary>
    public bool HasImageThumbnails => !IsRecalled && ImageThumbnails is { Count: > 0 };

    public int EditVersion
    {
        get => _editVersion;
        set
        {
            if (_editVersion == value)
                return;
            _editVersion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEdited));
        }
    }

    public long? EditedAtMs
    {
        get => _editedAtMs;
        set
        {
            if (_editedAtMs == value)
                return;
            _editedAtMs = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEdited));
        }
    }

    public bool IsEdited => !IsRecalled && (EditVersion > 1 || EditedAtMs is > 0);

    public string DisplayContent =>
        IsRecalled ? "消息已撤回" : Content;

    public bool HasAttachments => !IsRecalled && Attachments is { Count: > 0 };

    public string AttachmentSummary =>
        !HasAttachments
            ? string.Empty
            : string.Join(", ", Attachments!.Select(a => a.FileName ?? a.AttachmentId));

    public string? ReplyToMessageId { get; set; }
    public long? ReplyToSenderUserId { get; set; }
    public string? ReplyToPreview { get; set; }

    public bool HasReply => !IsRecalled && !string.IsNullOrWhiteSpace(ReplyToMessageId);

    public string ReplyDisplayText
    {
        get
        {
            if (!HasReply)
                return string.Empty;

            var preview = string.IsNullOrWhiteSpace(ReplyToPreview) ? "原消息" : ReplyToPreview!;
            return preview;
        }
    }

    public string? ForwardedFromMessageId { get; set; }
    public long? ForwardedFromSenderUserId { get; set; }
    public string? ForwardedFromPreview { get; set; }

    public bool HasForward => !IsRecalled && !string.IsNullOrWhiteSpace(ForwardedFromMessageId);

    public string ForwardDisplayText
    {
        get
        {
            if (!HasForward)
                return string.Empty;

            return string.IsNullOrWhiteSpace(ForwardedFromPreview) ? "原消息" : ForwardedFromPreview!;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// 统一状态变化入口：按 mutation 类型应用撤回/编辑，返回是否真正发生状态变化。
    /// 版本比较与属性通知全部集中在这里。
    /// </summary>
    public bool TryApply(MessageMutation mutation)
    {
        switch (mutation.Kind)
        {
            case MessageMutationKind.Recall:
                {
                    var recalledAt = mutation.RecalledAtMs > 0
                        ? mutation.RecalledAtMs.Value
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    // 已撤回或旧撤回（时间不更新）：拒绝。
                    if (Status == MessageStatus.Recalled || (RecalledAtMs is > 0 && RecalledAtMs >= recalledAt))
                        return false;

                    RecalledAtMs = recalledAt;
                    Status = MessageStatus.Recalled;
                    Content = string.Empty;
                    Attachments = null;
                    ReplyToMessageId = null;
                    ReplyToSenderUserId = null;
                    ReplyToPreview = null;
                    ForwardedFromMessageId = null;
                    ForwardedFromSenderUserId = null;
                    ForwardedFromPreview = null;
                    OnPropertyChanged(nameof(ReplyDisplayText));
                    OnPropertyChanged(nameof(ForwardDisplayText));
                    return true;
                }
            case MessageMutationKind.Edit:
                {
                    // 已撤回消息不可编辑。
                    if (IsRecalled)
                        return false;
                    // 版本单调：严格更新的版本才覆盖；无版本号时以编辑时间拒绝旧编辑。
                    if (mutation.EditVersion > 0 && EditVersion >= mutation.EditVersion)
                        return false;
                    if (mutation.EditVersion <= 0
                        && mutation.EditedAtMs is > 0
                        && EditedAtMs is > 0
                        && mutation.EditedAtMs <= EditedAtMs)
                    {
                        return false;
                    }

                    Content = mutation.Content?.Trim() ?? string.Empty;
                    if (mutation.EditVersion > 0)
                        EditVersion = mutation.EditVersion;
                    if (mutation.EditedAtMs is > 0)
                        EditedAtMs = mutation.EditedAtMs;
                    return true;
                }
            default:
                return false;
        }
    }
}
