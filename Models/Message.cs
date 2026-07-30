using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Core.Models;
using Core.Models.DTO;

namespace Chat_App.Models;

public class Message : INotifyPropertyChanged
{
    private bool _isRecalled;
    private bool _isEdited;
    private int _editVersion = 1;
    private long? _editedAtMs;
    private string _content = string.Empty;
    private string? _messageId;
    private IReadOnlyList<AttachmentRefDto>? _attachments;

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
    public bool IsSentByMe { get; set; }

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
        }
    }

    public bool IsFailed => Status == MessageStatus.Failed;
    public bool IsPending => Status == MessageStatus.Queued || Status == MessageStatus.Sending;
    public bool IsRead => Status == MessageStatus.Read;
    public bool IsRecalledStatus => Status == MessageStatus.Recalled;

    public required User Sender { get; set; }

    public IReadOnlyList<AttachmentRefDto>? Attachments
    {
        get => _attachments;
        set
        {
            if (ReferenceEquals(_attachments, value))
                return;
            _attachments = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(AttachmentSummary));
        }
    }

    public bool IsRecalled
    {
        get => _isRecalled;
        set
        {
            if (_isRecalled == value)
                return;
            _isRecalled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayContent));
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(HasReply));
            OnPropertyChanged(nameof(HasForward));
            OnPropertyChanged(nameof(IsEdited));
        }
    }

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

    public bool IsEdited
    {
        get => !IsRecalled && (_isEdited || EditVersion > 1 || EditedAtMs is > 0);
        set
        {
            if (_isEdited == value)
                return;
            _isEdited = value;
            OnPropertyChanged();
        }
    }

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

    public void ApplyRecalled()
    {
        IsRecalled = true;
        IsEdited = false;
        EditedAtMs = null;
        Content = string.Empty;
        Attachments = null;
        ReplyToMessageId = null;
        ReplyToSenderUserId = null;
        ReplyToPreview = null;
        ForwardedFromMessageId = null;
        ForwardedFromSenderUserId = null;
        ForwardedFromPreview = null;
        OnPropertyChanged(nameof(HasReply));
        OnPropertyChanged(nameof(ReplyDisplayText));
        OnPropertyChanged(nameof(HasForward));
        OnPropertyChanged(nameof(ForwardDisplayText));
    }

    public void ApplyEdited(string content, int editVersion, long editedAtMs)
    {
        if (IsRecalled)
            return;

        Content = content?.Trim() ?? string.Empty;
        EditVersion = editVersion > 0 ? editVersion : Math.Max(EditVersion, 2);
        EditedAtMs = editedAtMs > 0 ? editedAtMs : EditedAtMs;
        IsEdited = EditVersion > 1 || EditedAtMs is > 0;
    }
}
