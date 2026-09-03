using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Services;
using Chat_App.Presentation.ViewModels.Chat;
using Chat_App.Services;
using ChatApp.Contracts.Http.Attachments;
using Core.Contracts.Attachments;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Services;
using Xunit;
using AttachmentRefDto = ChatApp.Shared.Protocol.Tcp.TcpAttachmentRef;
using ConversationSyncWatermarkDto = ChatApp.Shared.Protocol.Tcp.ConversationSyncWatermark;
using MessageHistoryPageDto = ChatApp.Shared.Protocol.Tcp.MessageHistoryResponse;
using RelationshipSyncWatermarkDto = ChatApp.Shared.Protocol.Tcp.RelationshipSyncWatermark;
using SyncBootstrapResponseDto = ChatApp.Shared.Protocol.Tcp.SyncBootstrapResponse;

// 测试桩声明全部接口事件但从不触发属预期（CS0067）：仅实现接口成员以满足编译。
#pragma warning disable CS0067


namespace UnitTests;

/// <summary>
/// VOICE-MSG-2 降级路径（ViewModel 级）自动化验证：路径 2（播放下载失败防护）
/// 与路径 3（上传失败恢复）。路径 1（录音超时自动收尾）由 <see cref="VoiceRecorderTests"/>
/// 覆盖，本文件通过真实 <see cref="MessageViewModel"/> + 最小桩驱动其私有的
/// <see cref="MessageViewModel"/> 播放/发送链路，验证降级行为的终态。
/// 只驱动失败路径，故未用到的依赖（消息存储/数据库/会话上下文等）传 null。
/// </summary>
public sealed class VoiceDegradationViewModelTests
{
    [Fact]
    public async Task PlayVoice_DownloadThrows_StopsPlayerShowsTerminalError_NoPseudoPlaying()
    {
        var notifications = new FakeNotifications();
        var player = new FakeAudioPlayer();
        var download = new FakeDownload { Exception = new HttpRequestException("网络不可达") };
        using var vm = CreateVm(notifications, player, new FakeAttachmentClient(), download, new FakeVoiceRecorder());

        await InvokePlayVoice(vm, NewVoiceAttachment("voice-1"));

        // 播放器复位：不进入"播放中"伪状态，Play 从未被调用。
        Assert.Equal(1, player.StopCalls);
        Assert.Equal(0, player.PlayCalls);
        Assert.False(player.IsPlaying);
        // 明确终态提示。
        var error = Assert.Single(notifications.Errors);
        Assert.Contains("语音加载失败", error);
    }

    [Fact]
    public async Task PlayVoice_DownloadReturnsNull_StopsPlayerShowsTerminalError()
    {
        var notifications = new FakeNotifications();
        var player = new FakeAudioPlayer();
        var download = new FakeDownload { Result = null };
        using var vm = CreateVm(notifications, player, new FakeAttachmentClient(), download, new FakeVoiceRecorder());

        await InvokePlayVoice(vm, NewVoiceAttachment("voice-2"));

        Assert.Equal(1, player.StopCalls);
        Assert.Equal(0, player.PlayCalls);
        Assert.Contains("语音加载失败", Assert.Single(notifications.Errors));
    }

    [Fact]
    public async Task SendVoiceRecording_UploadThrows_AbandonsAttachmentShowsTerminalError_ResetsUploading()
    {
        var notifications = new FakeNotifications();
        var attachments = new FakeAttachmentClient { UploadException = new HttpRequestException("断网") };
        using var vm = CreateVm(notifications, new FakeAudioPlayer(), attachments, new FakeDownload(), new FakeVoiceRecorder());
        using var recording = NewRecording();

        await InvokeSendVoiceRecording(vm, recording);

        // 明确终态提示。
        var error = Assert.Single(notifications.Errors);
        Assert.StartsWith("语音上传失败", error);
        // 服务端不留孤儿附件：已 Abandon。
        var abandoned = Assert.Single(attachments.AbandonedIds);
        Assert.False(string.IsNullOrWhiteSpace(abandoned), "应以生成的 clientAttachmentId 调用 AbandonAsync");
        // 上传进度复位、无悬挂"上传中"状态。
        Assert.False(vm.IsUploading);
        Assert.Equal(0, vm.UploadProgress);
        // 录音产物由 using 释放。
        Assert.False(recording.WavStream.CanRead, "录音流应在方法返回后被释放");
    }

    [Fact]
    public async Task SendVoiceRecording_UploadCancelled_AbandonsAttachmentShowsCancelMessage()
    {
        var notifications = new FakeNotifications();
        var attachments = new FakeAttachmentClient { UploadException = new OperationCanceledException() };
        using var vm = CreateVm(notifications, new FakeAudioPlayer(), attachments, new FakeDownload(), new FakeVoiceRecorder());
        using var recording = NewRecording();

        await InvokeSendVoiceRecording(vm, recording);

        var error = Assert.Single(notifications.Errors);
        Assert.Contains("已取消", error);
        Assert.Single(attachments.AbandonedIds);
        Assert.False(vm.IsUploading);
    }

    // ── 音频输出路由（VOICE-MSG-3）：持久化偏好在播放前应用 ──────────

    [Fact]
    public async Task PlayVoice_Applies_Persisted_OutputDevice_Before_Play()
    {
        var notifications = new FakeNotifications();
        var player = new FakeAudioPlayer();
        var settings = new FakeSettingsService { Settings = { AudioOutputDeviceId = "3" } };
        var user = new StubUserContext { UserId = 7 };
        var download = new FakeDownload { Result = @"C:\cache\voice.wav" };
        using var vm = CreateVm(notifications, player, new FakeAttachmentClient(), download,
            new FakeVoiceRecorder(), settings, user);

        await InvokePlayVoice(vm, NewVoiceAttachment("voice-dev"));

        // 持久化设备在 Play 前生效，且设置每进程只读一次。
        Assert.Equal("3", player.SelectedOutputDeviceId);
        Assert.Equal(1, player.PlayCalls);
        Assert.Equal(1, settings.GetCalls);
    }

    [Fact]
    public async Task PlayVoice_Second_Play_Does_Not_Reread_Settings()
    {
        var notifications = new FakeNotifications();
        var player = new FakeAudioPlayer();
        var settings = new FakeSettingsService { Settings = { AudioOutputDeviceId = "1" } };
        var user = new StubUserContext { UserId = 7 };
        var download = new FakeDownload { Result = @"C:\cache\voice.wav" };
        using var vm = CreateVm(notifications, player, new FakeAttachmentClient(), download,
            new FakeVoiceRecorder(), settings, user);

        await InvokePlayVoice(vm, NewVoiceAttachment("voice-a"));
        player.Stop();
        await InvokePlayVoice(vm, NewVoiceAttachment("voice-b"));

        Assert.Equal(2, player.PlayCalls);
        Assert.Equal(1, settings.GetCalls);
        Assert.Equal("1", player.SelectedOutputDeviceId);
    }

    [Fact]
    public async Task PlayVoice_Without_Settings_Service_Plays_With_System_Default()
    {
        var notifications = new FakeNotifications();
        var player = new FakeAudioPlayer();
        var download = new FakeDownload { Result = @"C:\cache\voice.wav" };
        using var vm = CreateVm(notifications, player, new FakeAttachmentClient(), download, new FakeVoiceRecorder());

        await InvokePlayVoice(vm, NewVoiceAttachment("voice-nodefault"));

        // 无设置服务（降级构造）：不抛异常，不选择任何设备（null = 系统默认），照常播放。
        Assert.Null(player.SelectedOutputDeviceId);
        Assert.Equal(1, player.PlayCalls);
    }

    // ── 构造与驱动 ─────────────────────────────────────────────

    private static MessageViewModel CreateVm(
        FakeNotifications notifications,
        FakeAudioPlayer player,
        FakeAttachmentClient attachments,
        FakeDownload download,
        FakeVoiceRecorder recorder,
        ISettingsService? settingsService = null,
        ICurrentUserContext? currentUserContext = null)
    {
        return new MessageViewModel(
            notifications,
            new SessionStub(),
            attachments,
            null!,          // IMessageStore：仅成功路径使用，降级测试不触达
            new InMemoryEventBus(),
            null!,          // IDatabaseService
            currentUserContext!,
            null!,          // IAttachmentStorageService
            download,
            null!,          // IAttachmentThumbnailService
            recorder,
            player,
            settingsService);
    }

    private static async Task InvokePlayVoice(MessageViewModel vm, AttachmentRefDto? attachment)
    {
        var method = typeof(MessageViewModel).GetMethod(
            "PlayVoiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MessageViewModel), "PlayVoiceAsync");
        var task = (Task)method.Invoke(vm, new object?[] { attachment, CancellationToken.None })!;
        await task;
    }

    private static async Task InvokeSendVoiceRecording(MessageViewModel vm, VoiceRecording? recording)
    {
        var method = typeof(MessageViewModel).GetMethod(
            "SendVoiceRecordingAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MessageViewModel), "SendVoiceRecordingAsync");
        var task = (Task)method.Invoke(vm, new object?[] { recording, CancellationToken.None })!;
        await task;
    }

    private static AttachmentRefDto NewVoiceAttachment(string id) => new()
    {
        AttachmentId = id,
        FileName = $"{id}.wav",
        ContentType = "audio/wav",
        SizeBytes = 1024,
        Status = 1,
        DownloadApiHint = id,
        IsVoice = true,
        VoiceCodec = "pcm",
        VoiceContainer = "wav",
        VoiceDurationMs = 1000,
        VoiceSampleRateHz = 16_000,
        VoiceChannels = 1
    };

    private static VoiceRecording NewRecording() => new(
        new MemoryStream(new byte[1024]),
        new VoiceMetadata("pcm", "wav", 1000, 16_000, 1, 1024));

    // ── 最小测试桩 ────────────────────────────────────────────

    internal sealed class FakeNotifications : INotificationService
    {
        public List<string> Errors { get; } = new();
        public void ShowError(string message, string title = "错误") => Errors.Add(message);
        public void ShowWarning(string message, string title = "警告") { }
        public void ShowInfo(string message, string title = "提示") { }
        public void ShowSuccess(string message, string title = "成功") { }
    }

    internal sealed class FakeAudioPlayer : IAudioPlayer
    {
        public bool IsPlaying { get; private set; }
        public string? CurrentKey { get; private set; }
        public int PlayCalls { get; private set; }
        public int StopCalls { get; private set; }
        public string? SelectedOutputDeviceId { get; private set; }

        public event Action<AudioPlaybackProgress>? Progress;
        public event Action? Stopped;

        public void Play(string key, string wavPath) { PlayCalls++; IsPlaying = true; CurrentKey = key; }
        public void Pause() { IsPlaying = false; }
        public void Resume() { IsPlaying = true; }
        public void Stop() { StopCalls++; IsPlaying = false; CurrentKey = null; }
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public void SelectOutputDevice(string? deviceId) => SelectedOutputDeviceId = deviceId;
        public void Dispose() { }
    }

    internal sealed class FakeAttachmentClient : IAttachmentClientService
    {
        public Exception? UploadException { get; set; }
        public List<string> AbandonedIds { get; } = new();

        public Task<AttachmentUploadResult> UploadAndConfirmAsync(
            Stream content, string contentType, long contentLength, string? originalName = null,
            string? clientAttachmentId = null, IProgress<AttachmentUploadProgress>? progress = null,
            int maxAttempts = 3, string? sha256 = null, CancellationToken ct = default)
            => throw UploadException ?? new HttpRequestException("上传失败");

        public Task AbandonAsync(string attachmentId, CancellationToken ct = default)
        {
            AbandonedIds.Add(attachmentId);
            return Task.CompletedTask;
        }

        public Task<AttachmentPresignResponse> PresignAsync(
            AttachmentPresignRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task UploadAsync(
            AttachmentPresignResponse ticket, Stream content, string contentType, long contentLength,
            IProgress<AttachmentUploadProgress>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ConfirmAttachmentResponse> ConfirmAsync(
            ConfirmAttachmentRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<AttachmentDownloadResult> DownloadAsync(
            string attachmentIdOrHint, long? rangeFrom = null, long? rangeTo = null, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    internal sealed class FakeDownload : IAttachmentDownloadService
    {
        public Exception? Exception { get; set; }
        public string? Result { get; set; }

        public Task<string?> GetOrDownloadAsync(
            string attachmentId, string fileName, string? downloadApiHint, CancellationToken ct = default)
        {
            if (Exception is not null)
                throw Exception;
            return Task.FromResult(Result);
        }
    }

    /// <summary>内存版设置服务桩：记录 GetAsync 调用次数，验证偏好每进程只应用一次。</summary>
    internal sealed class FakeSettingsService : ISettingsService
    {
        public Core.Settings.ClientSettings Settings { get; set; } = new();
        public int GetCalls { get; private set; }

        public Task<Core.Settings.ClientSettings> GetAsync(long ownerUserId, CancellationToken ct = default)
        {
            GetCalls++;
            return Task.FromResult(Settings);
        }

        public Task SetAsync(long ownerUserId, Core.Settings.ClientSettings settings, CancellationToken ct = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(long ownerUserId, Action<Core.Settings.ClientSettings> mutate, CancellationToken ct = default)
        {
            mutate(Settings);
            return Task.CompletedTask;
        }
    }

    /// <summary>可切换登录态的用户上下文桩。</summary>
    internal sealed class StubUserContext : ICurrentUserContext
    {
        public long Generation { get; set; } = 1;
        public long? UserId { get; set; }
        public string? UserName => UserId is { } id ? $"user-{id}" : null;
        public bool IsAuthenticated => UserId is > 0;
        public bool HasUserId => UserId is > 0;
        public UserSessionSnapshot Snapshot => new(UserId ?? 0, Generation, UserName, null, null);
        public long RequireUserId() => UserId ?? throw new InvalidOperationException("未登录");
        public bool TryGetUserId(out long id)
        {
            id = UserId ?? 0;
            return UserId is > 0;
        }
    }

    internal sealed class FakeVoiceRecorder : IVoiceRecorder
    {
        public bool IsRecording => false;
        public VoiceRecorderOptions Options { get; } = new(16_000, 1);
        public event Action<VoiceRecordingProgress>? Progress;
        public event Action<VoiceRecording>? AutoCompleted;
        public void Start() { }
        public VoiceRecording? Stop() => null;
        public void Cancel() { }
    }

    /// <summary>最小 IChatSessionClient 桩：仅构造期订阅的 4 个事件字段可用，方法体不触达。</summary>
    internal sealed class SessionStub : IChatSessionClient
    {
        public bool IsConnected { get; set; }
        public bool IsAuthenticated { get; set; }
        public long CurrentUserId { get; set; }
        public long ConnectionGeneration { get; set; }
        public Guid ConnectionId { get; set; } = Guid.NewGuid();
        public SessionStamp CurrentSession => new(CurrentUserId, ConnectionGeneration, ConnectionId);

        public event EventHandler? Connected;
        public event EventHandler<long>? Authenticated;
        public event EventHandler<string>? AuthenticationFailed;
        public event EventHandler<ProtocolErrorDto>? ProtocolError;
        public event EventHandler<ChatMessageDto>? ChatMessageReceived;
        public event EventHandler<MessageAcknowledgementDto>? MessageAcknowledged;
        public event EventHandler<ConversationChangedDto>? ConversationChanged;
        public event EventHandler<MessageRecalledUpdateDto>? MessageRecalled;
        public event EventHandler<MessageEditedUpdateDto>? MessageEdited;
        public event EventHandler<TypingUpdateDto>? TypingUpdated;
        public event EventHandler<PresenceChangedDto>? PresenceChanged;
        public event EventHandler<string>? ConnectionClosed;
        public event EventHandler<MessageReceiptDto>? MessageReceiptReceived;
        public event EventHandler<MessageReceiptUpdatedDto>? MessageReceiptUpdated;
        public event EventHandler<MessageHistoryPageDto>? MessageHistoryPageReceived;
        public event EventHandler<ConversationMarkReadResponseDto>? ConversationMarkReadResponse;
        public event EventHandler<UnreadCountChangedDto>? UnreadCountChanged;
        public event EventHandler<CallSignalDto>? CallSignalReceived;
        public event EventHandler<MemberJoinedUpdateDto>? GroupMemberJoined;
        public event EventHandler<MemberLeftUpdateDto>? GroupMemberLeft;
        public event EventHandler<MemberRemovedUpdateDto>? GroupMemberRemoved;
        public event EventHandler<RoleChangedUpdateDto>? GroupRoleChanged;
        public event EventHandler<MembersAddedUpdateDto>? GroupMembersAdded;
        public event EventHandler<ConversationDissolvedUpdateDto>? GroupConversationDissolved;

        public ResumeAttemptResult? LastResumeResult => null;
        public string? LastIssuedResumeToken => null;

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken ct = default, string? resumeToken = null) => Task.CompletedTask;
        public Task AuthenticateAsync(string accessToken, long userId, string? sessionId, ulong? deviceIdHash, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task DisconnectAsync(string? reason = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendChatMessageAsync(long targetUserId, string? content, IReadOnlyList<string>? attachmentIds = null, string? replyToMessageId = null, long? replyToSenderUserId = null, string? replyToPreview = null, string? forwardedFromMessageId = null, long? forwardedFromSenderUserId = null, string? forwardedFromPreview = null, string? clientMessageId = null, string? conversationId = null, IReadOnlyList<long>? mentionedUserIds = null, IReadOnlyList<global::ChatApp.Shared.Protocol.Tcp.TcpAttachmentRef>? attachments = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ConversationListResponseDto> QueryConversationListAsync(int limit = 50, bool? beforeIsPinned = null, long? beforePinnedAtMs = null, long? beforeLastMessageAtMs = null, string? beforeConversationId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ConversationSetPrefsResponseDto> SetConversationPrefsAsync(string conversationId, bool? pinned = null, bool? muted = null, long? mutedUntilMs = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageRecallAcknowledgementDto> RecallMessageAsync(string messageId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageEditAcknowledgementDto> EditMessageAsync(string messageId, string content, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task SendTypingNotifyAsync(long targetUserId, bool isTyping, string? conversationId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<PresenceSnapshotResponseDto> QueryPresenceAsync(IReadOnlyList<long> userIds, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UnwatchPresenceAsync(IReadOnlyList<long> userIds, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SyncBootstrapResponseDto> QuerySyncBootstrapAsync(int listLimit = 50, int historyLimitPerConversation = 20, int maxConversationsWithHistory = 10, IReadOnlyList<ConversationSyncWatermarkDto>? watermarks = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<SyncBootstrapResponseDto> QuerySyncBootstrapWithRelationshipsAsync(int listLimit = 50, int historyLimitPerConversation = 20, int maxConversationsWithHistory = 10, IReadOnlyList<ConversationSyncWatermarkDto>? watermarks = null, IReadOnlyList<RelationshipSyncWatermarkDto>? relationshipWatermarks = null, int? relationshipListLimit = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageHistoryPageDto> QueryMessageHistoryAsync(string conversationId, int limit = 50, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageHistoryPageDto> QueryMessageHistoryAfterAsync(string conversationId, int limit = 50, string? afterMessageId = null, long? afterReceivedAtMs = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<MessageReceiptAckDto> SendMessageReceiptAsync(string conversationId, string? lastReadMessageId, long? lastReadAtMs, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ConversationMarkReadResponseDto> MarkConversationReadAsync(string conversationId, string? lastReadMessageId = null, long? lastReadAtMs = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<CreateGroupResponseDto> CreateGroupAsync(string title, IReadOnlyList<long>? memberUserIds = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<AddGroupMembersResponseDto> AddGroupMembersAsync(string conversationId, IReadOnlyList<long> memberUserIds, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<RemoveGroupMemberResponseDto> RemoveGroupMemberAsync(string conversationId, long targetUserId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<LeaveGroupResponseDto> LeaveGroupAsync(string conversationId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<DissolveGroupResponseDto> DissolveGroupAsync(string conversationId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ChangeMemberRoleResponseDto> ChangeMemberRoleAsync(string conversationId, long targetUserId, ConversationMemberRole newRole, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ListGroupMembersResponseDto> ListGroupMembersAsync(string conversationId, int? pageSize = null, string? cursor = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public void Dispose() { }
    }
}
