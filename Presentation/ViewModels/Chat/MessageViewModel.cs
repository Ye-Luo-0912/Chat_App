using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
using Chat_App.Infrastructure.Events;
using Core.Helpers;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Settings;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Serialization;
using Chat_App.Infrastructure.Services;
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
    private readonly IAttachmentDownloadService _downloadService;
    private readonly IAttachmentThumbnailService _thumbnailService;
    private readonly IVoiceRecorder _voiceRecorder;
    private readonly IAudioPlayer _audioPlayer;
    /// <summary>可选：用于读取持久化的音频输出设备偏好（测试/降级场景可为 null）。</summary>
    private readonly ISettingsService? _settingsService;
    private readonly List<IDisposable> _eventSubscriptions = [];

    /// <summary>持久化音频输出偏好每进程只应用一次（设置页切换会即时覆盖播放器状态）。</summary>
    private bool _audioOutputPreferenceApplied;

    private long CurrPeerId { get; set; }
    private string? CurrConversationId { get; set; }

    /// <summary>当前会话发送目标：直聊与群聊统一按会话寻址（群聊 PeerUserId 为空）。</summary>
    private Core.Models.DTO.MessageDestination? CurrDestination { get; set; }

    /// <summary>当前会话是否为群聊（群聊无对端用户，发送/转发/输入状态按会话处理）。</summary>
    private bool IsGroupConversation => CurrDestination is { IsGroup: true };

    // 会话切换代际与取消令牌：防止快速切换 A→B 时 A 的异步历史/已读任务污染 B。
    // 每次切换生成新代际，所有历史加载/附件加载/已读请求/Dispatcher 回调必须校验代际。
    private long _conversationGeneration;
    private CancellationTokenSource _conversationCts = new();

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
            ScheduleDraftSave();
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

    // 录音（VOICE-MSG-2）：跨平台 fallback 采集；UI 显示是否在录与已录时长。
    public bool IsRecording => _voiceRecorder.IsRecording;

    private string _recordingDurationText = "0:00";
    public string RecordingDurationText
    {
        get => _recordingDurationText;
        private set => SetProperty(ref _recordingDurationText, value);
    }

    // 语音播放（VOICE-MSG-2）：全局单实例播放状态，UI 据此驱动语音气泡。
    private string? _playingVoiceAttachmentId;
    public string? PlayingVoiceAttachmentId
    {
        get => _playingVoiceAttachmentId;
        private set => SetProperty(ref _playingVoiceAttachmentId, value);
    }

    private bool _isVoicePlaying;
    public bool IsVoicePlaying
    {
        get => _isVoicePlaying;
        private set => SetProperty(ref _isVoicePlaying, value);
    }

    private double _voicePlaybackProgress;
    public double VoicePlaybackProgress
    {
        get => _voicePlaybackProgress;
        private set => SetProperty(ref _voicePlaybackProgress, value);
    }

    private string _voicePlaybackDisplayText = string.Empty;
    public string VoicePlaybackDisplayText
    {
        get => _voicePlaybackDisplayText;
        private set => SetProperty(ref _voicePlaybackDisplayText, value);
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

    /// <summary>
    /// 支持 AddRange 批量通知的消息集合：历史页批量插入时只触发一次 CollectionChanged，
    /// 避免逐条插入导致每页 100 次 UI 重渲染（大列表避免频繁 CollectionChanged）。
    /// </summary>
    public sealed class BatchObservableCollection<T> : ObservableCollection<T>
    {
        public void AddRange(IEnumerable<T> items)
        {
            var list = items as IReadOnlyList<T> ?? items.ToList();
            if (list.Count == 0)
                return;

            var startIndex = Count;
            foreach (var item in list)
                Items.Add(item);

            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, new List<T>(list), startIndex));
        }
    }

    public BatchObservableCollection<Message> Messages { get; } = [];

    // 当前会话历史是否已渲染完成（含空会话）：首次批量加载到达前不显示空态，
    // 避免切换会话瞬间（ClearMessages → 历史异步加载）"还没有消息"占位闪烁。
    private bool _historyRendered = true;

    /// <summary>当前会话消息列表为空且历史已加载完成：驱动消息区"还没有消息"空态占位。</summary>
    public bool IsMessageListEmpty => _historyRendered && Messages.Count == 0;

    private readonly Dictionary<string, Message> _messagesByServerId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Message> _messagesByClientId = new(StringComparer.Ordinal);
    private static readonly User EmptyUser = new();

    // 图片缩略图预取：按附件 Id 去重（同一附件只触发一次），并发上限 2 防止快速滚动时突发网络/解码。
    private readonly HashSet<string> _thumbnailsRequested = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _thumbnailGate = new(2, 2);

    // 大图预览弹层状态：记录被预览的附件项，保存时直接复用下载/保存链路。
    private ImageThumbnailItem? _previewedImage;
    private string? _previewImagePath;
    private bool _isPreviewOpen;

    public bool IsPreviewOpen
    {
        get => _isPreviewOpen;
        private set
        {
            if (SetProperty(ref _isPreviewOpen, value) && !value)
            {
                _previewedImage = null;
                SavePreviewedImageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>大图预览源：原图路径优先，未就绪回退缩略图；均为 null 时显示占位。</summary>
    public string? PreviewImagePath
    {
        get => _previewImagePath;
        private set
        {
            if (SetProperty(ref _previewImagePath, value))
                OnPropertyChanged(nameof(HasPreviewImage));
        }
    }

    public bool HasPreviewImage => !string.IsNullOrWhiteSpace(_previewImagePath);

    public string? PreviewImageName => _previewedImage?.DisplayName;

    public AsyncRelayCommand SendMessageCommand { get; }
    public AsyncRelayCommand AttachFileCommand { get; }
    public AsyncRelayCommand CancelUploadCommand { get; }
    public AsyncRelayCommand StartRecordingCommand { get; }
    public AsyncRelayCommand SendRecordingCommand { get; }
    public AsyncRelayCommand CancelRecordingCommand { get; }
    public AsyncRelayCommand<AttachmentRefDto?> PlayVoiceCommand { get; }
    public AsyncRelayCommand<PendingAttachment?> ClearPendingAttachmentCommand { get; }
    public AsyncRelayCommand ClearReplyDraftCommand { get; }
    public AsyncRelayCommand ClearEditDraftCommand { get; }
    public RelayCommand ReplyToMessageCommand { get; }
    public RelayCommand ForwardMessageCommand { get; }
    public RelayCommand BeginEditMessageCommand { get; }
    public AsyncRelayCommand<Message> RecallMessageCommand { get; }
    public AsyncRelayCommand<Message> RetryMessageCommand { get; }
    public AsyncRelayCommand<Message> CancelSendCommand { get; }
    public RelayCommand ShowFailureReasonCommand { get; }
    public AsyncRelayCommand<Message> DeleteFailedMessageCommand { get; }
    public AsyncRelayCommand<AttachmentRefDto> DownloadAttachmentCommand { get; }
    public RelayCommand PreviewImageCommand { get; }
    public RelayCommand ClosePreviewCommand { get; }
    public AsyncRelayCommand SavePreviewedImageCommand { get; }

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
        IAttachmentStorageService storage,
        IAttachmentDownloadService downloadService,
        IAttachmentThumbnailService thumbnailService,
        IVoiceRecorder voiceRecorder,
        IAudioPlayer audioPlayer,
        ISettingsService? settingsService = null)
    {
        _notificationService = notificationService;
        _chatSession = chatSessionClient;
        _attachments = attachmentClientService;
        _storage = storage;
        _downloadService = downloadService;
        _thumbnailService = thumbnailService;
        _voiceRecorder = voiceRecorder;
        _audioPlayer = audioPlayer;
        _settingsService = settingsService;
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
            () => !IsUploading && (CurrConversationId is not null),
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

        StartRecordingCommand = new AsyncRelayCommand(
            _ =>
            {
                _voiceRecorder.Start();
                OnPropertyChanged(nameof(IsRecording));
                RefreshRecordingCommandStates();
                return Task.CompletedTask;
            },
            () => !IsRecording && !IsUploading && CurrConversationId is not null);

        SendRecordingCommand = new AsyncRelayCommand(
            SendVoiceAsync,
            () => IsRecording && !IsUploading,
            ex =>
            {
                Log.Error(ex, "语音发送失败");
                _notificationService.ShowError($"语音发送失败: {ex.Message}");
            });

        CancelRecordingCommand = new AsyncRelayCommand(
            _ =>
            {
                _voiceRecorder.Cancel();
                RecordingDurationText = "0:00";
                OnPropertyChanged(nameof(IsRecording));
                RefreshRecordingCommandStates();
                return Task.CompletedTask;
            },
            () => IsRecording);

        // 录音进度 → UI 时长显示（时长递增）。
        _voiceRecorder.Progress += p =>
        {
            var totalSec = (int)Math.Max(0, p.Elapsed.TotalSeconds);
            RecordingDurationText = $"{totalSec / 60}:{totalSec % 60:00}";
        };

        // 录音达到最长时长自动收尾 → 自动上传并发送（降级策略：超时不再无限录音）。
        _voiceRecorder.AutoCompleted += recording =>
        {
            _ = SendVoiceRecordingAsync(recording, CancellationToken.None);
        };

        // VOICE-MSG-2 播放：点击语音气泡 → 下载 WAV → 播放；再次点击暂停/恢复。
        PlayVoiceCommand = new AsyncRelayCommand<AttachmentRefDto?>(
            PlayVoiceAsync,
            att => att is not null && att.IsVoice && !string.IsNullOrWhiteSpace(att.AttachmentId),
            ex =>
            {
                Log.Error(ex, "语音播放失败");
                _notificationService.ShowError($"语音播放失败: {ex.Message}");
            });

        // 播放进度 → 全局进度/时长文本；停止 → 复位播放状态。
        _audioPlayer.Progress += p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                PlayingVoiceAttachmentId = p.Key;
                IsVoicePlaying = _audioPlayer.IsPlaying;
                var durationMs = Math.Max(1, p.Duration.TotalMilliseconds);
                VoicePlaybackProgress = Math.Clamp(p.Position.TotalMilliseconds / durationMs, 0, 1);
                VoicePlaybackDisplayText = $"{FormatVoiceTime(p.Position)} / {FormatVoiceTime(p.Duration)}";
            });
        };
        _audioPlayer.Stopped += () =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (PlayingVoiceAttachmentId is not null)
                    PlayingVoiceAttachmentId = null;
                IsVoicePlaying = false;
                VoicePlaybackProgress = 0;
                VoicePlaybackDisplayText = string.Empty;
            });
        };

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
            // 群聊回复保留实际发送者 Id（消息自带发送者）；直聊回退当前对端。
            _replyToSenderUserId = msg.IsSentByMe
                ? _chatSession.CurrentUserId
                : (msg.SenderUserId > 0
                    ? msg.SenderUserId
                    : (CurrPeerId > 0 ? CurrPeerId : (long?)null));
            ReplyDraftPreview = string.IsNullOrWhiteSpace(msg.Content)
                ? (msg.HasAttachments ? msg.AttachmentSummary : "原消息")
                : PreviewText.Truncate(msg.Content, 80);
            ScheduleDraftSave();
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
                ScheduleDraftSave();
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

        // 手动重试（Failed → Queued，交 OutboxProcessor 重发）
        RetryMessageCommand = new AsyncRelayCommand<Message>(
            RetryMessageAsync,
            msg => msg is { IsSentByMe: true, Status: MessageStatus.Failed }
                   && !string.IsNullOrWhiteSpace(msg.ClientMessageId),
            ex =>
            {
                Log.Error(ex, "重试发送失败");
                _notificationService.ShowError($"重试失败: {ex.Message}");
            });

        // 取消发送（Queued/Sending → Cancelled）
        CancelSendCommand = new AsyncRelayCommand<Message>(
            CancelSendAsync,
            msg => msg is { IsSentByMe: true }
                   && msg.Status is MessageStatus.Queued or MessageStatus.Sending
                   && !string.IsNullOrWhiteSpace(msg.ClientMessageId),
            ex =>
            {
                Log.Error(ex, "取消发送失败");
                _notificationService.ShowError($"取消失败: {ex.Message}");
            });

        // 查看发送失败原因（Failed 且带原因时可用）
        ShowFailureReasonCommand = new RelayCommand(param =>
        {
            if (param is not Message { IsSendFailed: true } msg)
                return;
            var reason = string.IsNullOrWhiteSpace(msg.FailedReason)
                ? "未知原因"
                : msg.FailedReason!;
            _notificationService.ShowError($"发送失败：{reason}");
        });

        // 删除失败消息：清除本地气泡与 Outbox 记录，彻底放弃发送
        DeleteFailedMessageCommand = new AsyncRelayCommand<Message>(
            DeleteFailedMessageAsync,
            msg => msg is { IsSendFailed: true } && !string.IsNullOrWhiteSpace(msg.ClientMessageId),
            ex =>
            {
                Log.Error(ex, "删除失败消息失败");
                _notificationService.ShowError($"删除失败: {ex.Message}");
            });

        DownloadAttachmentCommand = new AsyncRelayCommand<AttachmentRefDto>(
            DownloadAttachmentAsync,
            att => att is not null && !string.IsNullOrWhiteSpace(att.AttachmentId),
            ex =>
            {
                Log.Error(ex, "附件下载失败");
                _notificationService.ShowError($"附件下载失败: {ex.Message}");
            });

        // 大图预览：点击缩略图 → 弹层显示原图（未就绪回退缩略图/占位）。
        PreviewImageCommand = new RelayCommand(param =>
        {
            if (param is not ImageThumbnailItem item)
                return;
            _previewedImage = item;
            PreviewImagePath = item.FullPath ?? item.ThumbnailPath;
            OnPropertyChanged(nameof(PreviewImageName));
            IsPreviewOpen = true;
        });

        ClosePreviewCommand = new RelayCommand(_ => IsPreviewOpen = false);

        // 弹层内保存原图：关闭弹层后复用下载→另存为链路。
        SavePreviewedImageCommand = new AsyncRelayCommand(
            async ct =>
            {
                var item = _previewedImage;
                if (item is null)
                    return;
                IsPreviewOpen = false;
                await DownloadAttachmentAsync(item.Attachment, ct).ConfigureAwait(true);
            },
            () => _previewedImage is not null,
            ex =>
            {
                Log.Error(ex, "保存预览图片失败");
                _notificationService.ShowError($"保存失败: {ex.Message}");
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
        _eventSubscriptions.Add(_eventBus.Subscribe<PeerReadWatermarkAdvancedEvent>(OnPeerReadWatermarkAdvanced));
        _eventSubscriptions.Add(_eventBus.Subscribe<ConversationUpdatedEvent>(OnConversationUpdated));

        // 消息集合的任何变化（Add/Clear/Insert/Remove）都同步空态属性。
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsMessageListEmpty));
    }

    private void OnMessagePersisted(MessagePersistedEvent e)
    {
        if (CurrConversationId is null || e.Message.ConversationId != CurrConversationId)
            return;
        PostIfCurrent(() =>
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
        PostIfCurrent(() =>
        {
            var local = FindMessage(e.MessageId, e.ClientMessageId);
            if (local is null) return;
            local.FailedReason = e.FailureReason;
            TryAdvanceStatus(local, e.NewStatus);
        });
    }

    private void OnOutboxStatusChanged(OutboxStatusChangedEvent e)
    {
        if (string.IsNullOrWhiteSpace(e.ClientMessageId))
            return;
        PostIfCurrent(() =>
        {
            var local = FindMessage(null, e.ClientMessageId);
            if (local is null) return;
            local.FailedReason = e.FailureReason;
            TryAdvanceStatus(local, MapOutboxStatusToMessageStatus(e.NewStatus));
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
        PostIfCurrent(() => ApplyRecalled(e.MessageId, e.RecalledAtMs));
    }

    private void OnMessageEditedPersisted(MessageEditedEvent e)
    {
        if (CurrConversationId is null || e.ConversationId != CurrConversationId)
            return;
        PostIfCurrent(() => ApplyEdited(e.MessageId, e.Content, e.EditVersion, e.EditedAtMs));
    }

    /// <summary>
    /// 在 UI 线程执行 action，回调真正执行时会话可能已切换，必须再次校验代际。
    /// 统一 6 处领域事件订阅器的 generation 捕获 + Dispatcher.UIThread.Post + 代际复核模式。
    /// </summary>
    private void PostIfCurrent(Action action)
    {
        var generation = _conversationGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _conversationGeneration) return;
            action();
        });
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
        TrimMessageWindow();
    }

    // 插入旧历史时同步维护两个索引，确保后续编辑/撤回/去重能定位到这些消息。
    internal void InsertMessage(int index, Message msg)
    {
        Messages.Insert(index, msg);
        if (!string.IsNullOrWhiteSpace(msg.MessageId))
            _messagesByServerId[msg.MessageId] = msg;
        if (!string.IsNullOrWhiteSpace(msg.ClientMessageId))
            _messagesByClientId[msg.ClientMessageId] = msg;
    }

    /// <summary>内存消息窗口上限：更旧记录保留在 SQLite（历史分页可重新加载）。</summary>
    private const int MaxMessageWindow = 500;

    /// <summary>
    /// 消息窗口裁剪：集合超过上限时从最旧端移除（Messages[0] 为最早消息），
    /// 同步淘汰两个查找索引——虚拟化只减少视觉控件，此裁剪控制 ViewModel 与字符串内存。
    /// 编辑/撤回对已淘汰消息：FindMessage 未命中即忽略（DB 层已持久化，无功能损失）。
    /// </summary>
    private void TrimMessageWindow()
    {
        var overflow = Messages.Count - MaxMessageWindow;
        if (overflow <= 0)
            return;
        for (var i = 0; i < overflow; i++)
        {
            var msg = Messages[0];
            Messages.RemoveAt(0);
            if (!string.IsNullOrWhiteSpace(msg.MessageId)
                && ReferenceEquals(_messagesByServerId.GetValueOrDefault(msg.MessageId), msg))
            {
                _messagesByServerId.Remove(msg.MessageId);
            }
            if (!string.IsNullOrWhiteSpace(msg.ClientMessageId)
                && ReferenceEquals(_messagesByClientId.GetValueOrDefault(msg.ClientMessageId), msg))
            {
                _messagesByClientId.Remove(msg.ClientMessageId);
            }
        }
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

        if (!TouchesCurrentConversation(update.SenderUserId, update.ReceiverUserId))
            return;

        PostIfCurrent(() => ApplyRecalled(update.MessageId, update.RecalledAtMs));
    }

    private void OnMessageEdited(object? sender, MessageEditedUpdateDto update)
    {
        if (string.IsNullOrWhiteSpace(update.MessageId))
            return;

        if (!TouchesCurrentConversation(update.SenderUserId, update.ReceiverUserId))
            return;

        PostIfCurrent(() =>
            ApplyEdited(update.MessageId, update.Content, update.EditVersion, update.EditedAtMs));
    }

    /// <summary>
    /// 判断消息更新（撤回/编辑）是否影响当前会话。
    /// 仅当更新涉及当前好友与当前用户双方时返回 true；无选中会话时放行。
    /// </summary>
    private bool TouchesCurrentConversation(long senderUserId, long receiverUserId)
    {
        if (CurrPeerId <= 0)
            return true;

        var peerId = CurrPeerId;
        var selfId = _chatSession.CurrentUserId;
        var touchesPeer = senderUserId == peerId || receiverUserId == peerId;
        var touchesSelf = senderUserId == selfId || receiverUserId == selfId;
        return touchesPeer && touchesSelf;
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

        ApplyRecalled(message.MessageId, null);
        if (_editingMessage is not null
            && string.Equals(_editingMessage.MessageId, message.MessageId, StringComparison.Ordinal))
        {
            ClearEditDraft();
        }
    }

    /// <summary>手动重试发送：Failed/Cancelled → Queued，交 OutboxProcessor 认领重发。</summary>
    private async Task RetryMessageAsync(Message? message, CancellationToken ct)
    {
        if (message is null || !message.IsSentByMe || string.IsNullOrWhiteSpace(message.ClientMessageId))
            return;
        var selfId = _currentUserContext.RequireUserId();
        var ok = await _dbService.RetryOutboxAsync(selfId, message.ClientMessageId).ConfigureAwait(true);
        if (!ok)
            return;
        TryAdvanceStatus(message, MessageStatus.Queued);
        CancelSendCommand.RaiseCanExecuteChanged();
        _eventBus.Publish(new OutboxStatusChangedEvent(message.ClientMessageId, OutboxStatus.Queued, null));
    }

    /// <summary>取消发送：Queued/Sending → Cancelled，OutboxProcessor 不再认领。</summary>
    private async Task CancelSendAsync(Message? message, CancellationToken ct)
    {
        if (message is null || !message.IsSentByMe || string.IsNullOrWhiteSpace(message.ClientMessageId))
            return;
        var selfId = _currentUserContext.RequireUserId();
        var ok = await _dbService.CancelOutboxAsync(selfId, message.ClientMessageId).ConfigureAwait(true);
        if (!ok)
            return;
        TryAdvanceStatus(message, MessageStatus.Failed);
        _eventBus.Publish(new OutboxStatusChangedEvent(message.ClientMessageId, OutboxStatus.Cancelled, null));
    }

    /// <summary>删除失败消息：清理 Outbox 记录并移除本地气泡（DB 消息行保留，历史仍可见）。</summary>
    private async Task DeleteFailedMessageAsync(Message? message, CancellationToken ct)
    {
        if (message is null || !message.IsSendFailed || string.IsNullOrWhiteSpace(message.ClientMessageId))
            return;
        var selfId = _currentUserContext.RequireUserId();
        await _dbService.DeleteOutboxAsync(selfId, message.ClientMessageId).ConfigureAwait(true);
        Messages.Remove(message);
        _messagesByClientId.Remove(message.ClientMessageId);
        if (!string.IsNullOrWhiteSpace(message.MessageId))
            _messagesByServerId.Remove(message.MessageId);
    }

    private void ApplyRecalled(string messageId, long? recalledAtMs)
    {
        var existing = FindMessage(messageId, null);
        existing?.TryApply(new MessageMutation(MessageMutationKind.Recall, RecalledAtMs: recalledAtMs));
        BeginEditMessageCommand.RaiseCanExecuteChanged();
    }

    private void ApplyEdited(string messageId, string content, int editVersion, long editedAtMs)
    {
        var existing = FindMessage(messageId, null);
        existing?.TryApply(new MessageMutation(
            MessageMutationKind.Edit, content, editVersion, editedAtMs));
    }

    /// <summary>
    /// 初始化当前会话（会话中心：以 LocalConversation 为数据源）。
    /// 无好友记录的直聊历史会话同样可打开（PeerUserId 缺失时仅浏览与发送受限）。
    /// </summary>
    public void Init(LocalConversation conversation)
    {
        // 强制 flush 上一会话完整草稿（在清空输入状态前捕获快照，异步落库不阻塞切换）
        if (CurrConversationId is not null && _currentUserContext.HasUserId)
        {
            var prevConv = CurrConversationId;
            var prevOwner = _currentUserContext.UserId!.Value;
            _ = SaveDraftSnapshotAsync(prevOwner, prevConv);
        }

        var isGroup = conversation.Type == (byte)ConversationTypeDto.Group;
        var targetPeerId = conversation.PeerUserId ?? 0;
        var previousPeerId = CurrPeerId;
        if (previousPeerId > 0 && previousPeerId != targetPeerId)
            _ = UnwatchPeerPresenceAsync(previousPeerId);

        // 取消上一会话所有进行中的加载/已读/同步任务，并提升代际。
        CancelConversationOperations();
        var generation = ++_conversationGeneration;
        var ct = _conversationCts.Token;

        CurrPeerId = targetPeerId;
        CurrDestination = new Core.Models.DTO.MessageDestination(
            conversation.ConversationId,
            isGroup ? ConversationTypeDto.Group : ConversationTypeDto.Direct,
            conversation.PeerUserId);
        try
        {
            CurrConversationId = string.IsNullOrWhiteSpace(conversation.ConversationId)
                ? ConversationId.CreateDirect(_currentUserContext.RequireUserId(), targetPeerId)
                : conversation.ConversationId;
        }
        catch
        {
            CurrConversationId = null;
        }
        PeerTitle = conversation.Title;
        PeerIsOnline = conversation.PeerIsOnline;
        IsPeerTyping = false;
        CancelPeerTypingClear();
        ClearMessages();
        // 加载新会话草稿（从 DB 恢复上次未发送的输入）
        // 历史未加载完成前不显示空态（见 IsMessageListEmpty）。
        _historyRendered = false;
        OnPropertyChanged(nameof(IsMessageListEmpty));
        _ = LoadDraftAsync(CurrConversationId, generation, ct);
        ClearPendingAttachment();
        ClearReplyDraft();
        ClearEditDraft();
        _typingActive = false;
        UploadProgress = 0;
        IsUploading = false;
        AttachFileCommand.RaiseCanExecuteChanged();
        Log.Debug("MessageView 初始化: 会话={Title}", conversation.Title);
        if (CurrPeerId > 0)
            _ = RefreshPeerPresenceAsync(CurrPeerId);

        // 顺序：加载本地最新页 → 渲染 → 标记实际最后可见消息已读 → 后台同步缺失消息。
        // 全程校验代际，代际不匹配即放弃，避免快速切换 A→B 时 A 的结果污染 B。
        _ = InitializeConversationAsync(CurrConversationId, generation, ct);
    }

    /// <summary>
    /// 从 DB 加载会话草稿并设置到 NewMessage。校验代际，避免快速切换 A→B 时 A 的草稿覆盖 B。
    /// </summary>
    private async Task LoadDraftAsync(string? conversationId, long generation, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(conversationId) || !_currentUserContext.HasUserId)
        {
            _newMessage = string.Empty;
            OnPropertyChanged(nameof(NewMessage));
            return;
        }
        try
        {
            var conv = await _dbService.GetConversationAsync(_currentUserContext.UserId!.Value, conversationId).ConfigureAwait(true);
            if (generation != _conversationGeneration) return;
            _newMessage = conv?.Draft ?? string.Empty;
            OnPropertyChanged(nameof(NewMessage));
            RestoreDraftState(conv?.DraftState, generation);
        }
        catch
        {
            _newMessage = string.Empty;
            OnPropertyChanged(nameof(NewMessage));
        }
    }

    /// <summary>
    /// 恢复完整草稿状态：文本、回复目标、编辑目标、待发送附件。
    /// 附件已上传到服务端，仅恢复元数据即可直接发送。
    /// </summary>
    private void RestoreDraftState(string? json, long generation)
    {
        if (generation != _conversationGeneration || string.IsNullOrWhiteSpace(json))
            return;

        DraftState? state;
        try
        {
            state = JsonSerializer.Deserialize<DraftState>(json);
        }
        catch
        {
            Log.Warning("草稿状态解析失败，忽略恢复");
            return;
        }
        if (state is null)
            return;

        _draftRevision = state.Revision;
        _draftUpdatedAtMs = state.UpdatedAtMs;

        if (state.Attachments is { Count: > 0 })
        {
            foreach (var a in state.Attachments)
                AddPendingAttachment(new PendingAttachment
                {
                    AttachmentId = a.AttachmentId,
                    FileName = a.FileName,
                    ContentType = a.ContentType,
                    SizeBytes = a.SizeBytes,
                    IsVoice = a.IsVoice,
                    VoiceCodec = a.VoiceCodec,
                    VoiceContainer = a.VoiceContainer,
                    VoiceDurationMs = a.VoiceDurationMs,
                    VoiceSampleRateHz = a.VoiceSampleRateHz,
                    VoiceChannels = a.VoiceChannels,
                    VoiceWaveformPeaks = a.VoiceWaveformPeaks
                });
        }

        if (state.ReplyTarget is { } reply)
        {
            _replyToMessageId = reply.MessageId;
            _replyToSenderUserId = reply.SenderUserId;
            ReplyDraftPreview = reply.Preview ?? "原消息";
        }

        if (state.EditTarget is { } edit)
        {
            _editingMessage = new Message
            {
                MessageId = edit.MessageId,
                Content = edit.Content,
                EditVersion = edit.EditVersion,
                IsSentByMe = true,
                Sender = EmptyUser
            };
            OnPropertyChanged(nameof(HasEditDraft));
            OnPropertyChanged(nameof(EditDraftPreview));
            OnPropertyChanged(nameof(SendButtonText));
            ClearEditDraftCommand.RaiseCanExecuteChanged();
        }
    }

    // ── 完整草稿保存（500ms 防抖 + 切换/退出/关闭强制 flush） ──────────

    private const int DraftDebounceMs = 500;
    private CancellationTokenSource? _draftSaveCts;
    private bool _draftDirty;
    private int _draftRevision;
    private long _draftUpdatedAtMs;

    /// <summary>输入状态变化：取消进行中的防抖定时器，500ms 后保存完整草稿。</summary>
    private void ScheduleDraftSave()
    {
        _draftDirty = true;
        _draftSaveCts?.Cancel();
        _draftSaveCts?.Dispose();
        _draftSaveCts = new CancellationTokenSource();
        var token = _draftSaveCts.Token;
        _ = Task.Delay(DraftDebounceMs, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
                _ = SaveDraftAsync();
        }, TaskScheduler.Default);
    }

    /// <summary>防抖保存：捕获当前输入状态，序列化为 DraftState 写入 DB。</summary>
    private async Task SaveDraftAsync()
    {
        if (!_draftDirty)
            return;
        await SaveDraftSnapshotAsync(_currentUserContext.UserId, CurrConversationId).ConfigureAwait(false);
    }

    /// <summary>
    /// 强制保存指定会话的草稿快照（切换会话/退出登录/窗口关闭时调用）。
    /// 捕获调用时点上的完整输入状态，与后续界面变化无关。
    /// </summary>
    private async Task SaveDraftSnapshotAsync(long? ownerUserId, string? conversationId)
    {
        if (ownerUserId is not long owner || string.IsNullOrEmpty(conversationId))
            return;
        _draftSaveCts?.Cancel();

        var text = _newMessage;
        DraftReplyTarget? reply = null;
        if (_replyToMessageId is { } replyId)
        {
            reply = new DraftReplyTarget
            {
                MessageId = replyId,
                Preview = _replyDraftPreview,
                SenderUserId = _replyToSenderUserId
            };
        }

        DraftEditTarget? edit = null;
        if (_editingMessage is { MessageId: not null } em)
        {
            edit = new DraftEditTarget
            {
                MessageId = em.MessageId,
                Content = em.Content,
                EditVersion = em.EditVersion
            };
        }

        List<DraftAttachment>? attachments = null;
        if (_pendingAttachments.Count > 0)
        {
            attachments = [.. _pendingAttachments.Select(a => new DraftAttachment
            {
                AttachmentId = a.AttachmentId,
                FileName = a.FileName,
                ContentType = a.ContentType,
                SizeBytes = a.SizeBytes,
                IsVoice = a.IsVoice,
                VoiceCodec = a.VoiceCodec,
                VoiceContainer = a.VoiceContainer,
                VoiceDurationMs = a.VoiceDurationMs,
                VoiceSampleRateHz = a.VoiceSampleRateHz,
                VoiceChannels = a.VoiceChannels,
                VoiceWaveformPeaks = a.VoiceWaveformPeaks
            })];
        }

        var updatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var revision = _draftRevision + 1;
        var state = new DraftState
        {
            Text = text,
            ReplyTarget = reply,
            EditTarget = edit,
            Attachments = attachments,
            UpdatedAtMs = updatedAtMs,
            Revision = revision
        };

        try
        {
            var written = await _dbService.UpdateConversationDraftAsync(
                owner, conversationId, text, JsonSerializer.Serialize(state), updatedAtMs, revision)
                .ConfigureAwait(false);
            if (written)
            {
                _draftRevision = revision;
                _draftUpdatedAtMs = updatedAtMs;
                _draftDirty = false;
            }
            else
            {
                // 数据库已有更新版本（多窗口场景）：丢弃本地版本，避免旧草稿覆盖新草稿。
                Log.Warning("草稿写入被忽略（数据库已有更新版本），会话: {ConversationId}", conversationId);
                _draftDirty = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "保存草稿失败，会话: {ConversationId}", conversationId);
        }
    }

    /// <summary>强制 flush 当前会话草稿（退出登录/窗口关闭时调用）。</summary>
    public Task FlushDraftAsync() => SaveDraftAsync();

    private async Task InitializeConversationAsync(string? conversationId, long generation, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(conversationId))
            return;

        // 1. 加载本地最新页并渲染
        try
        {
            var history = await _messageStore.LoadHistoryAsync(_chatSession.CurrentSession, conversationId, limit: 100, ct: ct)
                .ConfigureAwait(true);
            if (generation != _conversationGeneration || ct.IsCancellationRequested)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _conversationGeneration)
                    return;
                // 历史页批量插入：单次 CollectionChanged，避免逐条 UI 重渲染
                var batch = new List<Message>(history.Count);
                foreach (var lm in history)
                {
                    var ui = ToUiMessage(lm);
                    if (ui is not null)
                        batch.Add(ui);
                }
                Messages.AddRange(batch);
                // 历史页已渲染（含空会话）：此时才允许显示空态。
                _historyRendered = true;
                OnPropertyChanged(nameof(IsMessageListEmpty));
            });
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "加载历史消息失败");
        }

        if (generation != _conversationGeneration)
            return;

        // 2. 渲染完成后，标记实际最后可见消息已读（不再读取空集合）
        await MarkCurrentConversationReadAsync(generation, ct);

        if (generation != _conversationGeneration)
            return;

        // 3. 后台同步缺失消息（catch-up），不阻塞 UI
        _ = SyncMissingHistoryAsync(conversationId, generation, ct);
    }

    private async Task SyncMissingHistoryAsync(string conversationId, long generation, CancellationToken ct)
    {
        try
        {
            await _messageStore.FetchAndPersistHistoryAsync(_chatSession.CurrentSession, conversationId, limit: 50, ct: ct)
                .ConfigureAwait(true);
            if (generation != _conversationGeneration)
                return;

            // 同步拉取的新消息补充到 UI（仅当仍是当前会话）
            var fresh = await _messageStore.LoadHistoryAsync(_chatSession.CurrentSession, conversationId, limit: 100, ct: ct)
                .ConfigureAwait(true);
            if (generation != _conversationGeneration)
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _conversationGeneration)
                    return;
                // 增量 diff：就地更新已存在消息（状态/撤回/编辑），仅追加尚未展示的新消息
                ApplyHistoryDiff(fresh);
            });
        }
        catch (OperationCanceledException)
        {
            // 切换会话时正常取消
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "后台同步缺失消息失败 ConversationId={ConversationId}", conversationId);
        }
    }

    private void CancelConversationOperations()
    {
        try
        {
            _conversationCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }
        _conversationCts = new CancellationTokenSource();
    }

    private void OnPeerReadWatermarkAdvanced(PeerReadWatermarkAdvancedEvent e)
    {
        if (CurrConversationId is null || !string.Equals(e.ConversationId, CurrConversationId, StringComparison.Ordinal))
            return;

        // 仅对端真实已读（服务端 103/105 序列水位）才推进已读展示；
        // 本地打开会话清未读（LocalUnreadClearedEvent）不得伪造对端已读。
        var cutoff = DateTimeOffset.FromUnixTimeMilliseconds(e.ReadAtMs).LocalDateTime;
        PostIfCurrent(() =>
        {
            foreach (var m in Messages)
            {
                if (m.IsSentByMe && m.Status == MessageStatus.Sent && m.Timestamp <= cutoff)
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

    private async Task MarkCurrentConversationReadAsync(long generation, CancellationToken ct)
    {
        // 直聊与群聊都按会话标记已读（群聊无对端用户，不要求 CurrPeerId）。
        if (CurrConversationId is null)
            return;
        try
        {
            var lastMessage = Messages.LastOrDefault();
            if (generation != _conversationGeneration)
                return;
            await _messageStore.MarkConversationReadAndNotifyAsync(
                _chatSession.CurrentSession,
                CurrConversationId,
                lastMessage?.MessageId,
                ct);
        }
        catch (OperationCanceledException)
        {
            // 切换会话时正常取消
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
                _chatSession.CurrentSession,
                CurrConversationId,
                limit: 50,
                beforeReceivedAtMs: oldest.ReceivedAtMs,
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
                        InsertMessage(0, uiMsg);
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

        IReadOnlyList<AttachmentRefDto>? attachments = AttachmentJson.Deserialize(lm.AttachmentsJson);

        var msg = new Message
        {
            MessageId = lm.MessageId,
            ClientMessageId = lm.ClientMessageId,
            Content = lm.Content ?? string.Empty,
            Timestamp = ts,
            ReceivedAtMs = lm.ReceivedAtMs,
            IsSentByMe = lm.SenderUserId == selfId,
            SenderUserId = lm.SenderUserId,
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
        {
            msg.TryApply(new MessageMutation(MessageMutationKind.Recall, RecalledAtMs: lm.RecalledAtMs));
        }
        // IsEdited 由 EditVersion/EditedAtMs 自动推导，无需手动赋值。

        return msg;
    }

    /// <summary>将同步 catch-up 消息合并进当前打开的会话（按 MessageId 去重）。</summary>
    public void ApplyCatchUp(IReadOnlyList<MessageHistoryItemDto> items)
    {
        // 群聊无对端用户（CurrPeerId=0）：按会话匹配即可应用。
        if (items.Count == 0 || CurrConversationId is null)
            return;

        if (CurrConversationId is not null)
            _ = _messageStore.PersistHistoryAsync(_chatSession.CurrentSession, CurrConversationId, items);

        var isGroup = IsGroupConversation;
        var peerId = CurrPeerId;
        var selfId = _chatSession.CurrentUserId;

        // 去重依赖 FindMessage 的 _messagesByServerId 索引：已存在则更新，本次循环新追加的也会被后续 FindMessage 命中。
        // 避免每次构建 knownIds HashSet 的 O(Messages) 分配。
        foreach (var item in items.OrderBy(i => i.ReceivedAtMs).ThenBy(i => i.MessageId, StringComparer.Ordinal))
        {
            if (!isGroup)
            {
                // 直聊过滤：消息必须涉及当前对端与当前用户双方。
                if (item.SenderUserId != peerId && item.ReceiverUserId != peerId)
                    continue;
                if (item.SenderUserId != selfId && item.ReceiverUserId != selfId)
                    continue;
            }
            else if (!string.IsNullOrWhiteSpace(item.ConversationId)
                     && item.ConversationId != CurrConversationId)
            {
                // 群聊过滤：消息必须属于当前会话（保留实际发送者，不做对端过滤）。
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.MessageId))
            {
                var existingMessage = FindMessage(item.MessageId, null);
                if (existingMessage is not null)
                {
                    if (item.RecalledAtMs is > 0)
                    {
                        existingMessage.TryApply(new MessageMutation(
                            MessageMutationKind.Recall, RecalledAtMs: item.RecalledAtMs));
                    }
                    else if (item.EditVersion > 1 || item.EditedAtMs is > 0)
                    {
                        existingMessage.TryApply(new MessageMutation(
                            MessageMutationKind.Edit,
                            item.Content?.Trim() ?? string.Empty,
                            item.EditVersion,
                            item.EditedAtMs));
                    }
                    continue;
                }
            }

            var message = new Message
            {
                MessageId = item.MessageId,
                Content = item.Content?.Trim() ?? string.Empty,
                Timestamp = DateTimeOffset.FromUnixTimeMilliseconds(item.ReceivedAtMs).LocalDateTime,
                ReceivedAtMs = item.ReceivedAtMs,
                IsSentByMe = item.SenderUserId == selfId,
                SenderUserId = item.SenderUserId,
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
            {
                message.TryApply(new MessageMutation(MessageMutationKind.Recall, RecalledAtMs: item.RecalledAtMs));
            }
            AddMessage(message);
        }
    }

    /// <summary>
    /// 将 DB 加载的最新页增量合并进当前会话：就地更新已存在消息的字段（状态/撤回/编辑），
    /// 仅追加尚未展示的新消息。不删除已有消息——fresh 仅为最新页，向上分页加载的更早消息不在其中。
    /// 保留已存在消息的对象引用与 UI 容器，避免 Clear+Add 重建。
    /// </summary>
    private void ApplyHistoryDiff(IReadOnlyList<LocalMessage> fresh)
    {
        if (fresh.Count == 0)
            return;

        // 单循环：用已有 _messagesByServerId/_messagesByClientId 索引 O(1) 查找，
        // 命中则就地更新（保留 UI 容器），未命中则追加。避免每次构建临时字典。
        foreach (var lm in fresh)
        {
            var existing = FindMessage(lm.MessageId, lm.ClientMessageId);
            if (existing is not null)
            {
                ApplyDbStateToMessage(existing, lm);
                continue;
            }

            var ui = ToUiMessage(lm);
            if (ui is not null)
                AddMessage(ui);
        }
    }

    /// <summary>
    /// 严格状态机推进：仅接受 <see cref="MessageStatusTransitions"/> 定义的合法转换，
    /// 已撤回消息不会被打回。
    /// </summary>
    private static bool TryAdvanceStatus(Message target, MessageStatus newStatus)
    {
        if (target.Status == newStatus)
            return true;
        if (!MessageStatusTransitions.CanTransition(target.Status, newStatus))
            return false;
        target.Status = newStatus;
        return true;
    }

    /// <summary>将 DB 最新状态同步到已存在的 UI 消息（状态/撤回/编辑单调推进，不回退）。</summary>
    private static void ApplyDbStateToMessage(Message target, LocalMessage src)
    {
        // 撤回具有最高优先级
        if ((src.RecalledAtMs is > 0 || src.Status == MessageStatus.Recalled) && !target.IsRecalled)
        {
            target.TryApply(new MessageMutation(MessageMutationKind.Recall, RecalledAtMs: src.RecalledAtMs));
            return;
        }

        // 状态单调推进：仅接受状态机的合法转换（Failed 是发送分支状态，不覆盖 Read）
        if (src.Status != target.Status && MessageStatusTransitions.CanTransition(target.Status, src.Status))
            target.Status = src.Status;

        // 编辑版本单调递增：仅当 DB 编辑版本更新时应用（TryApply 内部拒绝旧版本）
        var dbEditVersion = src.EditVersion > 0 ? src.EditVersion : 1;
        if (dbEditVersion > target.EditVersion)
        {
            target.TryApply(new MessageMutation(
                MessageMutationKind.Edit, src.Content ?? string.Empty, dbEditVersion, src.EditedAtMs));
        }
    }

    private async Task SendMessage(CancellationToken ct)
    {
        // 群聊无对端用户（CurrPeerId=0），按会话寻址即可发送。
        if (CurrConversationId is null)
            return;
        if (CurrDestination is null)
            return;

        var text = NewMessage?.Trim();
        var hasText = !string.IsNullOrWhiteSpace(text);
        var hasAttachments = _pendingAttachments.Count > 0;
        if (!hasText && !hasAttachments) return;

        // 离线发送放开：纯文本消息即使未连接也事务化写入 Outbox+LocalMessage，
        // 由 OutboxProcessor 在恢复连接后认领补发。
        // 附件消息的附件文件尚未上传到服务端，离线时无法发送。
        if (!_chatSession.IsConnected || !_chatSession.IsAuthenticated)
        {
            if (hasAttachments)
            {
                _notificationService.ShowError("未连接到服务器或未鉴权，带附件的消息暂无法发送。");
                return;
            }
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
                DownloadApiHint = a.AttachmentId,
                // VOICE-MSG-2：语音附件携带编解码/容器/时长/采样率/声道元数据。
                IsVoice = a.IsVoice,
                VoiceCodec = a.IsVoice ? a.VoiceCodec : null,
                VoiceContainer = a.IsVoice ? a.VoiceContainer : null,
                VoiceDurationMs = a.IsVoice ? a.VoiceDurationMs : null,
                VoiceSampleRateHz = a.IsVoice ? a.VoiceSampleRateHz : null,
                VoiceChannels = a.IsVoice ? a.VoiceChannels : null,
                // 波形峰值包络（0–255）：48 字节，STJ 自动 base64；缺省/空 = 接收端降级渲染。
                VoiceWaveformPeaks = a.IsVoice ? a.VoiceWaveformPeaks : null
            }).ToList();
        }

        var replyMessageId = _replyToMessageId;
        var replySenderId = _replyToSenderUserId;
        var replyPreview = _replyDraftPreview;

        var selfId = _currentUserContext.RequireUserId();
        var conversationId = CurrConversationId
            ?? ConversationId.CreateDirect(selfId, CurrPeerId);
        var nowUtc = DateTime.UtcNow;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var attachmentIdsJson = hasAttachments ? AttachmentJson.SerializeIds(attachmentIds) : null;
        var attachmentsJson = hasAttachments ? AttachmentJson.Serialize(attachments) : null;

        var clientMessageId = Guid.CreateVersion7().ToString("N");
        var targetUserId = CurrDestination?.PeerUserId; // 群聊为空（按会话寻址）

        var outbox = new LocalOutboxMessage
        {
            OwnerUserId = selfId,
            ClientMessageId = clientMessageId,
            ConversationId = conversationId,
            ConversationType = (byte)(CurrDestination?.Type ?? ConversationTypeDto.Direct),
            TargetUserId = targetUserId,
            Content = text,
            AttachmentIdsJson = attachmentIdsJson,
            ReplyToMessageId = replyMessageId,
            ReplyToSenderUserId = replySenderId,
            ReplyToPreview = replyPreview,
            Status = OutboxStatus.Queued,
            QueuedAt = nowUtc
        };

        var localMessage = new LocalMessage
        {
            OwnerUserId = selfId,
            ClientMessageId = clientMessageId,
            ConversationId = conversationId,
            SenderUserId = selfId,
            ReceiverUserId = targetUserId ?? 0,
            Content = text ?? string.Empty,
            ReceivedAtMs = nowMs,
            AttachmentsJson = attachmentsJson,
            ReplyToMessageId = replyMessageId,
            ReplyToSenderUserId = replySenderId,
            ReplyToPreview = replyPreview,
            Status = MessageStatus.Queued,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc
        };

        // 单事务写入 Outbox + LocalMessage（事务化 Outbox）：先持久化再发送
        try
        {
            await _dbService.EnqueueOutboxWithMessageAsync(outbox, localMessage).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "事务性写入 Outbox+Message 失败 ClientMessageId={ClientMessageId}", clientMessageId);
            _notificationService.ShowError($"消息保存失败: {ex.Message}");
            return;
        }

        AddMessage(new Message
        {
            ClientMessageId = clientMessageId,
            Content = text ?? string.Empty,
            Timestamp = DateTime.Now,
            IsSentByMe = true,
            Status = MessageStatus.Queued,
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

        // UI 仅事务化入库，网络发送全部由 OutboxProcessor 执行；
        // 发布事件提示排空器立即发送，后续状态经 OutboxStatusChangedEvent 回流 UI。
        _eventBus.Publish(new OutboxEnqueuedEvent(clientMessageId, conversationId, targetUserId ?? 0));
    }

    /// <summary>
    /// 转发消息到指定会话：仅文本（或附件摘要），带 ForwardedFrom*，不含附件与回复。
    /// 调用时当前会话仍应为来源会话（先发送再 Init 目标会话）。
    /// 返回可插入目标会话的本地气泡（调用方在 Init 之后添加）。
    /// </summary>
    public async Task<Message?> ExecuteForwardAsync(
        LocalConversation target,
        Message source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        var isGroupTarget = target.Type == (byte)ConversationTypeDto.Group;
        var targetPeerId = target.PeerUserId ?? 0;
        // 群聊无对端用户：按会话寻址即可转发；直聊要求有对端。
        if (!isGroupTarget && targetPeerId <= 0)
        {
            _notificationService.ShowError("无法转发到该会话。");
            return null;
        }

        if (source.IsRecalled || string.IsNullOrWhiteSpace(source.MessageId))
        {
            _notificationService.ShowError("无法转发该消息。");
            return null;
        }

        // 原消息发送方：优先取消息携带的发送者 Id（群聊来源消息保留实际发送者），
        // 直聊来源且无 Id 时回退当前对端。
        var forwardSenderId = source.IsSentByMe
            ? _chatSession.CurrentUserId
            : (source.SenderUserId > 0
                ? source.SenderUserId
                : CurrPeerId);

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
            ? (source.HasAttachments ? PreviewText.Truncate(source.AttachmentSummary, 80) : "原消息")
            : PreviewText.Truncate(source.Content, 80);

        var selfId = _currentUserContext.RequireUserId();
        var conversationId = string.IsNullOrWhiteSpace(target.ConversationId)
            ? ConversationId.CreateDirect(selfId, targetPeerId)
            : target.ConversationId;
        var nowUtc = DateTime.UtcNow;
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var clientMessageId = Guid.CreateVersion7().ToString("N");

        var outbox = new LocalOutboxMessage
        {
            OwnerUserId = selfId,
            ClientMessageId = clientMessageId,
            ConversationId = conversationId,
            ConversationType = (byte)(isGroupTarget
                ? ConversationTypeDto.Group
                : ConversationTypeDto.Direct),
            TargetUserId = isGroupTarget ? null : targetPeerId,
            Content = content,
            ForwardedFromMessageId = source.MessageId,
            ForwardedFromSenderUserId = forwardSenderId,
            ForwardedFromPreview = forwardPreview,
            Status = OutboxStatus.Queued,
            QueuedAt = nowUtc
        };

        var localMessage = new LocalMessage
        {
            OwnerUserId = selfId,
            ClientMessageId = clientMessageId,
            ConversationId = conversationId,
            SenderUserId = selfId,
            ReceiverUserId = isGroupTarget ? 0 : targetPeerId,
            Content = content,
            ReceivedAtMs = nowMs,
            ForwardedFromMessageId = source.MessageId,
            ForwardedFromSenderUserId = forwardSenderId,
            ForwardedFromPreview = forwardPreview,
            Status = MessageStatus.Queued,
            CreatedAt = nowUtc,
            UpdatedAt = nowUtc
        };

        // 单事务写入 Outbox + LocalMessage（事务化 Outbox）：先持久化再发送
        try
        {
            await _dbService.EnqueueOutboxWithMessageAsync(outbox, localMessage).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "事务性写入转发 Outbox+Message 失败 ClientMessageId={ClientMessageId}", clientMessageId);
            _notificationService.ShowError($"转发消息保存失败: {ex.Message}");
            return null;
        }

        // UI 仅事务化入库，网络发送全部由 OutboxProcessor 执行；
        // 发布事件提示排空器立即发送，后续状态经 OutboxStatusChangedEvent 回流 UI。
        _eventBus.Publish(new OutboxEnqueuedEvent(clientMessageId, conversationId, targetPeerId));

        return new Message
        {
            ClientMessageId = clientMessageId,
            Content = content,
            Timestamp = DateTime.Now,
            IsSentByMe = true,
            Status = MessageStatus.Queued,
            Sender = EmptyUser,
            ForwardedFromMessageId = source.MessageId,
            ForwardedFromSenderUserId = forwardSenderId,
            ForwardedFromPreview = forwardPreview
        };
    }

    private async Task AttachFileAsync(CancellationToken ct)
    {
        // 群聊无对端用户（CurrPeerId=0）：附件选择按会话可用。
        if (CurrConversationId is null) return;
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

        // 文件类型限制：拦截可执行文件、脚本等高风险类型（附件安全策略）
        var pickedExtension = Path.GetExtension(picked.FileName).ToLowerInvariant();
        if (BlockedExtensions.Contains(pickedExtension))
        {
            _notificationService.ShowError($"不支持此文件类型: {pickedExtension}");
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
            // 九3: 复制到临时文件时同步增量计算 SHA-256，源流仅读取一次（避免先算hash再复制的重复IO）。
            string? sha256 = null;
            clientAttachmentId = Guid.NewGuid().ToString("N");
            try
            {
                await using (var sourceStream = await picked.OpenReadAsync(ct).ConfigureAwait(true))
                {
                    var (relPath, hash) = await _storage.WriteToUploadingWithHashAsync(_currentUserContext.RequireUserId(), sourceStream, picked.FileName, ct).ConfigureAwait(true);
                    uploadingRelativePath = relPath;
                    sha256 = hash;
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to write local temp file");
                _notificationService.ShowError("Failed to save attachment temp file.");
                return;
            }

            // 去重检查
            if (sha256 is not null)
            {
                try
                {
                    var existing = await _dbService.GetAttachmentBySha256Async(_currentUserContext.RequireUserId(), sha256).ConfigureAwait(true);
                    if (existing is not null && !string.IsNullOrWhiteSpace(existing.AttachmentId))
                    {
                        // 命中去重，删除刚创建的临时文件
                        _storage.DeleteUploadingFile(_currentUserContext.RequireUserId(), uploadingRelativePath);
                        uploadingRelativePath = null;
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
            if (uploadingRelativePath is null)
            {
                Log.Warning("Attachment temp file path is missing");
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
                    Status = AttachmentStatus.Uploading,
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
            await using (var uploadStream = _storage.OpenUploadingRead(_currentUserContext.RequireUserId(), uploadingRelativePath))
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

            // 九2: 上传成功后将临时文件转为下载缓存，避免自己刚上传的文件再次打开仍需网络下载。
            string? localCachePath = null;
            try
            {
                localCachePath = _storage.MoveToDownloads(_currentUserContext.RequireUserId(), uploadingRelativePath, result.AttachmentId, result.OriginalName ?? picked.FileName);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to move uploaded file to downloads cache");
                _storage.DeleteUploadingFile(_currentUserContext.RequireUserId(), uploadingRelativePath);
            }
            uploadingRelativePath = null;

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
                    LocalCachePath = localCachePath,
                    Status = AttachmentStatus.Available,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                }).ConfigureAwait(true);
                await _dbService.UpdateAttachmentUploadPathAsync(owner, clientAttachmentId, localUploadingPath: null).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to update attachment metadata to Available");
            }
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

    /// <summary>
    /// 结束录音并上传为语音附件（VOICE-MSG-2）：Stop → 取 WAV → 复用
    /// <see cref="IAttachmentClientService.UploadAndConfirmAsync"/> 上传 → 作为语音
    /// 附件加入草稿 → 立即发送。codec=pcm、container=wav。
    /// </summary>
    private async Task SendVoiceAsync(CancellationToken ct)
    {
        if (CurrConversationId is null)
        {
            _voiceRecorder.Cancel();
            return;
        }

        var recording = _voiceRecorder.Stop();
        await SendVoiceRecordingAsync(recording, ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 上传录音为语音附件并发送（VOICE-MSG-2）：取 WAV → 复用
    /// <see cref="IAttachmentClientService.UploadAndConfirmAsync"/> 上传 → 作为语音
    /// 附件加入草稿 → 立即发送。codec=pcm、container=wav。
    /// 由录音结束命令（<see cref="SendVoiceAsync"/>）与自动收尾（超时）共用。
    /// <paramref name="recording"/> 为 null 表示无有效录音（放弃或尚未开始）。
    /// </summary>
    private async Task SendVoiceRecordingAsync(VoiceRecording? recording, CancellationToken ct)
    {
        RecordingDurationText = "0:00";
        OnPropertyChanged(nameof(IsRecording));
        SendRecordingCommand.RaiseCanExecuteChanged();
        CancelRecordingCommand.RaiseCanExecuteChanged();

        if (recording is null)
        {
            _notificationService.ShowError("录音为空，无法发送。");
            return;
        }

        using (recording)
        {
            var contentType = recording.Metadata.Container == "wav"
                ? "audio/wav"
                : $"audio/{recording.Metadata.Container}";
            var fileName = $"voice-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.{recording.Metadata.Container}";

            IsUploading = true;
            UploadProgress = 0;
            string? clientAttachmentId = null;
            try
            {
                clientAttachmentId = Guid.NewGuid().ToString("N");
                var progress = new Progress<AttachmentUploadProgress>(p =>
                {
                    Dispatcher.UIThread.Post(() => UploadProgress = p.Percent);
                });
                var wavStream = recording.WavStream;
                wavStream.Position = 0;
                var result = await _attachments.UploadAndConfirmAsync(
                        wavStream, contentType, wavStream.Length, fileName,
                        clientAttachmentId: clientAttachmentId, progress, maxAttempts: 3, sha256: null, ct)
                    .ConfigureAwait(true);

                AddPendingAttachment(new PendingAttachment
                {
                    AttachmentId = result.AttachmentId,
                    FileName = result.OriginalName ?? fileName,
                    ContentType = result.ContentType,
                    SizeBytes = result.SizeBytes,
                    IsVoice = true,
                    VoiceCodec = recording.Metadata.Codec,
                    VoiceContainer = recording.Metadata.Container,
                    VoiceDurationMs = recording.Metadata.DurationMs,
                    VoiceSampleRateHz = recording.Metadata.SampleRateHz,
                    VoiceChannels = recording.Metadata.Channels,
                    VoiceWaveformPeaks = recording.Metadata.VoiceWaveformPeaks
                });
                UploadProgress = 100;
            }
            catch (OperationCanceledException)
            {
                if (!string.IsNullOrEmpty(clientAttachmentId))
                    await _attachments.AbandonAsync(clientAttachmentId, CancellationToken.None).ConfigureAwait(true);
                _notificationService.ShowError("语音上传已取消。");
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "语音上传失败 ClientAttachmentId={ClientAttachmentId}", clientAttachmentId);
                if (!string.IsNullOrEmpty(clientAttachmentId))
                    await _attachments.AbandonAsync(clientAttachmentId, CancellationToken.None).ConfigureAwait(true);
                _notificationService.ShowError($"语音上传失败: {ex.Message}");
                return;
            }
            finally
            {
                IsUploading = false;
            }
        }

        // 语音作为附件已加入草稿，复用统一发送路径（文本为空、仅附件语音可发送）。
        await SendMessage(ct).ConfigureAwait(true);
    }

    /// <summary>
    /// 语音播放（VOICE-MSG-2）：点击语音气泡时下载 WAV 到本地缓存并播放。
    /// 再次点击：若为当前播放项则暂停/恢复切换；若为其他项则切换播放。<see cref="IAudioPlayer"/>
    /// 负责进度上报与停止复位，本方法只负责"取缓存路径 → 交给播放器"。
    /// </summary>
    private async Task PlayVoiceAsync(AttachmentRefDto? attachment, CancellationToken ct)
    {
        if (attachment is null || !attachment.IsVoice || string.IsNullOrWhiteSpace(attachment.AttachmentId))
            return;

        // 同一附件：暂停/恢复切换。
        if (_audioPlayer.IsPlaying && _audioPlayer.CurrentKey == attachment.AttachmentId)
        {
            _audioPlayer.Pause();
            return;
        }
        if (_audioPlayer.CurrentKey == attachment.AttachmentId)
        {
            _audioPlayer.Resume();
            return;
        }
        // 其他附件：停止当前，切换播放。
        if (_audioPlayer.IsPlaying)
            _audioPlayer.Stop();

        var fileName = !string.IsNullOrWhiteSpace(attachment.FileName)
            ? attachment.FileName
            : $"{attachment.AttachmentId}.wav";

        string? path;
        try
        {
            path = await _downloadService.GetOrDownloadAsync(
                    attachment.AttachmentId, fileName, attachment.DownloadApiHint, ct)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 手动切换/取消：复位，不提示。
            return;
        }
        catch (Exception ex)
        {
            // 降级策略：下载失败（断网/过期附件/服务端不可用）不复位播放器为"播放中"，
            // 给出明确终态并复位，避免 UI 卡在伪播放状态。
            _audioPlayer.Stop();
            Log.Warning(ex, "语音下载失败 AttachmentId={AttachmentId}", attachment.AttachmentId);
            _notificationService.ShowError("语音加载失败，请稍后重试。");
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            _audioPlayer.Stop();
            _notificationService.ShowError("语音加载失败，请稍后重试。");
            return;
        }

        // 重启后首次播放前应用持久化的输出设备偏好（用户在设置页改选时已即时生效，此处兜底）。
        await ApplyPersistedAudioOutputAsync().ConfigureAwait(true);

        _audioPlayer.Play(attachment.AttachmentId, path);
    }

    /// <summary>
    /// 把持久化的音频输出设备偏好（<see cref="ClientSettings.AudioOutputDeviceId"/>）应用到播放器。
    /// 每进程仅执行一次设置读取；读取失败静默回退系统默认，不阻塞播放。
    /// </summary>
    private async Task ApplyPersistedAudioOutputAsync()
    {
        if (_audioOutputPreferenceApplied)
            return;
        _audioOutputPreferenceApplied = true;

        try
        {
            if (_settingsService is null || _currentUserContext is null || !_currentUserContext.TryGetUserId(out var owner))
                return;
            var settings = await _settingsService.GetAsync(owner).ConfigureAwait(true);
            _audioPlayer.SelectOutputDevice(settings.AudioOutputDeviceId);
        }
        catch (Exception ex)
        {
            // 偏好读取失败不阻断播放（回退系统默认），仅记录。
            Log.Debug(ex, "应用音频输出设备偏好失败，使用系统默认");
        }
    }

    private static string FormatVoiceTime(TimeSpan t)
    {
        var totalSec = (int)Math.Max(0, t.TotalSeconds);
        return $"{totalSec / 60}:{totalSec % 60:00}";
    }

    private async Task MarkAttachmentFailedAsync(string? clientAttachmentId, string? uploadingRelativePath, string reason)
    {
        if (string.IsNullOrEmpty(clientAttachmentId)) return;
        try
        {
            await _dbService.UpdateAttachmentStatusAsync(_currentUserContext.RequireUserId(), null, clientAttachmentId, AttachmentStatus.Failed, null, reason).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to mark attachment as Failed ClientAttachmentId={ClientAttachmentId}", clientAttachmentId);
        }
    }
    /// <summary>
    /// 列表项可见时回调（MessageView.ContainerPrepared 挂钩）：为图片附件发起缩略图预取。
    /// 仅对真实进入可视区域的条目触发，配合虚拟化避免整页图片突发下载。
    /// </summary>
    public void OnMessageContainerPrepared(Message? message)
    {
        if (message?.ImageThumbnails is not { Count: > 0 })
            return;
        foreach (var item in message.ImageThumbnails)
            ScheduleThumbnailPrefetch(item);
    }

    private void ScheduleThumbnailPrefetch(ImageThumbnailItem item)
    {
        var attachmentId = item.Attachment.AttachmentId;
        if (string.IsNullOrWhiteSpace(attachmentId))
            return;
        lock (_thumbnailsRequested)
        {
            if (!_thumbnailsRequested.Add(attachmentId))
                return;
        }
        _ = PrefetchThumbnailAsync(item, attachmentId);
    }

    /// <summary>
    /// 缩略图预取管道：原图下载（single-flight 缓存）→ 本地生成缩略图 → UI 线程回填。
    /// 任一环节失败均保持缩略图缺省（气泡回退为附件链接），不影响消息渲染。
    /// </summary>
    private async Task PrefetchThumbnailAsync(ImageThumbnailItem item, string attachmentId)
    {
        try
        {
            await _thumbnailGate.WaitAsync().ConfigureAwait(true);
            try
            {
                var attachment = item.Attachment;
                var fileName = !string.IsNullOrWhiteSpace(attachment.FileName)
                    ? attachment.FileName
                    : $"{attachment.AttachmentId}.bin";
                var fullPath = await _downloadService.GetOrDownloadAsync(
                        attachment.AttachmentId, fileName, attachment.DownloadApiHint)
                    .ConfigureAwait(true);
                if (fullPath is null)
                    return;

                var owner = _currentUserContext.HasUserId ? _currentUserContext.UserId!.Value : 0;
                var thumbnailPath = await _thumbnailService.EnsureThumbnailAsync(
                        owner, attachment.AttachmentId, fileName, attachment.ContentType, fullPath)
                    .ConfigureAwait(true);
                if (thumbnailPath is null)
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    item.FullPath = fullPath;
                    item.ThumbnailPath = thumbnailPath;
                });
            }
            finally
            {
                _thumbnailGate.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "生成图片缩略图失败 AttachmentId={AttachmentId}", attachmentId);
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

        // 下载协调服务：缓存命中直接返回；未命中时"网络下载 → 校验 → 缓存落盘"，
        // 同附件并发调用共享同一次网络请求。
        string? cachedPath = null;
        try
        {
            cachedPath = await _downloadService.GetOrDownloadAsync(
                    attachment.AttachmentId, fileName, attachment.DownloadApiHint, ct)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "获取附件缓存路径失败");
        }

        Stream? content = null;
        try
        {
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

    /// <summary>不允许上传的高风险文件扩展名黑名单（可执行文件、脚本、系统文件）。</summary>
    private static readonly HashSet<string> BlockedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".scr", ".msi", ".ps1", ".vbs", ".js", ".jar",
        ".app", ".dll", ".sys", ".drv", ".reg", ".inf", ".lnk", ".sh", ".deb", ".rpm"
    };

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
        ScheduleDraftSave();
    }

    /// <summary>统一刷新录音相关命令的可用状态（录音开始/结束/取消后调用）。</summary>
    private void RefreshRecordingCommandStates()
    {
        StartRecordingCommand.RaiseCanExecuteChanged();
        SendRecordingCommand.RaiseCanExecuteChanged();
        CancelRecordingCommand.RaiseCanExecuteChanged();
    }

    private void AddPendingAttachment(PendingAttachment attachment)
    {
        _pendingAttachments.Add(attachment);
        OnPropertyChanged(nameof(PendingAttachments));
        OnPropertyChanged(nameof(HasPendingAttachment));
        OnPropertyChanged(nameof(PendingAttachmentSummary));
        ClearPendingAttachmentCommand.RaiseCanExecuteChanged();
        ScheduleDraftSave();
    }

    private void ClearReplyDraft()
    {
        _replyToMessageId = null;
        _replyToSenderUserId = null;
        ReplyDraftPreview = null;
        ScheduleDraftSave();
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
        ScheduleDraftSave();
    }

    public void Clear()
    {
        var peerId = CurrPeerId;
        _ = StopTypingAsync();
        if (peerId > 0)
            _ = UnwatchPeerPresenceAsync(peerId);

        CancelConversationOperations();
        IsPreviewOpen = false;
        CurrPeerId = 0;
        CurrConversationId = null;
        PeerTitle = string.Empty;
        PeerIsOnline = false;
        IsPeerTyping = false;
        CancelPeerTypingClear();
        ClearMessages();
        // 回到初始态：无会话打开，空态判定恢复默认（消息区此时整体隐藏）。
        _historyRendered = true;
        OnPropertyChanged(nameof(IsMessageListEmpty));
        NewMessage = string.Empty;
        ClearPendingAttachment();
        ClearReplyDraft();
        ClearEditDraft();
        AttachFileCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        CancelPeerTypingClear();
        var peerId = CurrPeerId;
        if (peerId > 0)
            _ = UnwatchPeerPresenceAsync(peerId);

        try
        {
            _conversationCts.Cancel();
            _conversationCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略
        }

        _chatSession.MessageRecalled -= OnMessageRecalled;
        _chatSession.MessageEdited -= OnMessageEdited;
        _chatSession.TypingUpdated -= OnTypingUpdated;
        _chatSession.PresenceChanged -= OnPresenceChanged;

        foreach (var sub in _eventSubscriptions)
            sub.Dispose();
        _eventSubscriptions.Clear();
        // 若正在录音则放弃并停止采集（幂等）。
        _voiceRecorder.Cancel();
        _thumbnailGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnTypingUpdated(object? sender, TypingUpdateDto update)
    {
        if (CurrPeerId <= 0 || update.SenderUserId != CurrPeerId)
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
        if (CurrPeerId <= 0 || update.UserId != CurrPeerId)
            return;

        Dispatcher.UIThread.Post(() => PeerIsOnline = update.IsOnline);
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
            if (CurrPeerId == friendId)
                PeerIsOnline = item.IsOnline;
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
        // 群聊无对端用户（CurrPeerId=0）：输入状态按会话广播（conversationId 参数）。
        if (!IsGroupConversation && CurrPeerId <= 0)
            return;
        if (!_chatSession.IsConnected
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
                    CurrPeerId,
                    true,
                    CurrConversationId
                        ?? ConversationId.CreateDirect(_chatSession.CurrentUserId, CurrPeerId))
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
        if (!_typingActive || (!IsGroupConversation && CurrPeerId <= 0))
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
                        CurrPeerId,
                        false,
                        CurrConversationId
                            ?? ConversationId.CreateDirect(_chatSession.CurrentUserId, CurrPeerId))
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

/// <summary>View 文件选择器返回的本地文件描述。选择阶段不读取文件内容，</summary>
/// 大小取自文件系统元数据；OpenReadAsync 延迟到上传阶段才真正打开流。
public sealed class PickedAttachmentFile
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required long ContentLength { get; init; }
    public required Func<CancellationToken, Task<Stream>> OpenReadAsync { get; init; }
}

/// <summary>待发送附件草稿项（阶段 3-5 多附件草稿；语音附件携带 VOICE-MSG-2 元数据）。</summary>
public sealed class PendingAttachment
{
    public string AttachmentId { get; init; } = string.Empty;
    public string? FileName { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public long SizeBytes { get; init; }

    /// <summary>是否为语音附件（VOICE-MSG-2）。为 true 时语音字段必须非空且为正。</summary>
    public bool IsVoice { get; init; }

    /// <summary>音频编解码器（如 pcm、opus）。仅语音附件有值。</summary>
    public string? VoiceCodec { get; init; }

    /// <summary>音频容器格式（如 wav、ogg）。仅语音附件有值。</summary>
    public string? VoiceContainer { get; init; }

    /// <summary>语音时长（毫秒）。仅语音附件有值。</summary>
    public long? VoiceDurationMs { get; init; }

    /// <summary>采样率（Hz）。仅语音附件有值。</summary>
    public int? VoiceSampleRateHz { get; init; }

    /// <summary>声道数。仅语音附件有值。</summary>
    public short? VoiceChannels { get; init; }

    /// <summary>语音波形峰值包络（0–255，可空）。仅语音附件有值；缺省/空 = 无波形，渲染降级。</summary>
    public byte[]? VoiceWaveformPeaks { get; init; }
}

