using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Models;
using Chat_App.Services;
using Chat_App.Shared.Commands;
using Chat_App.Shared.Mvvm;
using Core.Contracts.Attachments;
using Core.Events;
using Core.Helpers;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Infrastructure.Models;
using Infrastructure.Serialization;
using Serilog;

namespace Chat_App.Presentation.ViewModels.Chat;

/// <summary>
/// 消息视图 ViewModel：文本发送 + 附件上传（进度/取消/重试）+ 下行附件下载。
/// </summary>
public class MessageViewModel : ViewModelBase, IDisposable
{
    private readonly INotificationService _notificationService;
    private readonly IChatSessionClient _chatSession;
    private readonly IAttachmentClientService _attachments;
    private readonly IMessageStore _messageStore;
    private readonly IEventBus _eventBus;
    private readonly IDatabaseService _dbService;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IAttachmentStorageService _storage;
    private readonly List<IDisposable> _eventSubscriptions = [];

    private LocalFriend? CurrFriend { get; set; }
    private string? CurrConversationId { get; set; }

    private string _peerTitle = string.Empty;
    public string PeerTitle
    {
        get => _peerTitle;
        private set => SetProperty(ref _peerTitle, value);
    }

    private bool _peerIsOnline;
    public bool PeerIsOnline
    {
        get => _peerIsOnline;
        private set
        {
            if (SetProperty(ref _peerIsOnline, value))
                OnPropertyChanged(nameof(PeerStatusText));
        }
    }

    public string PeerStatusText => PeerIsOnline ? "在线" : "离线";

    private bool _isPeerTyping;
    public bool IsPeerTyping
    {
        get => _isPeerTyping;
        private set
        {
            if (SetProperty(ref _isPeerTyping, value))
                OnPropertyChanged(nameof(TypingStatusText));
        }
    }

    public string TypingStatusText => IsPeerTyping ? "对方正在输入…" : string.Empty;

    private CancellationTokenSource? _peerTypingClearCts;
    private DateTimeOffset _lastTypingSentUtc = DateTimeOffset.MinValue;
    private bool _typingActive;

    private string _newMessage = string.Empty;
    public string NewMessage
    {
        get => _newMessage;
        set
        {
            if (!SetProperty(ref _newMessage, value))
                return;
            _ = NotifyTypingFromComposerAsync();
        }
    }

    private double _uploadProgress;
    public double UploadProgress
    {
        get => _uploadProgress;
        private set => SetProperty(ref _uploadProgress, value);
    }

    private bool _isUploading;
    public bool IsUploading
    {
        get => _isUploading;
        private set
        {
            if (SetProperty(ref _isUploading, value))
            {
                SendMessageCommand.RaiseCanExecuteChanged();
                AttachFileCommand.RaiseCanExecuteChanged();
                CancelUploadCommand.RaiseCanExecuteChanged();
            }
        }
    }

    // 多附件草稿（阶段 3-5）
    private readonly List<PendingAttachment> _pendingAttachments = new();

    public IReadOnlyList<PendingAttachment> PendingAttachments => _pendingAttachments;

    public bool HasPendingAttachment => _pendingAttachments.Count > 0;

    public string PendingAttachmentSummary =>
        _pendingAttachments.Count == 0
            ? string.Empty
            : string.Join(", ", _pendingAttachments.Select(a => a.FileName ?? a.AttachmentId));

    private string? _replyToMessageId;
    private long? _replyToSenderUserId;
    private string? _replyDraftPreview;

    public string? ReplyDraftPreview
    {
        get => _replyDraftPreview;
        private set
        {
            if (SetProperty(ref _replyDraftPreview, value))
            {
                OnPropertyChanged(nameof(HasReplyDraft));
                ClearReplyDraftCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasReplyDraft => !string.IsNullOrWhiteSpace(_replyToMessageId);

    private Message? _editingMessage;

    public string? EditDraftPreview
    {
        get => _editingMessage is null
            ? null
            : (string.IsNullOrWhiteSpace(_editingMessage.Content) ? "编辑消息" : _editingMessage.Content);
    }

    public bool HasEditDraft => _editingMessage is not null;

    public string SendButtonText => HasEditDraft ? "保存" : "发送";

    public ObservableCollection<Message> Messages { get; } = [];

    private readonly Dictionary<string, Message> _messagesByServerId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Message> _messagesByClientId = new(StringComparer.Ordinal);
    private static readonly User EmptyUser = new();

    public AsyncRelayCommand SendMessageCommand { get; }
    public AsyncRelayCommand AttachFileCommand { get; }
    public AsyncRelayCommand CancelUploadCommand { get; }
    public AsyncRelayCommand<PendingAttachment?> ClearPendingAttachmentCommand { get; }
    public AsyncRelayCommand ClearReplyDraftCommand { get; }
    public AsyncRelayCommand ClearEditDraftCommand { get; }
    public RelayCommand ReplyToMessageCommand { get; }
    public RelayCommand ForwardMessageCommand { get; }
    public RelayCommand BeginEditMessageCommand { get; }
    public AsyncRelayCommand<Message> RecallMessageCommand { get; }
    public AsyncRelayCommand<AttachmentRefDto> DownloadAttachmentCommand { get; }

    /// <summary>由 ChatViewModel 注入：进入转发选好友模式。</summary>
    public Action<Message>? ForwardRequested { get; set; }

    /// <summary>由 View 注入：打开文件选择器并返回待上传文件。</summary>
    public Func<CancellationToken, Task<PickedAttachmentFile?>>? PickAttachmentAsync { get; set; }

    /// <summary>由 View 注入：保存下载流到用户选择的路径。</summary>
    public Func<string, Stream, string, CancellationToken, Task<bool>>? SaveDownloadedAttachmentAsync { get; set; }

    public MessageViewModel(
        INotificationService notificationService,
        IChatSessionClient chatSessionClient,
        IAttachmentClientService attachmentClientService,
        IMessageStore messageStore,
        IEventBus eventBus,
        IDatabaseService dbService,
        ICurrentUserContext currentUserContext,
        IAttachmentStorageService storage)
    {
        _notificationService = notificationService;
        _chatSession = chatSessionClient;
        _attachments = attachmentClientService;
        _storage = storage;
        _messageStore = messageStore;
        _eventBus = eventBus;
        _dbService = dbService;
        _currentUserContext = currentUserContext;

        SendMessageCommand = new AsyncRelayCommand(
            SendMessage,
            () => !IsUploading,
            ex =>
            {
                Log.Error(ex, "发送消息失败");
                _notificationService.ShowError($"发送消息失败: {ex.Message}");
            });

        AttachFileCommand = new AsyncRelayCommand(
            AttachFileAsync,
            () => !IsUploading && CurrFriend is not null,
            ex =>
            {
                Log.Error(ex, "附件上传失败");
                _notificationService.ShowError($"附件上传失败: {ex.Message}");
            });

        CancelUploadCommand = new AsyncRelayCommand(
            _ =>
            {
                AttachFileCommand.Cancel();
                return Task.CompletedTask;
            },
            () => IsUploading);

        ClearPendingAttachmentCommand = new AsyncRelayCommand<PendingAttachment?>(
            async (att, ct) =>
            {
                if (att is not null)
                {
                    try
                    {
                        await _attachments.AbandonAsync(att.AttachmentId, ct).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "清除待发送附件时 abandon 失败");
                    }
                    ClearPendingAttachment(att);
                }
                else
                {
                    foreach (var item in _pendingAttachments)
                    {
                        try
                        {
                            await _attachments.AbandonAsync(item.AttachmentId, ct).ConfigureAwait(true);
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "清除待发送附件时 abandon 失败");
                        }
                    }
                    ClearPendingAttachment();
                }
            },
            _ => HasPendingAttachment && !IsUploading);

        ClearReplyDraftCommand = new AsyncRelayCommand(
            _ =>
            {
                ClearReplyDraft();
                return Task.CompletedTask;
            },
            () => HasReplyDraft);

        ClearEditDraftCommand = new AsyncRelayCommand(
            _ =>
            {
                ClearEditDraft();
                return Task.CompletedTask;
            },
            () => HasEditDraft);

        ReplyToMessageCommand = new RelayCommand(param =>
        {
            if (param is not Message msg || string.IsNullOrWhiteSpace(msg.MessageId))
            {
                _notificationService.ShowError("该消息尚无服务端 Id，暂无法回复。");
                return;
            }

            if (msg.IsRecalled)
            {
                _notificationService.ShowError("已撤回的消息无法回复。");
                return;
            }

            ClearEditDraft();
            _replyToMessageId = msg.MessageId;
            _replyToSenderUserId = msg.IsSentByMe
                ? _chatSession.CurrentUserId
                : CurrFriend?.FriendId;
            ReplyDraftPreview = string.IsNullOrWhiteSpace(msg.Content)
                ? (msg.HasAttachments ? msg.AttachmentSummary : "原消息")
                : TruncatePreview(msg.Content);
        });

        ForwardMessageCommand = new RelayCommand(param =>
        {
            if (param is not Message msg || msg.IsRecalled)
            {
                _notificationService.ShowError("已撤回的消息无法转发。");
                return;
            }

            if (string.IsNullOrWhiteSpace(msg.MessageId))
            {
                _notificationService.ShowError("该消息尚无服务端 Id，暂无法转发。");
                return;
            }

            ForwardRequested?.Invoke(msg);
        });

        BeginEditMessageCommand = new RelayCommand(
            param =>
            {
                if (param is not Message msg
                    || !msg.IsSentByMe
                    || msg.IsRecalled
                    || string.IsNullOrWhiteSpace(msg.MessageId)
                    || msg.HasAttachments)
                {
                    _notificationService.ShowError("该消息暂无法编辑。");
                    return;
                }

                ClearReplyDraft();
                ClearPendingAttachment();
                _editingMessage = msg;
                NewMessage = msg.Content ?? string.Empty;
                OnPropertyChanged(nameof(HasEditDraft));
                OnPropertyChanged(nameof(EditDraftPreview));
                OnPropertyChanged(nameof(SendButtonText));
                ClearEditDraftCommand.RaiseCanExecuteChanged();
            },
            param => param is Message msg
                     && msg is { IsSentByMe: true, IsRecalled: false }
                     && !string.IsNullOrWhiteSpace(msg.MessageId)
                     && !msg.HasAttachments);

        RecallMessageCommand = new AsyncRelayCommand<Message>(
            RecallMessageAsync,
            msg => msg is { IsSentByMe: true, IsRecalled: false }
                   && !string.IsNullOrWhiteSpace(msg.MessageId),
            ex =>
            {
                Log.Error(ex, "撤回消息失败");
                _notificationService.ShowError($"撤回失败: {ex.Message}");
            });

        DownloadAttachmentCommand = new AsyncRelayCommand<AttachmentRefDto>(
            DownloadAttachmentAsync,
            att => att is not null && !string.IsNullOrWhiteSpace(att.AttachmentId),
            ex =>
            {
                Log.Error(ex, "附件下载失败");
                _notificationService.ShowError($"附件下载失败: {ex.Message}");
            });

        _chatSession.MessageRecalled += OnMessageRecalled;
        _chatSession.MessageEdited += OnMessageEdited;
        _chatSession.TypingUpdated += OnTypingUpdated;
        _chatSession.PresenceChanged += OnPresenceChanged;

        _eventSubscriptions.Add(_eventBus.Subscribe<MessagePersistedEvent>(OnMessagePersisted));
        _eventSubscriptions.Add(_eventBus.Subscribe<MessageStatusChangedEvent>(OnMessageStatusChanged));
        _eventSubscriptions.Add(_eventBus.Subscribe<OutboxStatusChangedEvent>(OnOutboxStatusChanged));
        _eventSubscriptions.Add(_eventBus.Subscribe<MessageRecalledEvent>(OnMessageRecalledPersisted));
        _eventSubscriptions.Add(_eventBus.Subscribe<MessageEditedEvent>(OnMessageEditedPersisted));
        _eventSubscriptions.Add(_eventBus.Subscribe<ConversationReadEvent>(OnConversationRead));
        _eventSubscriptions.Add(_eventBus.Subscribe<ConversationUpdatedEvent>(OnConversationUpdated));
    }

    private void OnMessagePersisted(MessagePersistedEvent e)
    {
        if (CurrConversationId is null || e.Message.ConversationId != CurrConversationId)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            var msg = e.Message;
            var existing = FindMessage(msg.MessageId, msg.ClientMessageId);
            if (existing is not null) return;
            var ui = ToUiMessage(msg);
            if (ui is not null) AddMessage(ui);
        });
    }

    private void OnMessageStatusChanged(MessageStatusChangedEvent e)
    {
        if (CurrConversationId is null || e.ConversationId != CurrConversationId)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            var local = FindMessage(e.MessageId, e.ClientMessageId);
            if (local is null) return;
            local.Status = e.NewStatus;
        });
    }

    private void OnOutboxStatusChanged(OutboxStatusChangedEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.ClientMessageId))
            return;
        Dispatcher.UIThread.Post(() =>
        {
            var local = FindMessage(null, e.ClientMessageId);
            if (local is null) return;
            local.Status = MapOutboxStatusToMessageStatus(e.NewStatus);
            if (!string.IsNullOrWhiteSpace(e.ServerMessageId))
            {
                local.MessageId = e.ServerMessageId;
                _messagesByServerId[e.ServerMessageId] = local;
                RecallMessageCommand.RaiseCanExecuteChanged();
                BeginEditMessageCommand.RaiseCanExecuteChanged();
            }
        });
    }

    private void OnMessageRecalledPersisted(MessageRecalledEvent e)
    {
        if (CurrConversationId is null || e.ConversationId != CurrConversationId)
            return;
        Dispatcher.UIThread.Post(() => ApplyRecalled(e.MessageId));
    }

    private void OnMessageEditedPersisted(MessageEditedEvent e)
    {
        if (CurrConversationId is null || e.ConversationId != CurrConversationId)
            return;
        Dispatcher.UIThread.Post(() => ApplyEdited(e.MessageId, e.Content, e.EditVersion, e.EditedAtMs));
    }

    private static MessageStatus MapOutboxStatusToMessageStatus(OutboxStatus os) => os switch
    {
        OutboxStatus.Queued => MessageStatus.Queued,
        OutboxStatus.Sending => MessageStatus.Sending,
        OutboxStatus.Sent => MessageStatus.Sent,
        OutboxStatus.Failed => MessageStatus.Failed,
        OutboxStatus.Cancelled => MessageStatus.Failed,
        _ => MessageStatus.Sent
    };

    private Message? FindMessage(string? messageId, string? clientMessageId)
    {
        if (!string.IsNullOrWhiteSpace(messageId) && _messagesByServerId.TryGetValue(messageId, out var m))
            return m;
        if (!string.IsNullOrWhiteSpace(clientMessageId) && _messagesByClientId.TryGetValue(clientMessageId, out m))
            return m;
        return null;
    }

    internal void AddMessage(Message msg)
    {
        Messages.Add(msg);
        if (!string.IsNullOrWhiteSpace(msg.MessageId))
            _messagesByServerId[msg.MessageId] = msg;
        if (!string.IsNullOrWhiteSpace(msg.ClientMessageId))
            _messagesByClientId[msg.ClientMessageId] = msg;
    }

    private void ClearMessages()
    {
        Messages.Clear();
        _messagesByServerId.Clear();
        _messagesByClientId.Clear();
    }

    private void OnMessageRecalled(object? sender, MessageRecalledUpdateDto update)
    {
        if (string.IsNullOrWhiteSpace(update.MessageId))
            return;

        if (CurrFriend is not null)
        {
            var peerId = CurrFriend.FriendId;
            var selfId = _chatSession.CurrentUserId;
            var touchesPeer = update.SenderUserId == peerId || update.ReceiverUserId == peerId;
            var touchesSelf = update.SenderUserId == selfId || update.ReceiverUserId == selfId;
            if (!touchesPeer || !touchesSelf)
                return;
        }

        Dispatcher.UIThread.Post(() => ApplyRecalled(update.MessageId));
    }

    private void OnMessageEdited(object? sender, MessageEditedUpdateDto update)
    {
        if (string.IsNullOrWhiteSpace(update.MessageId))
            return;

        if (CurrFriend is not null)
        {
            var peerId = CurrFriend.FriendId;
            var selfId = _chatSession.CurrentUserId;
            var touchesPeer = update.SenderUserId == peerId || update.ReceiverUserId == peerId;
            var touchesSelf = update.SenderUserId == selfId || update.ReceiverUserId == selfId;
            if (!touchesPeer || !touchesSelf)
                return;
        }

        Dispatcher.UIThread.Post(() =>
            ApplyEdited(update.MessageId, update.Content, update.EditVersion, update.EditedAtMs));
    }

    private async Task RecallMessageAsync(Message? message, CancellationToken ct)
    {
        if (message is null || !message.IsSentByMe || message.IsRecalled)
            return;
        if (string.IsNullOrWhiteSpace(message.MessageId))
        {
            _notificationService.ShowError("该消息尚无服务端 Id，暂无法撤回。");
            return;
        }

        if (!_chatSession.IsConnected || !_chatSession.IsAuthenticated)
        {
            _notificationService.ShowError("未连接到服务器或未鉴权，无法撤回。");
            return;
        }

        var ack = await _chatSession.RecallMessageAsync(message.MessageId, ct)
            .ConfigureAwait(true);
        if (!ack.Succeeded)
        {
            _notificationService.ShowError(ack.ErrorMessage ?? "撤回失败");
            return;
        }

        ApplyRecalled(message.MessageId);
        if (_editingMessage is not null
            && string.Equals(_editingMessage.MessageId, message.MessageId, StringComparison.Ordinal))
        {
            ClearEditDraft();
        }
    }

    private void ApplyRecalled(string messageId)
    {
        var existing = FindMessage(messageId, null);
        existing?.ApplyRecalled();
        BeginEditMessageCommand.RaiseCanExecuteChanged();
    }

    private void ApplyEdited(string messageId, string content, int editVersion, long editedAtMs)
    {
        var existing = FindMessage(messageId, null);
        existing?.ApplyEdited(content, editVersion, editedAtMs);
    }

    public void Init(LocalFriend selectedFriend)
    {
        var previousPeerId = CurrFriend?.FriendId ?? 0;
        if (previousPeerId > 0 && previousPeerId != selectedFriend.FriendId)
            _ = UnwatchPeerPresenceAsync(previousPeerId);

        CurrFriend = selectedFriend;
        try
        {
            CurrConversationId = ConversationId.CreateDirect(
                _currentUserContext.RequireUserId(), selectedFriend.FriendId);
        }
        catch
        {
            CurrConversationId = null;
        }
        PeerTitle = selectedFriend.Title;
        PeerIsOnline = selectedFriend.IsOnline;
        IsPeerTyping = false;
        CancelPeerTypingClear();
        ClearMessages();
        if (_newMessage.Length > 0)
        {
            _newMessage = string.Empty;
            OnPropertyChanged(nameof(NewMessage));
        }
        ClearPendingAttachment();
        ClearReplyDraft();
        ClearEditDraft();
        _typingActive = false;
        UploadProgress = 0;
        IsUploading = false;
        AttachFileCommand.RaiseCanExecuteChanged();
        Log.Debug("MessageView 初始化: 好友={FriendName}", selectedFriend.FriendName);
        _ = RefreshPeerPresenceAsync(selectedFriend.FriendId);
        _ = LoadHistoryAsync();
        _ = MarkCurrentConversationReadAsync();
    }

    private async Task LoadHistoryAsync()
    {
        if (CurrFriend is null || CurrConversationId is null)
            return;

        try
        {
            var history = await _messageStore.LoadHistoryAsync(CurrConversationId, limit: 100)
                .ConfigureAwait(true);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var lm in history)
                {
                    var ui = ToUiMessage(lm);
                    if (ui is not null)
                        AddMessage(ui);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载历史消息失败");
        }
    }

    private void OnConversationRead(ConversationReadEvent e)
    {
        if (CurrConversationId is null || !string.Equals(e.ConversationId, CurrConversationId, StringComparison.Ordinal))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            foreach (var m in Messages)
            {
                if (m.IsSentByMe && m.Status == MessageStatus.Sent)
                    m.Status = MessageStatus.Read;
            }
        });
    }

    private void OnConversationUpdated(ConversationUpdatedEvent e)
    {
        if (CurrConversationId is null || !string.Equals(e.Conversation.ConversationId, CurrConversationId, StringComparison.Ordinal))
            return;

        // 当前会话打开时未读数清零由 ChatViewModel.FriendListState 处理；此处暂无额外 UI 操作，保留订阅以便未来扩展。
    }

    private async Task MarkCurrentConversationReadAsync()
    {
        if (CurrConversationId is null || CurrFriend is null)
            return;
        try
        {
            var lastMessage = Messages.LastOrDefault();
            await _messageStore.MarkConversationReadAndNotifyAsync(
                CurrConversationId,
                lastMessage?.MessageId,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "标记会话已读失败 ConversationId={ConversationId}", CurrConversationId);
        }
    }

    /// <summary>向上加载更早的历史消息。返回是否还有更多。</summary>
    public async Task<bool> LoadOlderHistoryAsync(CancellationToken ct = default)
    {
        if (CurrConversationId is null || Messages.Count == 0)
            return false;

        var oldest = Messages.First();
        if (string.IsNullOrWhiteSpace(oldest.MessageId))
            return false;

        try
        {
            var older = await _messageStore.FetchAndPersistHistoryAsync(
                CurrConversationId,
                limit: 50,
                beforeReceivedAtMs: null,
                beforeMessageId: oldest.MessageId,
                ct: ct);

            if (older.Count == 0)
                return false;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                for (var i = older.Count - 1; i >= 0; i--)
                {
                    var uiMsg = ToUiMessage(older[i]);
                    if (uiMsg is not null)
                        Messages.Insert(0, uiMsg);
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载更早历史失败");
            return false;
        }
    }

    private Message? ToUiMessage(LocalMessage lm)
    {
        var selfId = _currentUserContext.HasUserId ? _currentUserContext.UserId!.Value : 0;
        var ts = lm.ReceivedAtMs > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(lm.ReceivedAtMs).LocalDateTime
            : DateTime.UtcNow;

        IReadOnlyList<AttachmentRefDto>? attachments = null;
        if (!string.IsNullOrWhiteSpace(lm.AttachmentsJson))
        {
            try
            {
                attachments = JsonSerializer.Deserialize<List<AttachmentRefDto>>(lm.AttachmentsJson, ChatJsonContext.Default.Options);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "反序列化附件 JSON 失败");
            }
        }

        var msg = new Message
        {
            MessageId = lm.MessageId,
            ClientMessageId = lm.ClientMessageId,
            Content = lm.Content ?? string.Empty,
            Timestamp = ts,
            IsSentByMe = lm.SenderUserId == selfId,
            Status = lm.Status,
            Sender = EmptyUser,
            Attachments = attachments,
            ReplyToMessageId = lm.ReplyToMessageId,
            ReplyToSenderUserId = lm.ReplyToSenderUserId,
            ReplyToPreview = lm.ReplyToPreview,
            ForwardedFromMessageId = lm.ForwardedFromMessageId,
            ForwardedFromSenderUserId = lm.ForwardedFromSenderUserId,
            ForwardedFromPreview = lm.ForwardedFromPreview,
            EditVersion = lm.EditVersion > 0 ? lm.EditVersion : 1,
            EditedAtMs = lm.EditedAtMs
        };

        if (lm.RecalledAtMs is > 0)
            msg.ApplyRecalled();
        else if (lm.EditVersion > 1 || lm.EditedAtMs is > 0)
            msg.IsEdited = true;

        return msg;
    }

    /// <summary>将同步 catch-up 消息合并进当前打开的会话（按 MessageId 去重）。</summary>
    public void ApplyCatchUp(IReadOnlyList<MessageHistoryItemDto> items)
    {
        if (CurrFriend is null || items.Count == 0)
            return;

        if (CurrConversationId is not null)
            _ = _messageStore.PersistHistoryAsync(CurrConversationId, items);

        var peerId = CurrFriend.FriendId;
        var selfId = _chatSession.CurrentUserId;
        var knownIds = new HashSet<string>(
            Messages.Where(m => !string.IsNullOrWhiteSpace(m.MessageId)).Select(m => m.MessageId!),
            StringComparer.Ordinal);

        foreach (var item in items.OrderBy(i => i.ReceivedAtMs).ThenBy(i => i.MessageId, StringComparer.Ordinal))
        {
            if (item.SenderUserId != peerId && item.ReceiverUserId != peerId)
                continue;
            if (item.SenderUserId != selfId && item.ReceiverUserId != selfId)
                continue;

            if (!string.IsNullOrWhiteSpace(item.MessageId))
            {
                var existingMessage = FindMessage(item.MessageId, null);
                if (existingMessage is not null)
                {
                    if (item.RecalledAtMs is > 0)
                        existingMessage.ApplyRecalled();
                    else if (item.EditVersion > 1 || item.EditedAtMs is > 0)
                        existingMessage.ApplyEdited(
                            item.Content?.Trim() ?? string.Empty,
                            item.EditVersion,
                            item.EditedAtMs ?? 0);
                    continue;
                }

                if (!knownIds.Add(item.MessageId))
                    continue;
            }

            var message = new Message
            {
                MessageId = item.MessageId,
                Content = item.Content?.Trim() ?? string.Empty,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(item.ReceivedAtMs).LocalDateTime,
                IsSentByMe = item.SenderUserId == selfId,
                Sender = EmptyUser,
                Attachments = item.Attachments,
                ReplyToMessageId = item.ReplyToMessageId,
                ReplyToSenderUserId = item.ReplyToSenderUserId,
                ReplyToPreview = item.ReplyToPreview,
                ForwardedFromMessageId = item.ForwardedFromMessageId,
                ForwardedFromSenderUserId = item.ForwardedFromSenderUserId,
                ForwardedFromPreview = item.ForwardedFromPreview,
                EditVersion = item.EditVersion > 0 ? item.EditVersion : 1,
                EditedAtMs = item.EditedAtMs
            };
            if (item.RecalledAtMs is > 0)
                message.ApplyRecalled();
            else if (item.EditVersion > 1 || item.EditedAtMs is > 0)
                message.IsEdited = true;
            AddMessage(message);
        }
    }

    private async Task SendMessage(CancellationToken ct)
    {
        if (CurrFriend is null) return;

        var text = NewMessage?.Trim();
        var hasText = !string.IsNullOrWhiteSpace(text);
        var hasAttachments = _pendingAttachments.Count > 0;
        if (!hasText && !hasAttachments) return;

        if (!_chatSession.IsConnected || !_chatSession.IsAuthenticated)
        {
            _notificationService.ShowError("未连接到服务器或未鉴权，无法发送消息。");
            return;
        }

        if (_editingMessage is not null)
        {
            if (!hasText)
            {
                _notificationService.ShowError("编辑内容不能为空。");
                return;
            }

            var editing = _editingMessage;
            if (string.IsNullOrWhiteSpace(editing.MessageId) || editing.IsRecalled)
            {
                ClearEditDraft();
                _notificationService.ShowError("该消息暂无法编辑。");
                return;
            }

            var editAck = await _chatSession.EditMessageAsync(editing.MessageId, text!, ct)
                .ConfigureAwait(true);
            if (!editAck.Succeeded)
            {
                _notificationService.ShowError(editAck.ErrorMessage ?? "编辑失败");
                return;
            }

            ApplyEdited(
                editing.MessageId,
                editAck.Content ?? text!,
                editAck.EditVersion ?? Math.Max(editing.EditVersion + 1, 2),
                editAck.EditedAtMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            NewMessage = string.Empty;
            ClearEditDraft();
            _ = StopTypingAsync();
            return;
        }

        IReadOnlyList<string>? attachmentIds = null;
        IReadOnlyList<AttachmentRefDto>? attachments = null;
        if (hasAttachments)
        {
            attachmentIds = _pendingAttachments.Select(a => a.AttachmentId).ToList();
            attachments = _pendingAttachments.Select(a => new AttachmentRefDto
            {
                AttachmentId = a.AttachmentId,
                FileName = a.FileName,
                ContentType = a.ContentType,
                SizeBytes = a.SizeBytes,
                Status = 1,
                DownloadApiHint = a.AttachmentId
            }).ToList();
        }

        var replyMessageId = _replyToMessageId;
        var replySenderId = _replyToSenderUserId;
        var replyPreview = _replyDraftPreview;

        var selfId = _currentUserContext.RequireUserId();
        var conversationId = ConversationId.CreateDirect(selfId, CurrFriend.FriendId);
        var nowUtc = DateTime.UtcNow;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var attachmentIdsJson = hasAttachments
            ? JsonSerializer.Serialize(attachmentIds, ChatJsonContext.Default.Options)
            : null;
        var attachmentsJson = hasAttachments
            ? JsonSerializer.Serialize(attachments, ChatJsonContext.Default.Options)
            : null;

        var clientMessageId = Guid.CreateVersion7().ToString("N");
        OutboxStatus outboxStatus;
        MessageStatus messageStatus;

        try
        {
            var returnedId = await _chatSession.SendChatMessageAsync(
                    CurrFriend.FriendId,
                    text,
                    attachmentIds,
                    replyMessageId,
                    replySenderId,
                    replyPreview,
                    null, null, null,
                    clientMessageId,
                    ct)
                .ConfigureAwait(true);
            clientMessageId = returnedId;
            outboxStatus = OutboxStatus.Sent;
            messageStatus = MessageStatus.Sent;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "发送消息失败，已入 Outbox 等待重试 ClientMessageId={ClientMessageId}", clientMessageId);
            outboxStatus = OutboxStatus.Failed;
            messageStatus = MessageStatus.Failed;
            _notificationService.ShowError($"消息发送失败，已保存可重试: {ex.Message}");
        }

        var outbox = new LocalOutboxMessage
        {
            OwnerUserId = selfId,
            ClientMessageId = clientMessageId,
            ConversationId = conversationId,
            TargetUserId = CurrFriend.FriendId,
            Content = text,
            AttachmentIdsJson = attachmentIdsJson,
            ReplyToMessageId = replyMessageId,
            ReplyToSenderUserId = replySenderId,
            ReplyToPreview = replyPreview,
            Status = outboxStatus,
            QueuedAt = nowUtc,
            SentAt = outboxStatus == OutboxStatus.Sent ? nowUtc : null
        };
        try
        {
            await _dbService.EnqueueOutboxAsync(outbox).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Outbox 入库失败 ClientMessageId={ClientMessageId}", clientMessageId);
        }

        var localMessage = new LocalMessage
        {
            OwnerUserId = selfId,
            ClientMessageId = clientMessageId,
            ConversationId = conversationId,
            SenderUserId = selfId,
            ReceiverUserId = CurrFriend.FriendId,
            Content = text ?? string.Empty,
            ReceivedAtMs = nowMs,
            AttachmentsJson = attachmentsJson,
            ReplyToMessageId = replyMessageId,
            ReplyToSenderUserId = replySenderId,
            ReplyToPreview = replyPreview,
            Status = messageStatus,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc
        };
        try
        {
            await _dbService.UpsertMessageAsync(localMessage).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LocalMessage 入库失败 ClientMessageId={ClientMessageId}", clientMessageId);
        }

        AddMessage(new Message
        {
            ClientMessageId = clientMessageId,
            Content = text ?? string.Empty,
            Timestamp = DateTime.Now,
            IsSentByMe = true,
            Status = messageStatus,
            Sender = EmptyUser,
            Attachments = attachments,
            ReplyToMessageId = replyMessageId,
            ReplyToSenderUserId = replySenderId,
            ReplyToPreview = replyPreview
        });

        NewMessage = string.Empty;
        ClearPendingAttachment();
        ClearReplyDraft();
        _ = StopTypingAsync();
    }

    /// <summary>
    /// 转发消息到指定好友：仅文本（或附件摘要），带 ForwardedFrom*，不含附件与回复。
    /// 调用时 CurrFriend 仍应为来源会话（先发送再 Init 目标会话）。
    /// 返回可插入目标会话的本地气泡（调用方在 Init 之后添加）。
    /// </summary>
    public async Task<Message?> ExecuteForwardAsync(
        LocalFriend target,
        Message source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        if (source.IsRecalled || string.IsNullOrWhiteSpace(source.MessageId))
        {
            _notificationService.ShowError("无法转发该消息。");
            return null;
        }

        if (!_chatSession.IsConnected || !_chatSession.IsAuthenticated)
        {
            _notificationService.ShowError("未连接到服务器或未鉴权，无法转发。");
            return null;
        }

        long forwardSenderId;
        if (source.IsSentByMe)
            forwardSenderId = _chatSession.CurrentUserId;
        else if (CurrFriend is not null)
            forwardSenderId = CurrFriend.FriendId;
        else
            forwardSenderId = 0;

        if (forwardSenderId <= 0)
        {
            _notificationService.ShowError("无法确定原消息发送方，转发失败。");
            return null;
        }

        var content = !string.IsNullOrWhiteSpace(source.Content)
            ? source.Content.Trim()
            : (source.HasAttachments
                ? source.AttachmentSummary
                : "转发消息");
        if (string.IsNullOrWhiteSpace(content))
            content = "转发消息";

        var forwardPreview = string.IsNullOrWhiteSpace(source.Content)
            ? (source.HasAttachments ? TruncatePreview(source.AttachmentSummary) : "原消息")
            : TruncatePreview(source.Content);

        var clientMessageId = await _chatSession.SendChatMessageAsync(
                target.FriendId,
                content,
                attachmentIds: null,
                replyToMessageId: null,
                replyToSenderUserId: null,
                replyToPreview: null,
                forwardedFromMessageId: source.MessageId,
                forwardedFromSenderUserId: forwardSenderId,
                forwardedFromPreview: forwardPreview,
                ct: ct)
            .ConfigureAwait(true);

        return new Message
        {
            ClientMessageId = clientMessageId,
            Content = content,
            Timestamp = DateTime.Now,
            IsSentByMe = true,
            Sender = EmptyUser,
            ForwardedFromMessageId = source.MessageId,
            ForwardedFromSenderUserId = forwardSenderId,
            ForwardedFromPreview = forwardPreview
        };
    }

    private async Task AttachFileAsync(CancellationToken ct)
    {
        if (CurrFriend is null) return;
        if (PickAttachmentAsync is null)
        {
            _notificationService.ShowError("No file picker available.");
            return;
        }

        var picked = await PickAttachmentAsync(ct).ConfigureAwait(true);
        if (picked is null) return;

        const long MaxAttachmentSize = 100 * 1024 * 1024;
        if (picked.ContentLength > MaxAttachmentSize)
        {
            _notificationService.ShowError($"File too large (max {MaxAttachmentSize / 1024 / 1024}MB).");
            return;
        }
        var availableSpace = _storage.GetAvailableDiskSpace();
        if (availableSpace.HasValue && availableSpace.Value < picked.ContentLength * 2)
        {
            _notificationService.ShowError("Insufficient disk space.");
            return;
        }

        IsUploading = true;
        UploadProgress = 0;
        string? clientAttachmentId = null;
        string? uploadingRelativePath = null;
        try
        {
            string? sha256 = null;
            try
            {
                await using (var hashStream = picked.OpenRead())
                {
                    var hashProgress = new Progress<long>(_ => { });
                    sha256 = await FileHasher.ComputeSha256Async(hashStream, hashProgress, ct).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Hash computation failed, continuing without dedup");
                sha256 = null;
            }
            if (sha256 is not null)
            {
                try
                {
                    var existing = await _dbService.GetAttachmentBySha256Async(_currentUserContext.RequireUserId(), sha256).ConfigureAwait(true);
                    if (existing is not null && !string.IsNullOrWhiteSpace(existing.AttachmentId))
                    {
                        AddPendingAttachment(new PendingAttachment
                        {
                            AttachmentId = existing.AttachmentId,
                            FileName = existing.FileName ?? picked.FileName,
                            ContentType = existing.ContentType,
                            SizeBytes = existing.SizeBytes
                        });
                        UploadProgress = 100;
                        _notificationService.ShowSuccess($"Attachment ready: {existing.FileName ?? picked.FileName}");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Local dedup query failed");
                }
            }

            clientAttachmentId = Guid.NewGuid().ToString("N");
            try
            {
                await using (var sourceStream = picked.OpenRead())
                {
                    uploadingRelativePath = await _storage.WriteToUploadingAsync(sourceStream, picked.FileName, ct).ConfigureAwait(true);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to write local temp file");
                _notificationService.ShowError("Failed to save attachment temp file.");
                return;
            }
            var owner = _currentUserContext.RequireUserId();
            try
            {
                await _dbService.UpsertAttachmentAsync(new LocalAttachment
                {
                    OwnerUserId = owner,
                    ClientAttachmentId = clientAttachmentId,
                    FileName = picked.FileName,
                    ContentType = picked.ContentType,
                    SizeBytes = picked.ContentLength,
                    Sha256 = sha256,
                    Status = 0,
                    LocalUploadingPath = uploadingRelativePath,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to persist uploading attachment metadata");
            }

            var progress = new Progress<AttachmentUploadProgress>(p =>
            {
                Dispatcher.UIThread.Post(() => UploadProgress = p.Percent);
            });

            AttachmentUploadResult result;
            await using (var uploadStream = _storage.OpenUploadingRead(uploadingRelativePath))
            {
                result = await _attachments.UploadAndConfirmAsync(
                        uploadStream, picked.ContentType, picked.ContentLength, picked.FileName,
                        clientAttachmentId: clientAttachmentId, progress, maxAttempts: 3, sha256, ct)
                    .ConfigureAwait(true);
            }
            AddPendingAttachment(new PendingAttachment
            {
                AttachmentId = result.AttachmentId,
                FileName = result.OriginalName ?? picked.FileName,
                ContentType = result.ContentType,
                SizeBytes = result.SizeBytes
            });
            UploadProgress = 100;
            _notificationService.ShowSuccess($"Attachment ready: {result.OriginalName ?? picked.FileName}");

            try
            {
                await _dbService.UpsertAttachmentAsync(new LocalAttachment
                {
                    OwnerUserId = owner,
                    AttachmentId = result.AttachmentId,
                    ClientAttachmentId = clientAttachmentId,
                    FileName = result.OriginalName,
                    ContentType = result.ContentType,
                    SizeBytes = result.SizeBytes,
                    Sha256 = sha256,
                    DownloadPath = result.DownloadPath,
                    ObjectKey = result.ObjectKey,
                    Status = 1,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }).ConfigureAwait(true);
                await _dbService.UpdateAttachmentUploadPathAsync(owner, clientAttachmentId, localUploadingPath: null).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to update attachment metadata to Available");
            }

            _storage.DeleteUploadingFile(uploadingRelativePath);
            uploadingRelativePath = null;
        }
        catch (OperationCanceledException)
        {
            await MarkAttachmentFailedAsync(clientAttachmentId, uploadingRelativePath, "Cancelled").ConfigureAwait(true);
            ClearPendingAttachment();
            _notificationService.ShowError("Attachment upload cancelled.");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Attachment upload failed ClientAttachmentId={ClientAttachmentId}", clientAttachmentId);
            await MarkAttachmentFailedAsync(clientAttachmentId, uploadingRelativePath, "Upload failed").ConfigureAwait(true);
            _notificationService.ShowError($"Attachment upload failed: {ex.Message}");
        }
        finally
        {
            IsUploading = false;
        }
    }

    private async Task MarkAttachmentFailedAsync(string? clientAttachmentId, string? uploadingRelativePath, string reason)
    {
        if (string.IsNullOrEmpty(clientAttachmentId)) return;
        try
        {
            await _dbService.UpdateAttachmentStatusAsync(_currentUserContext.RequireUserId(), null, clientAttachmentId, 2, null, reason).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to mark attachment as Failed ClientAttachmentId={ClientAttachmentId}", clientAttachmentId);
        }
    }
    private async Task DownloadAttachmentAsync(AttachmentRefDto? attachment, CancellationToken ct)
    {
        if (attachment is null || string.IsNullOrWhiteSpace(attachment.AttachmentId))
            return;

        if (SaveDownloadedAttachmentAsync is null)
        {
            _notificationService.ShowError("当前界面未接入文件保存器。");
            return;
        }

        var fileName = !string.IsNullOrWhiteSpace(attachment.FileName)
            ? attachment.FileName
            : $"{attachment.AttachmentId}.bin";

        // 1. 查本地缓存（阶段 3-4）
        string? cachedPath = null;
        try
        {
            cachedPath = _storage.GetDownloadCachePath(attachment.AttachmentId, fileName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "查询下载缓存失败");
        }

        Stream? content = null;
        try
        {
            if (cachedPath is null)
            {
                // 缓存未命中，从服务端下载并写入磁盘缓存
                var hint = !string.IsNullOrWhiteSpace(attachment.DownloadApiHint)
                    ? attachment.DownloadApiHint!
                    : attachment.AttachmentId;

                Stream? downloaded = null;
                try
                {
                    var result = await _attachments.DownloadAsync(hint, ct: ct).ConfigureAwait(true);
                    downloaded = result.Content;

                    try
                    {
                        cachedPath = await _storage.WriteToDownloadsAsync(
                            attachment.AttachmentId, fileName, downloaded, ct).ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "写入下载缓存失败");
                    }
                }
                finally
                {
                    downloaded?.Dispose();
                }

                // 下载缓存成功后更新 LocalAttachment.LocalCachePath（阶段 3-4）
                if (cachedPath is not null)
                {
                    await TryUpdateAttachmentCachePathAsync(attachment.AttachmentId, cachedPath).ConfigureAwait(true);
                }
            }
            else
            {
                Log.Information("下载缓存命中 AttachmentId={AttachmentId}", attachment.AttachmentId);
            }

            // 弹保存对话框：优先从缓存文件读取
            if (cachedPath is not null)
                content = File.OpenRead(cachedPath);

            if (content is null)
            {
                _notificationService.ShowError("下载附件失败：无法获取内容。");
                return;
            }

            var contentType = !string.IsNullOrWhiteSpace(attachment.ContentType)
                ? attachment.ContentType
                : GuessContentTypeFromName(fileName);

            var saved = await SaveDownloadedAttachmentAsync(fileName, content, contentType, ct)
                .ConfigureAwait(true);
            if (saved)
                _notificationService.ShowSuccess($"已保存: {fileName}");
        }
        catch (OperationCanceledException)
        {
            // 用户取消
        }
        catch (Exception ex)
        {
            Log.Error(ex, "下载附件失败");
            _notificationService.ShowError("下载附件失败: " + ex.Message);
        }
        finally
        {
            content?.Dispose();
        }
    }

    private async Task TryUpdateAttachmentCachePathAsync(string attachmentId, string cachedPath)
    {
        try
        {
            var owner = _currentUserContext.RequireUserId();
            var existing = await _dbService.GetAttachmentByAttachmentIdAsync(owner, attachmentId).ConfigureAwait(true);
            if (existing is not null && string.IsNullOrEmpty(existing.LocalCachePath))
            {
                existing.LocalCachePath = Path.GetFileName(cachedPath);
                existing.UpdatedAt = DateTime.UtcNow;
                await _dbService.UpsertAttachmentAsync(existing).ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "更新附件缓存路径失败");
        }
    }

    private static string GuessContentTypeFromName(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }

    private void ClearPendingAttachment(PendingAttachment? attachment = null)
    {
        if (attachment is not null)
            _pendingAttachments.Remove(attachment);
        else
        {
            _pendingAttachments.Clear();
            UploadProgress = 0;
        }
        OnPropertyChanged(nameof(PendingAttachments));
        OnPropertyChanged(nameof(HasPendingAttachment));
        OnPropertyChanged(nameof(PendingAttachmentSummary));
        ClearPendingAttachmentCommand.RaiseCanExecuteChanged();
    }

    private void AddPendingAttachment(PendingAttachment attachment)
    {
        _pendingAttachments.Add(attachment);
        OnPropertyChanged(nameof(PendingAttachments));
        OnPropertyChanged(nameof(HasPendingAttachment));
        OnPropertyChanged(nameof(PendingAttachmentSummary));
        ClearPendingAttachmentCommand.RaiseCanExecuteChanged();
    }

    private void ClearReplyDraft()
    {
        _replyToMessageId = null;
        _replyToSenderUserId = null;
        ReplyDraftPreview = null;
    }

    private void ClearEditDraft()
    {
        if (_editingMessage is null)
            return;
        _editingMessage = null;
        OnPropertyChanged(nameof(HasEditDraft));
        OnPropertyChanged(nameof(EditDraftPreview));
        OnPropertyChanged(nameof(SendButtonText));
        ClearEditDraftCommand.RaiseCanExecuteChanged();
    }

    private static string TruncatePreview(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 80 ? trimmed : trimmed[..80] + "…";
    }

    public void Clear()
    {
        var peerId = CurrFriend?.FriendId ?? 0;
        _ = StopTypingAsync();
        if (peerId > 0)
            _ = UnwatchPeerPresenceAsync(peerId);

        CurrFriend = null;
        CurrConversationId = null;
        PeerTitle = string.Empty;
        PeerIsOnline = false;
        IsPeerTyping = false;
        CancelPeerTypingClear();
        ClearMessages();
        NewMessage = string.Empty;
        ClearPendingAttachment();
        ClearReplyDraft();
        ClearEditDraft();
        AttachFileCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        CancelPeerTypingClear();
        var peerId = CurrFriend?.FriendId ?? 0;
        if (peerId > 0)
            _ = UnwatchPeerPresenceAsync(peerId);

        _chatSession.MessageRecalled -= OnMessageRecalled;
        _chatSession.MessageEdited -= OnMessageEdited;
        _chatSession.TypingUpdated -= OnTypingUpdated;
        _chatSession.PresenceChanged -= OnPresenceChanged;

        foreach (var sub in _eventSubscriptions)
            sub.Dispose();
        _eventSubscriptions.Clear();
        GC.SuppressFinalize(this);
    }

    private void OnTypingUpdated(object? sender, TypingUpdateDto update)
    {
        if (CurrFriend is null || update.SenderUserId != CurrFriend.FriendId)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            IsPeerTyping = update.IsTyping;
            if (!update.IsTyping)
            {
                CancelPeerTypingClear();
                return;
            }

            CancelPeerTypingClear();
            _peerTypingClearCts = new CancellationTokenSource();
            var token = _peerTypingClearCts.Token;
            _ = ClearPeerTypingAfterDelayAsync(token);
        });
    }

    private void OnPresenceChanged(object? sender, PresenceChangedDto update)
    {
        if (CurrFriend is null || update.UserId != CurrFriend.FriendId)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            PeerIsOnline = update.IsOnline;
            CurrFriend.IsOnline = update.IsOnline;
        });
    }

    private async Task RefreshPeerPresenceAsync(long friendId)
    {
        if (!_chatSession.IsConnected || !_chatSession.IsAuthenticated || friendId <= 0)
            return;

        try
        {
            var snap = await _chatSession.QueryPresenceAsync([friendId]).ConfigureAwait(true);
            var item = snap.Items.FirstOrDefault(i => i.UserId == friendId);
            if (item is null)
                return;

            PeerIsOnline = item.IsOnline;
            if (CurrFriend?.FriendId == friendId)
                CurrFriend.IsOnline = item.IsOnline;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "查询好友在线状态失败");
        }
    }

    private async Task UnwatchPeerPresenceAsync(long friendId)
    {
        if (!_chatSession.IsConnected || !_chatSession.IsAuthenticated || friendId <= 0)
            return;

        try
        {
            await _chatSession.UnwatchPresenceAsync([friendId]).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "取消在线状态订阅失败");
        }
    }

    private async Task NotifyTypingFromComposerAsync()
    {
        if (CurrFriend is null
            || !_chatSession.IsConnected
            || !_chatSession.IsAuthenticated)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_newMessage))
        {
            await StopTypingAsync().ConfigureAwait(false);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (_typingActive && now - _lastTypingSentUtc < TimeSpan.FromSeconds(2))
            return;

        try
        {
            await _chatSession.SendTypingNotifyAsync(
                    CurrFriend.FriendId,
                    true,
                    ConversationId.CreateDirect(_chatSession.CurrentUserId, CurrFriend.FriendId))
                .ConfigureAwait(false);
            _typingActive = true;
            _lastTypingSentUtc = now;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "发送输入状态失败");
        }
    }

    private async Task StopTypingAsync()
    {
        if (!_typingActive || CurrFriend is null)
        {
            _typingActive = false;
            return;
        }

        _typingActive = false;
        try
        {
            if (_chatSession.IsConnected && _chatSession.IsAuthenticated)
            {
                await _chatSession.SendTypingNotifyAsync(
                        CurrFriend.FriendId,
                        false,
                        ConversationId.CreateDirect(_chatSession.CurrentUserId, CurrFriend.FriendId))
                    .ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "停止输入状态失败");
        }
    }

    private async Task ClearPeerTypingAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3.5), token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => IsPeerTyping = false);
        }
        catch (OperationCanceledException)
        {
            // refreshed
        }
    }

    private void CancelPeerTypingClear()
    {
        try
        {
            _peerTypingClearCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        _peerTypingClearCts?.Dispose();
        _peerTypingClearCts = null;
    }
}

/// <summary>View 文件选择器返回的本地文件描述。</summary>
public sealed class PickedAttachmentFile
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long ContentLength { get; init; }
    public required Func<Stream> OpenRead { get; init; }
}

/// <summary>待发送附件草稿项（阶段 3-5 多附件草稿）。</summary>
public sealed class PendingAttachment
{
    public string AttachmentId { get; init; } = string.Empty;
    public string? FileName { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public long SizeBytes { get; init; }
}
