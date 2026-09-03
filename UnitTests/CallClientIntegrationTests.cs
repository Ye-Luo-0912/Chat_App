using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Presentation.ViewModels.Chat;
using Chat_App.Infrastructure.Services;
using Chat_App.Services;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Services;
using Xunit;
using MessageHistoryPageDto = ChatApp.Shared.Protocol.Tcp.MessageHistoryResponse;
using SyncBootstrapResponseDto = ChatApp.Shared.Protocol.Tcp.SyncBootstrapResponse;
using ConversationSyncWatermarkDto = ChatApp.Shared.Protocol.Tcp.ConversationSyncWatermark;
using RelationshipSyncWatermarkDto = ChatApp.Shared.Protocol.Tcp.RelationshipSyncWatermark;

// 测试桩声明全部接口事件但从不触发属预期（CS0067）：仅实现接口成员以满足编译。
#pragma warning disable CS0067


namespace UnitTests;

/// <summary>
/// CALL-E2E-2 客户端通话集成测试：覆盖 <see cref="CallApiService"/> 授权请求映射
/// 与 <see cref="ChatViewModel"/> 通话命令/状态。
/// <see cref="CallSessionManager"/> 的测试见 <see cref="CallSessionStateMachineTests"/>。
/// </summary>
public sealed class CallClientIntegrationTests
{
    private const long CallerId = 7001;
    private const long CalleeId = 7002;

    // ════════════════ CallApiService ════════════════

    [Fact]
    public async Task CallApiService_RequestGrant_Success_ReturnsGrant()
    {
        using var client = CreateClient(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/calls/grants", request.RequestUri?.AbsolutePath);

            return Json(HttpStatusCode.OK, """
                {
                  "data": {
                    "callId": "call-abc",
                    "callerUserId": 7001,
                    "calleeUserId": 7002,
                    "expiresAtMs": 1900000000000,
                    "nonce": "nonce-xyz",
                    "signature": "sig-abc123"
                  }
                }
                """);
        });

        var service = new CallApiService(client);
        var grant = await service.RequestGrantAsync(CalleeId);

        Assert.NotNull(grant);
        Assert.Equal("call-abc", grant!.CallId);
        Assert.Equal(CallerId, grant.CallerUserId);
        Assert.Equal(CalleeId, grant.CalleeUserId);
        Assert.Equal(1900000000000L, grant.ExpiresAtMs);
        Assert.Equal("nonce-xyz", grant.Nonce);
        Assert.Equal("sig-abc123", grant.Signature);
    }

    [Fact]
    public async Task CallApiService_ErrorEnvelope_ReturnsNull()
    {
        using var client = CreateClient(_ => Task.FromResult(Json(HttpStatusCode.Forbidden, """
            { "error": "call_grant_invalid" }
            """)));

        var service = new CallApiService(client);
        var grant = await service.RequestGrantAsync(CalleeId);

        Assert.Null(grant);
    }

    [Fact]
    public async Task CallApiService_HttpFailure_ReturnsNull()
    {
        using var client = CreateClient(_ => throw new HttpRequestException("网络不可达"));

        var service = new CallApiService(client);
        var grant = await service.RequestGrantAsync(CalleeId);

        Assert.Null(grant);
    }

    [Fact]
    public async Task CallApiService_InvalidCallee_ReturnsNull()
    {
        using var client = CreateClient(_ => throw new HttpRequestException("不应到达"));
        var service = new CallApiService(client);

        Assert.Null(await service.RequestGrantAsync(0));
        Assert.Null(await service.RequestGrantAsync(-1));
    }

    [Fact]
    public async Task CallApiService_EmptyData_ReturnsNull()
    {
        using var client = CreateClient(_ => Task.FromResult(Json(HttpStatusCode.OK, """{ "data": {} }""")));
        var service = new CallApiService(client);

        var grant = await service.RequestGrantAsync(CalleeId);
        Assert.Null(grant);
    }

    // ════════════════ ChatViewModel 通话命令 ════════════════

    [Fact]
    public async Task StartCallCommand_OnDirectConversation_FetchesGrantAndStartsCall()
    {
        var callApi = new FakeCallApiService { Result = NewGrant() };
        var callManager = new FakeCallSessionManager();
        var vm = CreateVm(callApi, callManager);
        var conv = NewDirectConversation(CalleeId, "好友A");

        // 先设置 SelectedConversation（模拟用户选中会话）
        vm.SelectedConversation = conv;

        // 验证命令可执行
        Assert.True(StartCallCanExecute(vm, conv));

        // 执行
        await InvokeStartCall(vm, conv);

        // 验证：请求了 grant + 用 peerId 发起了 StartCall
        Assert.Equal(CalleeId, callApi.LastCalleeId);
        Assert.NotNull(callManager.LastGrant);
        Assert.Equal(CalleeId, callManager.LastStartCalleeUserId);
        Assert.Equal("call-abc", callManager.LastGrant!.CallId);
    }

    [Fact]
    public async Task StartCallCommand_NullGrant_ShowsError_DoesNotStartCall()
    {
        var callApi = new FakeCallApiService { Result = null };
        var callManager = new FakeCallSessionManager();
        var vm = CreateVm(callApi, callManager);
        var conv = NewDirectConversation(CalleeId, "好友A");
        vm.SelectedConversation = conv;

        await InvokeStartCall(vm, conv);

        // grant 为空 → 不调用 StartCallAsync
        Assert.Null(callManager.LastStartCalleeUserId);
        // 应有错误通知
        Assert.Single(((FakeNotifications)vm.FieldNotifications()).Errors);
    }

    [Fact]
    public void StartCallCommand_CanExecute_RequiresDirectConversation()
    {
        var vm = CreateVm();
        var direct = NewDirectConversation(CalleeId, "好友A");
        var group = new Chat_App.Infrastructure.Models.LocalConversation
        {
            ConversationId = "group-1",
            Type = 2,
            GroupTitle = "群聊"
        };
        var nullConv = (Chat_App.Infrastructure.Models.LocalConversation?)null;

        // 直聊会话且已鉴权且支持信令
        Assert.True(StartCallCanExecute(vm, direct));
        // 群聊不可通话
        Assert.False(StartCallCanExecute(vm, group));
        // null 不可通话
        Assert.False(StartCallCanExecute(vm, nullConv!));
    }

    [Fact]
    public async Task AcceptCallCommand_WhenIncomingCall_AcceptsCall()
    {
        var callManager = new FakeCallSessionManager();
        var vm = CreateVm(new FakeCallApiService(), callManager);

        // 模拟来电
        var session = new CallSession("call-1", CallRole.Callee, CallerId, CallStateDto.Ringing);
        callManager.ActiveCallsList.Add(session);
        SimulateIncomingCall(vm, session);

        Assert.True(vm.IsIncomingCall);

        // 执行接听
        await InvokeAccept(vm);

        Assert.Equal("call-1", callManager.LastAcceptCallId);
    }

    [Fact]
    public async Task RejectCallCommand_WhenIncomingCall_RejectsCall()
    {
        var callManager = new FakeCallSessionManager();
        var vm = CreateVm(new FakeCallApiService(), callManager);

        var session = new CallSession("call-2", CallRole.Callee, CallerId, CallStateDto.Ringing);
        callManager.ActiveCallsList.Add(session);
        SimulateIncomingCall(vm, session);

        Assert.True(vm.IsIncomingCall);

        await InvokeReject(vm);

        Assert.Equal("call-2", callManager.LastRejectCallId);
    }

    [Fact]
    public async Task EndCallCommand_WhenActive_CallsEnd()
    {
        var callManager = new FakeCallSessionManager();
        var vm = CreateVm(new FakeCallApiService(), callManager);

        // 模拟已接通
        var session = new CallSession("call-3", CallRole.Caller, CalleeId, CallStateDto.Active);
        callManager.ActiveCallsList.Add(session);
        SimulateCallChanged(vm, session);

        Assert.True(vm.IsCallActive);
        Assert.Equal("通话中…", vm.CallStatusText);

        await InvokeEnd(vm);

        Assert.Equal("call-3", callManager.LastEndCallId);
    }

    [Fact]
    public void CallEnded_ClearsState()
    {
        var callManager = new FakeCallSessionManager();
        var vm = CreateVm(new FakeCallApiService(), callManager);

        // 模拟通话结束
        var session = new CallSession("call-4", CallRole.Caller, CalleeId, CallStateDto.Active);
        SimulateCallChanged(vm, session);
        Assert.True(vm.IsCallActive);

        // 模拟结束事件
        SimulateCallEnded(vm, session);
        Assert.False(vm.IsCallActive);
        Assert.False(vm.IsIncomingCall);
        Assert.Equal(string.Empty, vm.CallStatusText);
    }

    // ════════════════ 构造与驱动 ════════════════

    private static ChatViewModel CreateVm(
        ICallApiService? callApi = null,
        ICallSessionManager? callManager = null)
    {
        var notifications = new FakeNotifications();
        var sessionStub = new SessionStub
        {
            IsAuthenticated = true,
            CurrentUserId = CallerId
        };
        var context = new FakeUserContext { UserId = CallerId };

        // 构造真实的 MessageViewModel：其构造器会订阅录音/播放器与 ChatViewModel 事件，故不可为 null。
        var messageVm = new MessageViewModel(
            notifications,
            sessionStub,
            null!,   // IAttachmentClientService
            null!,   // IMessageStore
            new InMemoryEventBus(),
            null!,   // IDatabaseService
            null!,   // ICurrentUserContext
            null!,   // IAttachmentStorageService
            null!,   // IAttachmentDownloadService
            null!,   // IAttachmentThumbnailService
            new FakeVoiceRecorder(),
            new FakeAudioPlayer());

        var vm = new ChatViewModel(
            notifications,
            messageVm,
            null!,           // IChatFriendLoader（构造器不触达）
            new FakeConnectionCoordinator(),
            sessionStub,
            new FakeSyncEngine(),
            null!,           // IDatabaseService
            context,
            new FakeFriendStore(),
            new InMemoryEventBus(),
            callManager ?? new FakeCallSessionManager(),
            callApi ?? new FakeCallApiService { Result = NewGrant() });

        return vm;
    }

    private static bool StartCallCanExecute(ChatViewModel vm, Chat_App.Infrastructure.Models.LocalConversation? conv)
    {
        return vm.StartCallCommand.CanExecute(conv);
    }

    private static async Task InvokeStartCall(ChatViewModel vm, Chat_App.Infrastructure.Models.LocalConversation? conv)
    {
        var method = typeof(ChatViewModel).GetMethod("StartCallAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ChatViewModel), "StartCallAsync");
        var task = (Task)method.Invoke(vm, new object?[] { conv, CancellationToken.None })!;
        await task;
    }

    private static async Task InvokeAccept(ChatViewModel vm)
    {
        // AcceptCallCommand 是公开的，直接执行
        // 但需要模拟 CanExecute 先通过
        if (vm.AcceptCallCommand.CanExecute(null))
            vm.AcceptCallCommand.Execute(null);
        await Task.CompletedTask;
    }

    private static async Task InvokeReject(ChatViewModel vm)
    {
        if (vm.RejectCallCommand.CanExecute(null))
            vm.RejectCallCommand.Execute(null);
        await Task.CompletedTask;
    }

    private static async Task InvokeEnd(ChatViewModel vm)
    {
        if (vm.EndCallCommand.CanExecute(null))
            vm.EndCallCommand.Execute(null);
        await Task.CompletedTask;
    }

    private static void SimulateIncomingCall(ChatViewModel vm, CallSession session)
    {
        // 直接调用私有方法（跳过 Dispatcher 调度）
        var method = typeof(ChatViewModel).GetMethod("TrackCall",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ChatViewModel), "TrackCall");
        method.Invoke(vm, new object[] { session });
    }

    private static void SimulateCallChanged(ChatViewModel vm, CallSession session)
    {
        // CallStateChanged 走 TrackCall
        SimulateIncomingCall(vm, session);
    }

    private static void SimulateCallEnded(ChatViewModel vm, CallSession session)
    {
        var method = typeof(ChatViewModel).GetMethod("OnCallEnded",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(ChatViewModel), "OnCallEnded");
        method.Invoke(vm, new object?[] { null, session });
    }

    private static Chat_App.Infrastructure.Models.LocalConversation NewDirectConversation(long peerId, string name)
    {
        return new Chat_App.Infrastructure.Models.LocalConversation
        {
            ConversationId = $"direct-{peerId}",
            Type = 1,
            PeerUserId = peerId,
            PeerDisplayName = name
        };
    }

    private static CallGrantDto NewGrant() => new()
    {
        CallId = "call-abc",
        CallerUserId = CallerId,
        CalleeUserId = CalleeId,
        ExpiresAtMs = 1_900_000_000_000L,
        Nonce = "nonce-xyz",
        Signature = "sig-abc123"
    };

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
        new(new StubHandler(handler))
        {
            BaseAddress = new Uri("https://chat.test")
        };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }

    // ── 测试桩 ──

    private sealed class FakeCallApiService : ICallApiService
    {
        public CallGrantDto? Result { get; set; }
        public long LastCalleeId { get; private set; }

        public Task<CallGrantDto?> RequestGrantAsync(long calleeUserId, CancellationToken ct = default)
        {
            LastCalleeId = calleeUserId;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeCallSessionManager : ICallSessionManager
    {
        public List<CallSession> ActiveCallsList { get; } = new();
        public IReadOnlyCollection<CallSession> ActiveCalls => ActiveCallsList;

        public long? LastStartCalleeUserId { get; private set; }
        public CallGrantDto? LastGrant { get; private set; }
        public string? LastAcceptCallId { get; private set; }
        public string? LastRejectCallId { get; private set; }
        public string? LastEndCallId { get; private set; }

        public event EventHandler<CallSession>? IncomingCall;
        public event EventHandler<CallSession>? CallStateChanged;
        public event EventHandler<CallSession>? CallEnded;

        public void RaiseIncomingCall(CallSession session) => IncomingCall?.Invoke(this, session);
        public void RaiseCallStateChanged(CallSession session) => CallStateChanged?.Invoke(this, session);
        public void RaiseCallEnded(CallSession session) => CallEnded?.Invoke(this, session);

        public Task<CallSession> StartCallAsync(long calleeUserId, string? sdpOffer = null, CallGrantDto? grant = null, CancellationToken ct = default)
        {
            LastStartCalleeUserId = calleeUserId;
            LastGrant = grant;
            var session = new CallSession(grant?.CallId ?? "call-auto", CallRole.Caller, calleeUserId, CallStateDto.Ringing);
            ActiveCallsList.Add(session);
            // 模拟正常上行完成后的状态通知
            return Task.FromResult(session);
        }

        public Task AcceptAsync(string callId, string? sdpAnswer = null, CancellationToken ct = default)
        {
            LastAcceptCallId = callId;
            return Task.CompletedTask;
        }

        public Task RejectAsync(string callId, CancellationToken ct = default)
        {
            LastRejectCallId = callId;
            return Task.CompletedTask;
        }

        public Task EndAsync(string callId, CancellationToken ct = default)
        {
            LastEndCallId = callId;
            return Task.CompletedTask;
        }

        public Task CancelAsync(string callId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ReconnectAsync(string callId, string? sdp = null, CancellationToken ct = default) => Task.CompletedTask;

        public CallSession? GetCall(string callId) => ActiveCallsList.Find(c => c.CallId == callId);

        public void Dispose() { }
    }

    private sealed class FakeUserContext : ICurrentUserContext
    {
        public long? UserId { get; set; }
        public Core.Models.UserSessionSnapshot Snapshot => new(UserId ?? 0, 0, null, null, null);
        public long Generation { get; set; }
        public string? UserName => null;
        public bool IsAuthenticated => UserId is > 0;
        public bool HasUserId => UserId is > 0;
        public long RequireUserId() => UserId is > 0 ? UserId!.Value : throw new System.InvalidOperationException("未登录");
        public bool TryGetUserId(out long userId)
        {
            userId = UserId ?? 0;
            return UserId is > 0;
        }
    }

    private sealed class FakeConnectionCoordinator : IChatConnectionCoordinator
    {
        public ChatConnectionStatus Status => ChatConnectionStatus.Connected;
        public event EventHandler<ChatConnectionStatus>? StatusChanged;
        public void RegisterEventHandlers() { }
        public void UnregisterEventHandlers() { }
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    /// <summary>最小 ISyncEngine 桩：ChatViewModel 构造器仅订阅其 Completed 事件。</summary>
    private sealed class FakeSyncEngine : ISyncEngine
    {
        public bool IsSyncing => false;
        public ISyncDiagnostics Diagnostics => new FakeSyncDiagnostics();
        public event EventHandler<SyncCompletedEventArgs>? Completed;
        public Task RestartAsync(SessionStamp session, CancellationToken ct = default) => Task.CompletedTask;
        public void Start(SessionStamp session, CancellationToken ct = default) { }
        public Task StopAsync() => Task.CompletedTask;
    }

    private sealed class FakeSyncDiagnostics : ISyncDiagnostics
    {
        public bool IsRunning => false;
        public DateTime? LastSyncUtc => null;
        public long LastDurationMs => 0;
        public string? LastError => null;
        public Core.Diagnostics.SyncFailureRecord? LastFailure => null;
        public long FailCount => 0;
        public int ConsecutiveFailures => 0;
        public int SyncCount => 0;
        public long ConversationsSynced => 0;
        public long MessagesSynced => 0;
        public long LastSyncedMessageAtMs => 0;
    }

    /// <summary>最小 IVoiceRecorder 桩：MessageViewModel 构造器仅订阅其 Progress/AutoCompleted 事件。</summary>
    private sealed class FakeVoiceRecorder : IVoiceRecorder
    {
        public bool IsRecording => false;
        public VoiceRecorderOptions Options { get; } = new(16_000, 1);
        public event Action<VoiceRecordingProgress>? Progress;
        public event Action<VoiceRecording>? AutoCompleted;
        public void Start() { }
        public VoiceRecording? Stop() => null;
        public void Cancel() { }
    }

    /// <summary>最小 IAudioPlayer 桩：MessageViewModel 构造器仅订阅其 Progress/Stopped 事件。</summary>
    private sealed class FakeAudioPlayer : IAudioPlayer
    {
        public bool IsPlaying => false;
        public string? CurrentKey => null;
        public string? SelectedOutputDeviceId { get; private set; }
        public event Action<AudioPlaybackProgress>? Progress;
        public event Action? Stopped;
        public void Play(string key, string wavPath) { }
        public void Pause() { }
        public void Resume() { }
        public void Stop() { }
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public void SelectOutputDevice(string? deviceId) => SelectedOutputDeviceId = deviceId;
        public void Dispose() { }
    }

    /// <summary>最小 IFriendStore 桩：ChatViewModel 构造器仅订阅其 FriendsChanged 事件。</summary>
    private sealed class FakeFriendStore : IFriendStore
    {
        public IReadOnlyList<Chat_App.Infrastructure.Models.LocalFriend> Snapshot
            => Array.Empty<Chat_App.Infrastructure.Models.LocalFriend>();
        public event EventHandler? FriendsChanged;
        public Task<IReadOnlyList<Chat_App.Infrastructure.Models.LocalFriend>> LoadAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Chat_App.Infrastructure.Models.LocalFriend>>(
                Array.Empty<Chat_App.Infrastructure.Models.LocalFriend>());
        public Task<IReadOnlyList<Chat_App.Infrastructure.Models.LocalFriend>> SyncFromServerAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Chat_App.Infrastructure.Models.LocalFriend>>(
                Array.Empty<Chat_App.Infrastructure.Models.LocalFriend>());
        public void Reset() { }
    }

    /// <summary>最小 IChatSessionClient 桩：仅暴露必要的属性。</summary>
    private sealed class SessionStub : IChatSessionClient
    {
        public bool IsConnected { get; set; }
        public bool IsAuthenticated { get; set; }
        public long CurrentUserId { get; set; }
        public long ConnectionGeneration { get; set; }
        public Guid ConnectionId { get; set; } = Guid.NewGuid();
        public Core.Models.SessionStamp CurrentSession => new(CurrentUserId, ConnectionGeneration, ConnectionId);
        // 覆盖接口默认实现：通话信令能力需协商启用，测试桩默认开启。
        public bool SupportsCallSignaling => true;

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
        public Task AuthenticateAsync(string accessToken, long userId, string? sessionId, ulong? deviceIdHash, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(string? reason = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> SendChatMessageAsync(long targetUserId, string? content, IReadOnlyList<string>? attachmentIds = null, string? replyToMessageId = null, long? replyToSenderUserId = null, string? replyToPreview = null, string? forwardedFromMessageId = null, long? forwardedFromSenderUserId = null, string? forwardedFromPreview = null, string? clientMessageId = null, string? conversationId = null, IReadOnlyList<long>? mentionedUserIds = null, IReadOnlyList<global::ChatApp.Shared.Protocol.Tcp.TcpAttachmentRef>? attachments = null, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task SendHeartbeatAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ConversationListResponseDto> QueryConversationListAsync(int limit = 50, bool? beforeIsPinned = null, long? beforePinnedAtMs = null, long? beforeLastMessageAtMs = null, string? beforeConversationId = null, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<ConversationSetPrefsResponseDto> SetConversationPrefsAsync(string conversationId, bool? pinned = null, bool? muted = null, long? mutedUntilMs = null, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<MessageRecallAcknowledgementDto> RecallMessageAsync(string messageId, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<MessageEditAcknowledgementDto> EditMessageAsync(string messageId, string content, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task SendTypingNotifyAsync(long targetUserId, bool isTyping, string? conversationId = null, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<PresenceSnapshotResponseDto> QueryPresenceAsync(IReadOnlyList<long> userIds, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task UnwatchPresenceAsync(IReadOnlyList<long> userIds, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<SyncBootstrapResponseDto> QuerySyncBootstrapAsync(int listLimit = 50, int historyLimitPerConversation = 20, int maxConversationsWithHistory = 10, IReadOnlyList<ConversationSyncWatermarkDto>? watermarks = null, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<SyncBootstrapResponseDto> QuerySyncBootstrapWithRelationshipsAsync(int listLimit = 50, int historyLimitPerConversation = 20, int maxConversationsWithHistory = 10, IReadOnlyList<ConversationSyncWatermarkDto>? watermarks = null, IReadOnlyList<RelationshipSyncWatermarkDto>? relationshipWatermarks = null, int? relationshipListLimit = null, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<MessageHistoryPageDto> QueryMessageHistoryAsync(string conversationId, int limit = 50, long? beforeReceivedAtMs = null, string? beforeMessageId = null, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<MessageHistoryPageDto> QueryMessageHistoryAfterAsync(string conversationId, int limit = 50, string? afterMessageId = null, long? afterReceivedAtMs = null, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<MessageReceiptAckDto> SendMessageReceiptAsync(string conversationId, string? lastReadMessageId, long? lastReadAtMs, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<ConversationMarkReadResponseDto> MarkConversationReadAsync(string conversationId, string? lastReadMessageId = null, long? lastReadAtMs = null, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<CreateGroupResponseDto> CreateGroupAsync(string title, IReadOnlyList<long>? memberUserIds = null, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<AddGroupMembersResponseDto> AddGroupMembersAsync(string conversationId, IReadOnlyList<long> memberUserIds, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<RemoveGroupMemberResponseDto> RemoveGroupMemberAsync(string conversationId, long targetUserId, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<LeaveGroupResponseDto> LeaveGroupAsync(string conversationId, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<DissolveGroupResponseDto> DissolveGroupAsync(string conversationId, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<ChangeMemberRoleResponseDto> ChangeMemberRoleAsync(string conversationId, long targetUserId, ConversationMemberRole newRole, CancellationToken ct = default) => throw new System.NotSupportedException();
        public Task<ListGroupMembersResponseDto> ListGroupMembersAsync(string conversationId, int? pageSize = null, string? cursor = null, CancellationToken ct = default) => throw new System.NotSupportedException();

        public void Dispose() { }
    }

    /// <summary>INotificationService 桩，收集错误消息供断言。</summary>
    public sealed class FakeNotifications : INotificationService
    {
        public List<string> Errors { get; } = new();
        public void ShowError(string message, string title = "错误") => Errors.Add(message);
        public void ShowWarning(string message, string title = "警告") { }
        public void ShowInfo(string message, string title = "提示") { }
        public void ShowSuccess(string message, string title = "成功") { }
    }
}

/// <summary>
/// 扩展方法，通过反射暴露 ChatViewModel 的私有 _notificationService 字段，用于测试断言。
/// </summary>
internal static class ChatViewModelTestExtensions
{
    public static INotificationService FieldNotifications(this ChatViewModel vm)
    {
        var field = typeof(ChatViewModel).GetField("_notificationService",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(ChatViewModel), "_notificationService");
        return (INotificationService)field.GetValue(vm)!;
    }
}