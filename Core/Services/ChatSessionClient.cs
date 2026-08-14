using Core.Buffers;
using Core.Diagnostics;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using TcpClientHello = ChatApp.Shared.Protocol.Tcp.ClientHello;
using TcpGatewayFeature = ChatApp.Shared.Protocol.Tcp.GatewayFeature;
using TcpGoAway = ChatApp.Shared.Protocol.Tcp.GoAway;
using TcpCallConstants = ChatApp.Shared.Protocol.Tcp.TcpCallConstants;
using TcpProtocolErrorCode = ChatApp.Shared.Protocol.Tcp.ProtocolErrorCode;
using TcpProtocolErrorCodeExtensions = ChatApp.Shared.Protocol.Tcp.ProtocolErrorCodeExtensions;
using TcpProtocolErrorFrame = ChatApp.Shared.Protocol.Tcp.ProtocolErrorFrame;
using TcpResumeResponse = ChatApp.Shared.Protocol.Tcp.ResumeResponse;
using TcpServerHello = ChatApp.Shared.Protocol.Tcp.ServerHello;
using TcpFrameConstants = ChatApp.Shared.Protocol.Tcp.TcpFrameConstants;

namespace Core.Services
{
    public class ChatSessionClient : IChatSessionClient, IMetricsSource
    {
        private readonly ITcpClient _tcpClient;
        private readonly IMessagePacketCodec _codec;
        private AuthRequestState? _authState;
        private readonly IPacketBodySerializer _bodySerializer;
        private readonly string _installationId;
        private readonly SemaphoreSlim _connectGate = new(1, 1);
        private HandshakeState? _handshakeState;
        private long _helloSentGeneration;
        private long _handshakeGeneration;
        private int _negotiatedProtocolVersion;
        private uint _negotiatedFeatureBits;
        private int _serverHeartbeatIntervalMs;
        private int _serverMaxPayloadBytes;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ConversationListResponseDto>> _listPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<RelationshipListResponseDto>> _relationshipListPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CallCommandResponseDto>> _callCommandPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ConversationSetPrefsResponseDto>> _prefsPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageRecallAcknowledgementDto>> _recallPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageEditAcknowledgementDto>> _editPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<SyncBootstrapResponseDto>> _syncPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<PresenceSnapshotResponseDto>> _presencePending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageHistoryPageDto>> _historyPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageReceiptAckDto>> _receiptPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ConversationMarkReadResponseDto>> _markReadPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CreateGroupResponseDto>> _createGroupPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<AddGroupMembersResponseDto>> _addGroupMembersPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<RemoveGroupMemberResponseDto>> _removeGroupMemberPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<LeaveGroupResponseDto>> _leaveGroupPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<DissolveGroupResponseDto>> _dissolveGroupPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ChangeMemberRoleResponseDto>> _changeRolePending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ListGroupMembersResponseDto>> _listGroupMembersPending = new(StringComparer.Ordinal);

        private long _lastHeartbeatAckTicks;
        private const int HeartbeatTimeoutSeconds = 60;

        // ── 诊断指标 ──
        private long _packetsReceived;
        private long _packetsSent;
        private long _heartbeatsSent;
        private long _disconnects;
        private long _lastHeartbeatSentTicks;
        private readonly LatencyHistogram _heartbeatRtt = new();
        private readonly LatencyHistogram _rpcLatency = new();
        private long _rpcsSent;
        private long _routeFailures;
        private long _historyConversationIdsInferred;
        private long _historyConversationMismatches;

        // 请求超时（秒）—— 按业务类型分级
        private const int AuthTimeoutSec = 5;
        private const int HandshakeTimeoutSec = 5;
        private const int DefaultRequestTimeoutSec = 8;
        private const int SyncBootstrapTimeoutSec = 12;
        private const int HistoryFetchTimeoutSec = 15;
        private const int ReceiptTimeoutSec = 10;

        // 仅声明客户端已经实现并在本类公开 API 中使用的能力。暂不声明 SessionResume：
        // 当前连接协调器仍固定执行完整鉴权，启用 Resume 前需先让它消费 ResumeResponse。
        private const TcpGatewayFeature AdvertisedFeatures =
            TcpGatewayFeature.CommandCapabilities |
            TcpGatewayFeature.ConversationSync |
            TcpGatewayFeature.ConversationPreferences |
            TcpGatewayFeature.MessageMutation |
            TcpGatewayFeature.PresenceAndTyping |
            TcpGatewayFeature.GroupManagement |
            TcpGatewayFeature.RelationshipRead |
            TcpGatewayFeature.CallSignaling;

        /// <summary>
        /// 关系只读能力是否已协商启用：仅当握手中服务端回显 <see cref="TcpGatewayFeature.RelationshipRead"/>
        /// 能力位时，SyncEngine 才发起关系增量同步；未协商则 fail-closed（不发送关系水位、不应用关系增量）。
        /// </summary>
        public virtual bool SupportsRelationshipRead =>
            (NegotiatedFeatureBits & (uint)TcpGatewayFeature.RelationshipRead) != 0;

        /// <summary>
        /// 通话信令控制面是否已协商启用：仅当握手中服务端回显
        /// <see cref="TcpGatewayFeature.CallSignaling"/> 能力位时，客户端才允许发送通话信令命令；
        /// 未协商则 fail-closed（<see cref="SendCallCommandAsync"/> 抛 <see cref="NotSupportedException"/>）。
        /// </summary>
        public virtual bool SupportsCallSignaling =>
            (NegotiatedFeatureBits & (uint)TcpGatewayFeature.CallSignaling) != 0;

        // 协议上限
        private const int MaxAttachmentsPerMessage = 32;
        private const int MaxAttachmentIdLength = 64;
        private const int MaxMessageIdLength = 64;
        private const int MaxPreviewLength = 256;
        private const int MaxEditContentLength = 4000;
        private const int MaxPresenceIdsPerRequest = 100;
        private const int MaxGroupTitleLength = 100;
        private const int MaxGroupMembersPerRequest = 200;

        public bool IsConnected => _tcpClient.IsConnected;

        public bool IsAuthenticated { get; private set; }

        public long CurrentUserId { get; private set; }

        /// <summary>
        /// 当前在途请求总数（15 类 pending 字典之和）。
        /// 每次请求的 finally 都会回收自身条目，断线时批量失败并清空。
        /// 测试以此断言"每次测试后 pending 字典均为空"，运维可据此观测积压。
        /// </summary>
        public int PendingRequestCount =>
            _listPending.Count + _prefsPending.Count + _recallPending.Count + _editPending.Count
            + _relationshipListPending.Count + _callCommandPending.Count
            + _presencePending.Count + _syncPending.Count + _historyPending.Count + _receiptPending.Count
            + _markReadPending.Count + _createGroupPending.Count + _addGroupMembersPending.Count
            + _removeGroupMemberPending.Count + _leaveGroupPending.Count + _dissolveGroupPending.Count
            + _changeRolePending.Count
            + _listGroupMembersPending.Count;

        /// <summary>
        /// 请求超时缩放系数（仅测试使用）：乘法缩放全部请求/鉴权超时，
        /// 使"服务器不响应"类验收测试在毫秒级完成，不依赖固定 Delay。
        /// </summary>
        internal double RequestTimeoutScale { get; set; } = 1.0;

        private TimeSpan ScaleTimeout(TimeSpan timeout) =>
            RequestTimeoutScale <= 0
                ? timeout
                : TimeSpan.FromTicks((long)(timeout.Ticks * RequestTimeoutScale));

        // 连接代际与连接 Id：每次成功建立连接递增/更换，
        // 供入站事件队列做 SessionStamp 代际校验，防止旧连接/旧账户事件写入新会话状态。
        private long _connectionGeneration;
        private Guid _connectionId;

        public long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);

        public Guid ConnectionId => _connectionId;

        /// <summary>当前连接是否已收到并验证 ServerHello。</summary>
        public bool HasCompletedHandshake =>
            ConnectionGeneration != 0 &&
            Volatile.Read(ref _handshakeGeneration) == ConnectionGeneration;

        public ushort NegotiatedProtocolVersion =>
            (ushort)Volatile.Read(ref _negotiatedProtocolVersion);

        public uint NegotiatedFeatureBits => Volatile.Read(ref _negotiatedFeatureBits);

        public int ServerHeartbeatIntervalMs => Volatile.Read(ref _serverHeartbeatIntervalMs);

        public int ServerMaxPayloadBytes => Volatile.Read(ref _serverMaxPayloadBytes);

        /// <summary>
        /// 当前会话戳：未鉴权或已断开时为 SessionStamp.None。
        /// </summary>
        public SessionStamp CurrentSession =>
            IsAuthenticated && CurrentUserId > 0
                ? new SessionStamp(CurrentUserId, ConnectionGeneration, ConnectionId)
                : SessionStamp.None;

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

        // ── 群聊事件 ──
        public event EventHandler<MemberJoinedUpdateDto>? GroupMemberJoined;
        public event EventHandler<MemberLeftUpdateDto>? GroupMemberLeft;
        public event EventHandler<MemberRemovedUpdateDto>? GroupMemberRemoved;
        public event EventHandler<RoleChangedUpdateDto>? GroupRoleChanged;
        public event EventHandler<MembersAddedUpdateDto>? GroupMembersAdded;
        public event EventHandler<ConversationDissolvedUpdateDto>? GroupConversationDissolved;

        public ChatSessionClient(ITcpClient tcpClient, IMessagePacketCodec codec, IPacketBodySerializer bodySerializer)
            : this(tcpClient, codec, bodySerializer, Guid.NewGuid().ToString("N"))
        {
        }

        /// <summary>
        /// 使用本机持久化设备标识派生稳定的协议 InstallationId。
        /// 保留三参数构造函数供现有测试与手工调用使用；依赖注入会优先选择此重载。
        /// </summary>
        public ChatSessionClient(
            ITcpClient tcpClient,
            IMessagePacketCodec codec,
            IPacketBodySerializer bodySerializer,
            ILocalDeviceIdentity deviceIdentity)
            : this(tcpClient, codec, bodySerializer, DeriveInstallationId(deviceIdentity))
        {
        }

        private ChatSessionClient(
            ITcpClient tcpClient,
            IMessagePacketCodec codec,
            IPacketBodySerializer bodySerializer,
            string installationId)
        {
            _tcpClient = tcpClient;
            _codec = codec;
            _bodySerializer = bodySerializer;
            _installationId = installationId;

            _tcpClient.ConnectionStatusChanged += OnConnectionStatusChanged;
            _tcpClient.OnDataChunkReceived += OnDataChunkReceived;
        }

        private static string DeriveInstallationId(ILocalDeviceIdentity deviceIdentity)
        {
            ArgumentNullException.ThrowIfNull(deviceIdentity);
            if (string.IsNullOrWhiteSpace(deviceIdentity.DeviceId))
                throw new ArgumentException("本机设备标识不能为空", nameof(deviceIdentity));

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(deviceIdentity.DeviceId));
            return Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant();
        }

        /// <summary>
        /// 当 TCP 客户端接收到新的数据块时触发该事件处理程序，负责将接收到的数据块追加到消息包解码器中，并尝试从中解析出完整的消息包。
        /// 如果成功解析出一个消息包，则根据消息包的类型进行相应的处理，例如处理认证结果、处理聊天消息等。通过这个事件处理程序，ChatSessionClient 能够及时响应服务器发送的数据，并根据数据内容更新连接状态、认证状态以及触发相应的事件通知外部订阅者。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <exception cref="NotImplementedException"></exception>
        private void OnDataChunkReceived(object? sender, ReadOnlyMemory<byte> rawData)
        {
            _codec.Append(rawData);

            // 同步路由：Body 是零拷贝切片，RoutePacket 在下次 Append 前消费完毕
            while (_codec.TryRead(out var packet))
            {
                try
                {
                    RoutePacket(packet);
                }
                catch (Exception ex)
                {
                    HandleRouteFailure(packet.Command, ex);
                }
            }
        }

        /// <summary>
        /// 当 TCP 客户端的连接状态发生改变时触发该事件处理程序，负责根据新的连接状态来更新 ChatSessionClient 的状态，并触发相应的事件通知外部订阅者。
        /// </summary>
        private void OnConnectionStatusChanged(object? sender, ConnectionStateChangedEventArgs e)
        {
            if (e.State == ConnectionState.Connected)
            {
                Connected?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                Interlocked.Increment(ref _disconnects);
                IsAuthenticated = false;
                Volatile.Write(ref _helloSentGeneration, 0);
                Volatile.Write(ref _handshakeGeneration, 0);
                Interlocked.Exchange(ref _lastHeartbeatAckTicks, 0);
                var generation = Volatile.Read(ref _connectionGeneration);
                var handshakeState = Volatile.Read(ref _handshakeState);
                if (handshakeState is not null && handshakeState.Generation == generation)
                    handshakeState.Tcs.TrySetException(new IOException(e.Reason ?? "连接已断开"));
                // 鉴权中的请求必须显式结束，否则等待方只能靠超时兜底。
                // 仅当前代际的鉴权请求会被失败：旧连接的断线事件不得误杀新连接的鉴权。
                var authState = Volatile.Read(ref _authState);
                if (authState is not null
                    && authState.Generation == generation)
                {
                    authState.Tcs.TrySetException(new IOException(e.Reason ?? "连接已断开"));
                }
                FailPendingRequests(new IOException(e.Reason ?? "连接已断开"));
                ConnectionClosed?.Invoke(this, e.Reason ?? "连接已断开");
            }
        }

        public async Task AuthenticateAsync(string accessToken, long userId, string? sessionId, ulong? deviceIdHash, CancellationToken ct = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("TCP 尚未连接！");

            var currentGeneration = Volatile.Read(ref _connectionGeneration);
            if (Volatile.Read(ref _handshakeGeneration) != currentGeneration)
                throw new InvalidOperationException("TCP 协议握手尚未完成，不能发送鉴权请求");

            // 为未来 Resume 路径保留幂等语义：若握手已恢复同一用户，不再重复鉴权。
            if (IsAuthenticated && CurrentUserId == userId)
                return;

            // 鉴权请求状态对象：记录代际，供响应/断线按代际关联，杜绝跨连接误配。
            var state = new AuthRequestState
            {
                Generation = Volatile.Read(ref _connectionGeneration),
                Tcs = new TaskCompletionSource<AuthResponseDto>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            // CAS 防并发：新鉴权不得覆盖在途鉴权。
            if (Interlocked.CompareExchange(ref _authState, state, null) is not null)
                throw new InvalidOperationException("鉴权请求已在进行中");

            var authRequest = new AuthRequestDto
            {
                AccessToken = accessToken,
                UserId = userId,
                SessionId = sessionId,
                DeviceIdHash = deviceIdHash
            };

            try
            {
                await SendPacketAsync(PacketCommand.AuthenticationRequest, authRequest, ct).ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ScaleTimeout(TimeSpan.FromSeconds(AuthTimeoutSec)));
                try
                {
                    await state.Tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 外部取消：静默重抛，不触发事件。
                    throw;
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    // 内部超时：明确区分于外部取消。
                    AuthenticationFailed?.Invoke(this, "鉴权超时，服务器未响应");
                    throw new TimeoutException("鉴权超时，服务器未响应");
                }
                // 成功/服务端拒绝/协议错误/断线：由 HandleAuthResponse / HandleErrorCommand / 断线处理器完成 TCS。
            }
            finally
            {
                // 仅清空仍指向本次请求的引用，避免误清后续请求（完成后不留悬垂引用）。
                Interlocked.CompareExchange(ref _authState, null, state);
            }
        }

        public async Task ConnectAsync(ServerEndpoint endpoint, CancellationToken ct = default)
        {
            await _connectGate.WaitAsync(ct).ConfigureAwait(false);
            HandshakeState? state = null;
            try
            {
                // 整个 TCP 建连 + 协议握手串行化，避免并发重连覆盖代次或让旧握手完成新连接。
                _codec.Reset();
                await _tcpClient.ConnectAsync(endpoint, ct).ConfigureAwait(false);

                var generation = Interlocked.Increment(ref _connectionGeneration);
                _connectionId = Guid.NewGuid();
                state = new HandshakeState
                {
                    Generation = generation,
                    Tcs = new TaskCompletionSource<TcpServerHello>(TaskCreationOptions.RunContinuationsAsynchronously)
                };
                var previousState = Interlocked.Exchange(ref _handshakeState, state);
                previousState?.Tcs.TrySetException(new IOException("连接已被新的握手替换"));

                Volatile.Write(ref _helloSentGeneration, 0);
                Volatile.Write(ref _handshakeGeneration, 0);
                Volatile.Write(ref _negotiatedProtocolVersion, 0);
                Volatile.Write(ref _negotiatedFeatureBits, 0);
                Volatile.Write(ref _serverHeartbeatIntervalMs, 0);
                Volatile.Write(ref _serverMaxPayloadBytes, 0);

                var hello = new TcpClientHello
                {
                    ProtocolVersion = TcpFrameConstants.CurrentProtocolVersion,
                    FeatureBits = (uint)AdvertisedFeatures,
                    InstallationId = _installationId,
                    ClientTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    ResumeToken = null,
                    MaxPayloadBytes = MessagePacket.MaxBodySize
                };

                // 先发布本代次握手已发起状态，兼容测试/回环服务端在 SendAsync 内同步回包。
                Volatile.Write(ref _helloSentGeneration, generation);
                await SendPacketAsync(PacketCommand.ClientHello, hello, ct, priority: true)
                    .ConfigureAwait(false);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(ScaleTimeout(TimeSpan.FromSeconds(HandshakeTimeoutSec)));
                try
                {
                    await state.Tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await DisconnectAfterFailedHandshakeAsync("TCP 协议握手已取消").ConfigureAwait(false);
                    throw;
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
                {
                    await DisconnectAfterFailedHandshakeAsync("等待 ServerHello 超时").ConfigureAwait(false);
                    throw new TimeoutException("TCP 协议握手超时，服务器未返回 ServerHello");
                }
            }
            catch
            {
                if (_tcpClient.IsConnected)
                    await DisconnectAfterFailedHandshakeAsync("TCP 协议握手失败").ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (state is not null)
                    Interlocked.CompareExchange(ref _handshakeState, null, state);
                _connectGate.Release();
            }
        }

        private void HandleRouteFailure(PacketCommand command, Exception exception)
        {
            Interlocked.Increment(ref _routeFailures);
            var contractException = new InvalidDataException(
                $"无法解析或处理 {command} 响应，协议契约可能不兼容。",
                exception);
            Trace.TraceError(
                "路由帧失败 Command={0}, Error={1}",
                command,
                exception.Message);

            // 无法可靠读取 RequestId 时，失败该命令全部在途请求；比等待业务超时更可观测，
            // 且仅影响同一响应类型，不扩大到其他并行 RPC。
            switch (command)
            {
                case PacketCommand.MessageHistoryPage:
                    FailAll(_historyPending, contractException);
                    break;
                case PacketCommand.SyncBootstrapResponse:
                    FailAll(_syncPending, contractException);
                    break;
                case PacketCommand.RelationshipListResponse:
                    FailAll(_relationshipListPending, contractException);
                    break;
                case PacketCommand.CallCommandResponse:
                    FailAll(_callCommandPending, contractException);
                    break;
            }
        }

        private async Task DisconnectAfterFailedHandshakeAsync(string reason)
        {
            try
            {
                await _tcpClient.DisconnectAsync(reason, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"握手失败后断开连接失败: {ex.Message}");
            }
        }


        /// <summary>
        /// DisconnectAsync 方法负责断开与服务器的连接，并重置 ChatSessionClient 的认证状态和当前用户 ID。该方法首先将 IsAuthenticated 设置为 false，表示当前不再处于认证状态，然后将 CurrentUserId 重置为 0，表示没有当前用户 ID。接下来，调用 TCP 客户端的 Disconnect 方法来断开与服务器的连接，并传递一个可选的 reason 参数来描述断开连接的原因。最后，通过 await Task.CompletedTask 来保持方法的异步签名，以便在需要时可以进行异步操作。通过这个方法，ChatSessionClient 可以主动断开与服务器的连接，并及时更新状态以反映当前的连接和认证状态。
        /// </summary>
        /// <param name="reason"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async Task DisconnectAsync(string? reason = null, CancellationToken ct = default)
        {
            IsAuthenticated = false;
            CurrentUserId = 0;
            Volatile.Write(ref _helloSentGeneration, 0);
            Volatile.Write(ref _handshakeGeneration, 0);
            Interlocked.Exchange(ref _lastHeartbeatAckTicks, 0);
            var handshakeState = Volatile.Read(ref _handshakeState);
            handshakeState?.Tcs.TrySetException(new IOException(reason ?? "连接已断开"));
            // 真正等待收发循环退出后再返回。
            await _tcpClient.DisconnectAsync(reason, ct).ConfigureAwait(false);
        }

        public void Dispose()
        {
            Volatile.Read(ref _handshakeState)?.Tcs.TrySetException(
                new ObjectDisposedException(nameof(ChatSessionClient)));
            _tcpClient.OnDataChunkReceived -= OnDataChunkReceived;
            _tcpClient.ConnectionStatusChanged -= OnConnectionStatusChanged;
            _tcpClient.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// SendChatMessageAsync 方法负责将指定的目标用户 ID 和消息内容封装成一个 ChatMessageDto 对象，并通过 SendPacketAsync 方法将其作为一个聊天消息包发送到服务器。该方法首先创建一个 ChatMessageDto 实例，设置目标用户 ID、消息内容和发送时间，然后调用 SendPacketAsync 方法，将 PacketCommand.ChatMessage 命令和 ChatMessageDto 对象作为消息包的内容发送到服务器，以实现向指定用户发送聊天消息的功能。
        /// </summary>
        /// <param name="targetUserId"></param>
        /// <param name="content"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        public async Task<string> SendChatMessageAsync(
            long targetUserId,
            string? content,
            IReadOnlyList<string>? attachmentIds = null,
            string? replyToMessageId = null,
            long? replyToSenderUserId = null,
            string? replyToPreview = null,
            string? forwardedFromMessageId = null,
            long? forwardedFromSenderUserId = null,
            string? forwardedFromPreview = null,
            string? clientMessageIdParam = null,
            string? conversationId = null,
            IReadOnlyList<long>? mentionedUserIds = null,
            CancellationToken ct = default)
        {
            var hasAttachments = attachmentIds is { Count: > 0 };
            if (string.IsNullOrWhiteSpace(content) && !hasAttachments)
                throw new ArgumentException("消息文本与附件至少需要其一。");

            if (attachmentIds is { Count: > MaxAttachmentsPerMessage })
                throw new ArgumentException($"单条消息最多 {MaxAttachmentsPerMessage} 个附件。");

            if (attachmentIds?.Any(static id =>
                    string.IsNullOrWhiteSpace(id) || id.Length > MaxAttachmentIdLength) == true)
            {
                throw new ArgumentException("附件 Id 无效。");
            }

            var hasReply = !string.IsNullOrWhiteSpace(replyToMessageId);
            var hasForward = !string.IsNullOrWhiteSpace(forwardedFromMessageId);
            if (hasReply && hasForward)
                throw new ArgumentException("回复与转发不能同时设置。");

            if (hasReply)
            {
                if (replyToMessageId!.Length > MaxMessageIdLength)
                    throw new ArgumentException("回复目标消息 Id 过长。");
                if (replyToSenderUserId is null or <= 0)
                    throw new ArgumentException("回复目标发送方无效。");
            }
            else if (replyToSenderUserId is not null || !string.IsNullOrWhiteSpace(replyToPreview))
            {
                throw new ArgumentException("缺少回复目标消息 Id。");
            }

            if (hasForward)
            {
                if (forwardedFromMessageId!.Length > MaxMessageIdLength)
                    throw new ArgumentException("转发来源消息 Id 过长。");
                if (forwardedFromSenderUserId is null or <= 0)
                    throw new ArgumentException("转发来源发送方无效。");
            }
            else if (forwardedFromSenderUserId is not null
                     || !string.IsNullOrWhiteSpace(forwardedFromPreview))
            {
                throw new ArgumentException("缺少转发来源消息 Id。");
            }

            var preview = string.IsNullOrWhiteSpace(replyToPreview)
                ? null
                : (replyToPreview.Length <= MaxPreviewLength ? replyToPreview : replyToPreview[..MaxPreviewLength]);
            var forwardPreview = string.IsNullOrWhiteSpace(forwardedFromPreview)
                ? null
                : (forwardedFromPreview.Length <= MaxPreviewLength
                    ? forwardedFromPreview
                    : forwardedFromPreview[..MaxPreviewLength]);

            var clientMessageId = string.IsNullOrWhiteSpace(clientMessageIdParam)
                ? Guid.CreateVersion7().ToString("N")
                : clientMessageIdParam;
            var chatPayload = new ChatMessageDto
            {
                MessageId = clientMessageId,
                ConversationId = string.IsNullOrWhiteSpace(conversationId) ? null : conversationId.Trim(),
                TargetUserId = targetUserId,
                Content = content,
                SentUtc = DateTime.UtcNow,
                AttachmentIds = hasAttachments ? attachmentIds : null,
                ReplyToMessageId = hasReply ? replyToMessageId : null,
                ReplyToSenderUserId = hasReply ? replyToSenderUserId : null,
                ReplyToPreview = hasReply ? preview : null,
                ForwardedFromMessageId = hasForward ? forwardedFromMessageId : null,
                ForwardedFromSenderUserId = hasForward ? forwardedFromSenderUserId : null,
                ForwardedFromPreview = hasForward ? forwardPreview : null,
                MentionedUserIds = mentionedUserIds is { Count: > 0 }
                    ? NormalizeMemberIds(mentionedUserIds)
                    : null
            };

            await SendPacketAsync(PacketCommand.ChatMessage, chatPayload, ct);
            return clientMessageId;
        }

        public async Task SendHeartbeatAsync(CancellationToken ct = default)
        {
            // 检查半开连接：距上次 ACK 超过阈值则主动断连
            var lastAckTicks = Interlocked.Read(ref _lastHeartbeatAckTicks);
            if (lastAckTicks > 0)
            {
                var elapsed = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - lastAckTicks);
                if (elapsed.TotalSeconds > HeartbeatTimeoutSeconds)
                {
                    Debug.WriteLine($"心跳 ACK 超时 {(int)elapsed.TotalSeconds}s，判定半开连接，主动断开");
                    _ = DisconnectAsync("心跳超时", ct);
                    return;
                }
            }

            Interlocked.Exchange(ref _lastHeartbeatSentTicks, DateTime.UtcNow.Ticks);
            Interlocked.Increment(ref _heartbeatsSent);
            // 高优通道发送：心跳不被普通消息积压饿死，保活/半开检测按时执行。
            await SendPacketAsync(PacketCommand.Heartbeat, (object?)null, ct, priority: true);
        }

        public Task<ConversationListResponseDto> QueryConversationListAsync(
            int limit = 50,
            bool? beforeIsPinned = null,
            long? beforePinnedAtMs = null,
            long? beforeLastMessageAtMs = null,
            string? beforeConversationId = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            return SendRequestAsync(_listPending, PacketCommand.ConversationListRequest,
                new ConversationListRequestDto
                {
                    BeforeIsPinned = beforeIsPinned,
                    BeforePinnedAtMs = beforePinnedAtMs,
                    BeforeLastMessageAtMs = beforeLastMessageAtMs,
                    BeforeConversationId = beforeConversationId,
                    Limit = Math.Clamp(limit, 1, 100)
                },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "会话列表请求 Id 冲突", ct);
        }

        public Task<RelationshipListResponseDto> QueryRelationshipListAsync(
            RelationshipListTypeDto listType,
            int? pageSize = null,
            string? cursor = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (listType is < RelationshipListTypeDto.Friends or > RelationshipListTypeDto.BlockedUsers)
                throw new ArgumentOutOfRangeException(nameof(listType));
            if (pageSize is < 1 or > 200)
                throw new ArgumentOutOfRangeException(nameof(pageSize));

            return SendRequestAsync(_relationshipListPending, PacketCommand.RelationshipListRequest,
                new RelationshipListRequestDto
                {
                    ListType = listType,
                    PageSize = pageSize,
                    Cursor = string.IsNullOrWhiteSpace(cursor) ? null : cursor
                },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "关系列表请求 Id 冲突", ct);
        }

        /// <summary>
        /// 发送一条通话信令命令（invite/ringing/accept/reject/cancel/end/reconnect）。
        /// <paramref name="request"/> 以 call id + command id 幂等、单调 revision 排序；
        /// grant 为 Server 签发的短期授权输入，客户端只原样携带不做校验。
        /// 仅当握手协商到 <see cref="TcpGatewayFeature.CallSignaling"/> 能力位时允许发送，否则 fail-closed。
        /// </summary>
        public Task<CallCommandResponseDto> SendCallCommandAsync(
            CallCommandRequestDto request,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (!SupportsCallSignaling)
                throw new NotSupportedException("服务器未协商通话信令能力（CallSignaling）。");
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(request.CommandId) || request.CommandId.Length > TcpCallConstants.MaxCommandIdBytes)
                throw new ArgumentException("command id 缺失或超长。", nameof(request));
            if (string.IsNullOrWhiteSpace(request.CallId) || request.CallId.Length > TcpCallConstants.MaxCallIdBytes)
                throw new ArgumentException("call id 缺失或超长。", nameof(request));
            if (request.ActorUserId <= 0)
                throw new ArgumentException("通话参与者（actor）无效。", nameof(request));
            if (request.Sdp is { Length: > 0 } && request.Sdp.Length > TcpCallConstants.MaxSdpBytes)
                throw new ArgumentException($"SDP 载荷超过预算（>{TcpCallConstants.MaxSdpBytes} 字节）。", nameof(request));

            return SendRequestAsync(_callCommandPending, PacketCommand.CallCommandRequest, request,
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec), "通话信令命令 Id 冲突", ct);
        }

        public Task<ConversationSetPrefsResponseDto> SetConversationPrefsAsync(
            string conversationId,
            bool? pinned = null,
            bool? muted = null,
            long? mutedUntilMs = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("conversationId 不能为空");
            if (pinned is null && muted is null)
                throw new ArgumentException("pinned 与 muted 至少需要其一");

            return SendRequestAsync(_prefsPending, PacketCommand.ConversationSetPrefsRequest,
                new ConversationSetPrefsRequestDto
                {
                    ConversationId = conversationId.Trim(),
                    Pinned = pinned,
                    Muted = muted,
                    MutedUntilMs = mutedUntilMs
                },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "会话偏好请求 Id 冲突", ct);
        }

        public Task<MessageRecallAcknowledgementDto> RecallMessageAsync(
            string messageId,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > MaxMessageIdLength)
                throw new ArgumentException("messageId 无效");

            return SendRequestAsync(_recallPending, PacketCommand.MessageRecallRequest,
                new MessageRecallRequestDto
                {
                    MessageId = messageId.Trim()
                },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "消息撤回请求 Id 冲突", ct);
        }

        public Task<MessageEditAcknowledgementDto> EditMessageAsync(
            string messageId,
            string content,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > MaxMessageIdLength)
                throw new ArgumentException("messageId 无效");
            ArgumentNullException.ThrowIfNull(content);
            var trimmed = content.Trim();
            if (trimmed.Length == 0)
                throw new ArgumentException("编辑内容不能为空");
            if (trimmed.Length > MaxEditContentLength)
                throw new ArgumentException("编辑内容过长");

            return SendRequestAsync(_editPending, PacketCommand.MessageEditRequest,
                new MessageEditRequestDto
                {
                    MessageId = messageId.Trim(),
                    Content = trimmed
                },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "消息编辑请求 Id 冲突", ct);
        }

        // ──────────── 群聊命令 ────────────

        public Task<CreateGroupResponseDto> CreateGroupAsync(
            string title,
            IReadOnlyList<long>? memberUserIds = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            var trimmed = title?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                throw new ArgumentException("群名称不能为空");
            if (trimmed.Length > MaxGroupTitleLength)
                throw new ArgumentException("群名称过长");
            var members = NormalizeMemberIds(memberUserIds);

            return SendRequestAsync(_createGroupPending, PacketCommand.CreateGroupRequest,
                new CreateGroupRequestDto
                {
                    Title = trimmed,
                    MemberUserIds = members.Length > 0 ? members : null
                },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "创建群聊请求 Id 冲突", ct);
        }

        public Task<AddGroupMembersResponseDto> AddGroupMembersAsync(
            string conversationId,
            IReadOnlyList<long> memberUserIds,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("conversationId 不能为空");
            var members = NormalizeMemberIds(memberUserIds);
            if (members.Length == 0)
                throw new ArgumentException("至少需要一名成员");

            return SendRequestAsync(_addGroupMembersPending, PacketCommand.AddGroupMembersRequest,
                new AddGroupMembersRequestDto
                {
                    ConversationId = conversationId.Trim(),
                    MemberUserIds = members
                },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "添加群成员请求 Id 冲突", ct);
        }

        public Task<RemoveGroupMemberResponseDto> RemoveGroupMemberAsync(
            string conversationId,
            long targetUserId,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("conversationId 不能为空");
            if (targetUserId <= 0)
                throw new ArgumentException("targetUserId 无效");

            return SendRequestAsync(_removeGroupMemberPending, PacketCommand.RemoveGroupMemberRequest,
                new RemoveGroupMemberRequestDto
                {
                    ConversationId = conversationId.Trim(),
                    TargetUserId = targetUserId
                },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "移除群成员请求 Id 冲突", ct);
        }

        public Task<LeaveGroupResponseDto> LeaveGroupAsync(
            string conversationId,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("conversationId 不能为空");

            return SendRequestAsync(_leaveGroupPending, PacketCommand.LeaveGroupRequest,
                new LeaveGroupRequestDto { ConversationId = conversationId.Trim() },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "退出群聊请求 Id 冲突", ct);
        }

        public Task<DissolveGroupResponseDto> DissolveGroupAsync(
            string conversationId,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("conversationId 不能为空");

            return SendRequestAsync(_dissolveGroupPending, PacketCommand.DissolveGroupRequest,
                new DissolveGroupRequestDto { ConversationId = conversationId.Trim() },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "解散群聊请求 Id 冲突", ct);
        }

        public Task<ChangeMemberRoleResponseDto> ChangeMemberRoleAsync(
            string conversationId,
            long targetUserId,
            ConversationMemberRole newRole,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("conversationId 不能为空");
            if (targetUserId <= 0)
                throw new ArgumentException("targetUserId 无效");
            if (newRole is not (ConversationMemberRole.Owner or ConversationMemberRole.Admin or ConversationMemberRole.Member))
                throw new ArgumentException("newRole 无效");

            return SendRequestAsync(_changeRolePending, PacketCommand.ChangeMemberRoleRequest,
                new ChangeMemberRoleRequestDto
                {
                    ConversationId = conversationId.Trim(),
                    TargetUserId = targetUserId,
                    NewRole = newRole
                },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "变更成员角色请求 Id 冲突", ct);
        }

        public Task<ListGroupMembersResponseDto> ListGroupMembersAsync(
            string conversationId,
            int? pageSize = null,
            string? cursor = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("conversationId 不能为空");

            return SendRequestAsync(_listGroupMembersPending, PacketCommand.ListGroupMembersRequest,
                new ListGroupMembersRequestDto
                {
                    ConversationId = conversationId.Trim(),
                    PageSize = pageSize is > 0 ? Math.Min(pageSize.Value, 200) : null,
                    Cursor = cursor
                },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "群成员列表请求 Id 冲突", ct);
        }

        /// <summary>规整成员 Id 列表：过滤无效值、去重、截断到上限。</summary>
        private static long[] NormalizeMemberIds(IReadOnlyList<long>? userIds)
            => (userIds ?? []).Where(static id => id > 0).Distinct().Take(MaxGroupMembersPerRequest).ToArray();

        public async Task SendTypingNotifyAsync(
            long targetUserId,
            bool isTyping,
            string? conversationId = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (targetUserId <= 0)
                return;

            await SendPacketAsync(
                PacketCommand.TypingNotify,
                new TypingNotifyDto
                {
                    TargetUserId = targetUserId,
                    ConversationId = conversationId,
                    IsTyping = isTyping
                },
                ct);
        }

        public Task<PresenceSnapshotResponseDto> QueryPresenceAsync(
            IReadOnlyList<long> userIds,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            ArgumentNullException.ThrowIfNull(userIds);

            return SendRequestAsync(_presencePending, PacketCommand.PresenceQuery,
                new PresenceQueryRequestDto
                {
                    UserIds = NormalizePresenceIds(userIds)
                },
                TimeSpan.FromSeconds(DefaultRequestTimeoutSec),
                "在线状态请求 Id 冲突", ct);
        }

        public async Task UnwatchPresenceAsync(
            IReadOnlyList<long> userIds,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            ArgumentNullException.ThrowIfNull(userIds);

            var ids = NormalizePresenceIds(userIds);
            if (ids.Length == 0)
                return;

            await SendPacketAsync(
                PacketCommand.PresenceUnwatch,
                new PresenceUnwatchRequestDto { UserIds = ids },
                ct);
        }

        public Task<SyncBootstrapResponseDto> QuerySyncBootstrapAsync(
            int listLimit = 50,
            int historyLimitPerConversation = 20,
            int maxConversationsWithHistory = 10,
            IReadOnlyList<ConversationSyncWatermarkDto>? watermarks = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            return SendRequestAsync(_syncPending, PacketCommand.SyncBootstrapRequest,
                new SyncBootstrapRequestDto
                {
                    ListLimit = Math.Clamp(listLimit, 1, 100),
                    HistoryLimitPerConversation = Math.Clamp(historyLimitPerConversation, 1, 50),
                    MaxConversationsWithHistory = Math.Clamp(maxConversationsWithHistory, 0, 20),
                    Watermarks = watermarks
                },
                TimeSpan.FromSeconds(SyncBootstrapTimeoutSec),
                "同步引导请求 Id 冲突", ct);
        }

        public Task<SyncBootstrapResponseDto> QuerySyncBootstrapWithRelationshipsAsync(
            int listLimit = 50,
            int historyLimitPerConversation = 20,
            int maxConversationsWithHistory = 10,
            IReadOnlyList<ConversationSyncWatermarkDto>? watermarks = null,
            IReadOnlyList<RelationshipSyncWatermarkDto>? relationshipWatermarks = null,
            int? relationshipListLimit = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            return SendRequestAsync(_syncPending, PacketCommand.SyncBootstrapRequest,
                new SyncBootstrapRequestDto
                {
                    ListLimit = Math.Clamp(listLimit, 1, 100),
                    HistoryLimitPerConversation = Math.Clamp(historyLimitPerConversation, 1, 50),
                    MaxConversationsWithHistory = Math.Clamp(maxConversationsWithHistory, 0, 20),
                    Watermarks = watermarks,
                    RelationshipWatermarks = relationshipWatermarks,
                    RelationshipListLimit = relationshipListLimit is null
                        ? null
                        : Math.Clamp(relationshipListLimit.Value, 1, 200)
                },
                TimeSpan.FromSeconds(SyncBootstrapTimeoutSec),
                "同步引导请求 Id 冲突", ct);
        }

        public Task<MessageHistoryPageDto> QueryMessageHistoryAsync(
            string conversationId,
            int limit = 50,
            long? beforeReceivedAtMs = null,
            string? beforeMessageId = null,
            CancellationToken ct = default)
            => QueryMessageHistoryCoreAsync(
                conversationId,
                limit,
                beforeReceivedAtMs,
                beforeMessageId,
                afterReceivedAtMs: null,
                afterMessageId: null,
                ct);

        public Task<MessageHistoryPageDto> QueryMessageHistoryAfterAsync(
            string conversationId,
            long afterReceivedAtMs,
            string afterMessageId,
            int limit = 50,
            CancellationToken ct = default)
            => QueryMessageHistoryCoreAsync(
                conversationId,
                limit,
                beforeReceivedAtMs: null,
                beforeMessageId: null,
                afterReceivedAtMs,
                afterMessageId,
                ct);

        private async Task<MessageHistoryPageDto> QueryMessageHistoryCoreAsync(
            string conversationId,
            int limit,
            long? beforeReceivedAtMs,
            string? beforeMessageId,
            long? afterReceivedAtMs,
            string? afterMessageId,
            CancellationToken ct)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("会话 Id 不能为空", nameof(conversationId));
            if (afterReceivedAtMs is <= 0)
                throw new ArgumentOutOfRangeException(nameof(afterReceivedAtMs));
            if (afterReceivedAtMs.HasValue != !string.IsNullOrWhiteSpace(afterMessageId))
                throw new ArgumentException("正向历史游标必须同时包含时间和消息 Id。", nameof(afterMessageId));
            if (beforeReceivedAtMs.HasValue != !string.IsNullOrWhiteSpace(beforeMessageId))
                throw new ArgumentException("反向历史游标必须同时包含时间和消息 Id。", nameof(beforeMessageId));
            if (beforeReceivedAtMs.HasValue && afterReceivedAtMs.HasValue)
                throw new ArgumentException("历史请求不能同时包含正向和反向游标。");

            var requestedConversationId = conversationId.Trim();

            var response = await SendRequestAsync(_historyPending, PacketCommand.MessageHistoryRequest,
                new MessageHistoryRequestDto
                {
                    ConversationId = requestedConversationId,
                    BeforeReceivedAtMs = beforeReceivedAtMs,
                    BeforeMessageId = beforeMessageId,
                    AfterReceivedAtMs = afterReceivedAtMs,
                    AfterMessageId = afterMessageId,
                    Limit = Math.Clamp(limit, 1, 100)
                },
                TimeSpan.FromSeconds(HistoryFetchTimeoutSec),
                "历史拉取请求 Id 冲突", ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(response.ConversationId))
            {
                // 兼容旧 Gateway；明确留下诊断标记，避免调用方误以为服务端已返回完整契约。
                response.ConversationId = requestedConversationId;
                Interlocked.Increment(ref _historyConversationIdsInferred);
                Trace.TraceWarning(
                    "MessageHistoryPage 缺少 ConversationId，已按请求会话推断。RequestId={0} ConversationId={1}",
                    response.RequestId,
                    requestedConversationId);
            }
            else if (!string.Equals(response.ConversationId, requestedConversationId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _historyConversationMismatches);
                throw new InvalidDataException(
                    $"历史响应会话错配：期望 {requestedConversationId}，实际 {response.ConversationId}。");
            }

            return response;
        }
        public Task<MessageReceiptAckDto> SendMessageReceiptAsync(
            string conversationId,
            string? lastReadMessageId,
            long? lastReadAtMs,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("会话 Id 不能为空", nameof(conversationId));

            return SendRequestAsync(_receiptPending, PacketCommand.MessageReceipt,
                new MessageReceiptDto
                {
                    ConversationId = conversationId.Trim(),
                    LastReadMessageId = lastReadMessageId,
                    LastReadAtMs = lastReadAtMs,
                    ReaderUserId = CurrentUserId
                },
                TimeSpan.FromSeconds(ReceiptTimeoutSec),
                "已读回执请求 Id 冲突", ct);
        }
        public Task<ConversationMarkReadResponseDto> MarkConversationReadAsync(
            string conversationId,
            string? lastReadMessageId = null,
            long? lastReadAtMs = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("会话 Id 不能为空", nameof(conversationId));

            return SendRequestAsync(_markReadPending, PacketCommand.ConversationMarkReadRequest,
                new ConversationMarkReadRequestDto
                {
                    ConversationId = conversationId.Trim(),
                    LastReadMessageId = lastReadMessageId,
                    LastReadAtMs = lastReadAtMs
                },
                TimeSpan.FromSeconds(ReceiptTimeoutSec),
                "标记已读请求 Id 冲突", ct);
        }
        private void EnsureAuthenticated()
        {
            if (!IsConnected || !IsAuthenticated)
                throw new InvalidOperationException("TCP 未连接或未鉴权");
        }

        /// <summary>
        /// 统一请求-响应模板：生成 requestId → 注册 TCS → 发包 → 带超时等待响应 → finally 清理。
        /// 调用方需先 EnsureAuthenticated 并完成参数校验。
        /// </summary>
        /// <summary>
        /// 请求-响应模板：RequestId 只在此处生成一次并回填到请求 DTO，
        /// 同时作为 pending 字典键；服务端响应原样回显该 Id，路由层据此匹配。
        /// 请求与响应永远不可能因 Id 不一致而失配。
        /// </summary>
        private async Task<TResponse> SendRequestAsync<TRequest, TResponse>(
            ConcurrentDictionary<string, TaskCompletionSource<TResponse>> pending,
            PacketCommand command,
            TRequest request,
            TimeSpan timeout,
            string conflictMessage,
            CancellationToken ct)
            where TRequest : ChatApp.Shared.Protocol.Tcp.ITcpRequest
        {
            var requestId = Guid.NewGuid().ToString("N");
            request.RequestId = requestId;
            var tcs = new TaskCompletionSource<TResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!pending.TryAdd(requestId, tcs))
                throw new InvalidOperationException(conflictMessage);

            Interlocked.Increment(ref _rpcsSent);
            var sw = Stopwatch.StartNew();

            try
            {
                await SendPacketAsync(command, request, ct).ConfigureAwait(false);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linkedCts.CancelAfter(ScaleTimeout(timeout));
                return await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            finally
            {
                // 所有完成路径（成功/超时/取消/断连失败）都记录：超时即延迟证据。
                _rpcLatency.Add(sw.Elapsed);
                pending.TryRemove(requestId, out _);
            }
        }

        /// <summary>规整 presence 用户 Id 列表：过滤无效值、去重、截断到上限。</summary>
        private static long[] NormalizePresenceIds(IReadOnlyList<long> userIds)
            => userIds.Where(static id => id > 0).Distinct().Take(MaxPresenceIdsPerRequest).ToArray();

        /// <summary>将所有 pending 请求以异常失败并清空字典（连接断开时批量回收）。</summary>
        private static bool FailAll<T>(ConcurrentDictionary<string, TaskCompletionSource<T>> dict, Exception ex)
        {
            var failedAny = false;
            foreach (var pair in dict)
            {
                if (!dict.TryRemove(pair.Key, out var tcs))
                    continue;
                tcs.TrySetException(ex);
                failedAny = true;
            }
            return failedAny;
        }

        private void FailPendingRequests(Exception ex)
        {
            FailAll(_listPending, ex);
            FailAll(_relationshipListPending, ex);
            FailAll(_callCommandPending, ex);
            FailAll(_prefsPending, ex);
            FailAll(_recallPending, ex);
            FailAll(_editPending, ex);
            FailAll(_presencePending, ex);
            FailAll(_syncPending, ex);
            FailAll(_historyPending, ex);
            FailAll(_receiptPending, ex);
            FailAll(_markReadPending, ex);
            FailAll(_createGroupPending, ex);
            FailAll(_addGroupMembersPending, ex);
            FailAll(_removeGroupMemberPending, ex);
            FailAll(_leaveGroupPending, ex);
            FailAll(_dissolveGroupPending, ex);
            FailAll(_changeRolePending, ex);
            FailAll(_listGroupMembersPending, ex);
        }


        /// <summary>
        /// SendPacketAsync 方法负责将指定的命令和可选的字符串负载封装成一个消息包，并通过 TCP 客户端发送到服务器。该方法首先将字符串负载转换为 UTF-8 编码的字节数组，然后创建一个 MessagePacket 实例，包含命令和负载数据。接下来，使用 IMessagePacketCodec 将消息包序列化到一个内存缓冲区中，并通过 TCP 客户端的 SendAsync 方法将序列化后的数据发送到服务器。这个方法是 ChatSessionClient 中用于发送各种类型消息（如认证请求、聊天消息、心跳等）的核心方法，通过它可以实现与服务器的通信。
        /// </summary>
        /// <param name="command"></param>
        /// <param name="payload"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async Task SendPacketAsync<T>(PacketCommand command, T? payload, CancellationToken ct, bool priority = false)
        {
            Interlocked.Increment(ref _packetsSent);
            // 热路径：池化出站帧缓冲，JSON 直写同一缓冲，无中间 byte[] 分配，
            // 传输层接管所有权发送完成后归还 ArrayPool，无 ToArray 复制。
            var frameWriter = new PooledBufferWriter(MessagePacket.HeaderSize + 64);
            try
            {
                // 预留 10 字节帧头：先写 magic + command，length 待 body 写完后回填。
                var headerSpan = frameWriter.GetSpan(MessagePacket.HeaderSize);
                BinaryPrimitives.WriteUInt32LittleEndian(headerSpan, MessagePacket.MagicNumber);
                BinaryPrimitives.WriteUInt16LittleEndian(headerSpan.Slice(MessagePacket.CommandOffset), (ushort)command);
                BinaryPrimitives.WriteInt32LittleEndian(headerSpan.Slice(MessagePacket.LengthOffset), 0);
                frameWriter.Advance(MessagePacket.HeaderSize);

                // JSON 直接写入同一缓冲（帧头之后），无 SerializeToUtf8Bytes 的中间 byte[]
                var bodyStart = frameWriter.WrittenCount;
                if (payload is not null)
                    _bodySerializer.Serialize(frameWriter, payload);

                var bodyLen = frameWriter.WrittenCount - bodyStart;
                if (bodyLen > MessagePacket.MaxBodySize)
                    throw new InvalidOperationException($"Body 过大 ({bodyLen} > {MessagePacket.MaxBodySize}): {command}");

                // 回填 body 长度到帧头 offset 6（需可写切片，因 JSON 写入后才知 body 长度）
                BinaryPrimitives.WriteInt32LittleEndian(
                    frameWriter.GetWritableSlice(MessagePacket.LengthOffset, 4), bodyLen);

                // 所有权在此点转移给传输层：标记已转移，finally 不再释放。
                // SendAsync(IMemoryOwner) 契约：无论成功/失败/取消都会 Dispose owner。
                var toSend = frameWriter;
                frameWriter = null!;
                if (priority)
                    await _tcpClient.SendPriorityAsync(toSend, ct).ConfigureAwait(false);
                else
                    await _tcpClient.SendAsync(toSend, ct).ConfigureAwait(false);
            }
            finally
            {
                // 仅在所有权转移前抛出（构造/编码阶段异常）时释放
                frameWriter?.Dispose();
            }
        }


        /// <summary>
        /// RoutePacket 方法负责根据接收到的消息包的命令类型来路由和处理不同类型的消息包。
        /// 它通过检查消息包的 Command 属性来确定消息包的类型，并根据不同的命令类型执行相应的处理逻辑。
        /// </summary>
        /// <param name="packet"></param>
        private void RoutePacket(MessagePacket packet)
        {
            Interlocked.Increment(ref _packetsReceived);
            switch (packet.Command)
            {
                case PacketCommand.ServerHello:
                    HandleServerHello(_bodySerializer.Deserialize<TcpServerHello>(packet.Body));
                    return;
                case PacketCommand.GoAway:
                    HandleGoAway(_bodySerializer.Deserialize<TcpGoAway>(packet.Body));
                    return;
                case PacketCommand.ResumeResponse:
                    HandleResumeResponse(_bodySerializer.Deserialize<TcpResumeResponse>(packet.Body));
                    return;
                case PacketCommand.AuthenticationResponse:
                    var response = _bodySerializer.Deserialize<AuthResponseDto>(packet.Body);
                    HandleAuthResponse(response);
                    return;
                case PacketCommand.ChatMessage:
                    var message = _bodySerializer.Deserialize<ChatMessageDto>(packet.Body);
                    if (message is not null)
                        ChatMessageReceived?.Invoke(this, message);
                    return;
                case PacketCommand.ConversationListPage:
                    var listPage = _bodySerializer.Deserialize<ConversationListResponseDto>(packet.Body);
                    if (listPage is not null
                        && !string.IsNullOrWhiteSpace(listPage.RequestId)
                        && _listPending.TryRemove(listPage.RequestId, out var listTcs))
                    {
                        listTcs.TrySetResult(listPage);
                    }
                    return;
                case PacketCommand.RelationshipListResponse:
                    var relationshipPage = _bodySerializer.Deserialize<RelationshipListResponseDto>(packet.Body);
                    if (relationshipPage is not null
                        && !string.IsNullOrWhiteSpace(relationshipPage.RequestId)
                        && _relationshipListPending.TryRemove(relationshipPage.RequestId, out var relationshipTcs))
                    {
                        relationshipTcs.TrySetResult(relationshipPage);
                    }
                    return;
                case PacketCommand.ConversationSetPrefsResponse:
                    var prefs = _bodySerializer.Deserialize<ConversationSetPrefsResponseDto>(packet.Body);
                    if (prefs is not null
                        && !string.IsNullOrWhiteSpace(prefs.RequestId)
                        && _prefsPending.TryRemove(prefs.RequestId, out var prefsTcs))
                    {
                        prefsTcs.TrySetResult(prefs);
                    }
                    return;
                case PacketCommand.MessageRecallAck:
                    var recallAck = _bodySerializer.Deserialize<MessageRecallAcknowledgementDto>(packet.Body);
                    if (recallAck is not null
                        && !string.IsNullOrWhiteSpace(recallAck.RequestId)
                        && _recallPending.TryRemove(recallAck.RequestId, out var recallTcs))
                    {
                        recallTcs.TrySetResult(recallAck);
                    }
                    return;
                case PacketCommand.MessageRecalled:
                    var recalled = _bodySerializer.Deserialize<MessageRecalledUpdateDto>(packet.Body);
                    if (recalled is not null)
                        MessageRecalled?.Invoke(this, recalled);
                    return;
                case PacketCommand.MessageEditAck:
                    var editAck = _bodySerializer.Deserialize<MessageEditAcknowledgementDto>(packet.Body);
                    if (editAck is not null
                        && !string.IsNullOrWhiteSpace(editAck.RequestId)
                        && _editPending.TryRemove(editAck.RequestId, out var editTcs))
                    {
                        editTcs.TrySetResult(editAck);
                    }
                    return;
                case PacketCommand.MessageEdited:
                    var edited = _bodySerializer.Deserialize<MessageEditedUpdateDto>(packet.Body);
                    if (edited is not null)
                        MessageEdited?.Invoke(this, edited);
                    return;
                case PacketCommand.TypingUpdate:
                    var typing = _bodySerializer.Deserialize<TypingUpdateDto>(packet.Body);
                    if (typing is not null)
                        TypingUpdated?.Invoke(this, typing);
                    return;
                case PacketCommand.PresenceSnapshot:
                    var presenceSnap = _bodySerializer.Deserialize<PresenceSnapshotResponseDto>(packet.Body);
                    if (presenceSnap is not null
                        && !string.IsNullOrWhiteSpace(presenceSnap.RequestId)
                        && _presencePending.TryRemove(presenceSnap.RequestId, out var presenceTcs))
                    {
                        presenceTcs.TrySetResult(presenceSnap);
                    }
                    return;
                case PacketCommand.PresenceChanged:
                    var presenceChanged = _bodySerializer.Deserialize<PresenceChangedDto>(packet.Body);
                    if (presenceChanged is not null)
                        PresenceChanged?.Invoke(this, presenceChanged);
                    return;
                case PacketCommand.SyncBootstrapResponse:
                    var sync = _bodySerializer.Deserialize<SyncBootstrapResponseDto>(packet.Body);
                    if (sync is null)
                        throw new InvalidDataException("SyncBootstrapResponse 反序列化结果为空。");
                    if (string.IsNullOrWhiteSpace(sync.RequestId))
                        throw new InvalidDataException("SyncBootstrapResponse 缺少 RequestId。");
                    if (_syncPending.TryRemove(sync.RequestId, out var syncTcs))
                    {
                        syncTcs.TrySetResult(sync);
                    }
                    return;
                case PacketCommand.ConversationChanged:
                    var changed = _bodySerializer.Deserialize<ConversationChangedDto>(packet.Body);
                    if (changed is not null)
                        ConversationChanged?.Invoke(this, changed);
                    return;
                case PacketCommand.Heartbeat:
                    return;
                case PacketCommand.HeartbeatAcknowledgement:
                    Interlocked.Exchange(ref _lastHeartbeatAckTicks, DateTime.UtcNow.Ticks);
                    var sentTicks = Interlocked.Exchange(ref _lastHeartbeatSentTicks, 0);
                    if (sentTicks > 0)
                        _heartbeatRtt.Add(TimeSpan.FromTicks(DateTime.UtcNow.Ticks - sentTicks));
                    return;
                case PacketCommand.MessageAcknowledgement:
                    var ack = _bodySerializer.Deserialize<MessageAcknowledgementDto>(packet.Body);
                    if (ack is not null)
                        MessageAcknowledged?.Invoke(this, ack);
                    return;
                case PacketCommand.MessageReceipt:
                    var receipt = _bodySerializer.Deserialize<MessageReceiptDto>(packet.Body);
                    if (receipt is not null)
                        MessageReceiptReceived?.Invoke(this, receipt);
                    return;
                case PacketCommand.MessageReceiptAcknowledgement:
                    var receiptAck = _bodySerializer.Deserialize<MessageReceiptAckDto>(packet.Body);
                    if (receiptAck is not null
                        && !string.IsNullOrWhiteSpace(receiptAck.RequestId)
                        && _receiptPending.TryRemove(receiptAck.RequestId, out var receiptTcs))
                    {
                        receiptTcs.TrySetResult(receiptAck);
                    }
                    return;
                case PacketCommand.MessageReceiptUpdated:
                    var receiptUpdated = _bodySerializer.Deserialize<MessageReceiptUpdatedDto>(packet.Body);
                    if (receiptUpdated is not null)
                        MessageReceiptUpdated?.Invoke(this, receiptUpdated);
                    return;
                case PacketCommand.MessageHistoryPage:
                    var historyPage = _bodySerializer.Deserialize<MessageHistoryPageDto>(packet.Body);
                    if (historyPage is null)
                        throw new InvalidDataException("MessageHistoryPage 反序列化结果为空。");
                    if (string.IsNullOrWhiteSpace(historyPage.RequestId))
                        throw new InvalidDataException("MessageHistoryPage 缺少 RequestId。");
                    if (_historyPending.TryRemove(historyPage.RequestId, out var historyTcs))
                    {
                        historyTcs.TrySetResult(historyPage);
                    }
                    MessageHistoryPageReceived?.Invoke(this, historyPage);
                    return;
                case PacketCommand.ConversationMarkReadResponse:
                    var markReadResp = _bodySerializer.Deserialize<ConversationMarkReadResponseDto>(packet.Body);
                    if (markReadResp is not null
                        && !string.IsNullOrWhiteSpace(markReadResp.RequestId)
                        && _markReadPending.TryRemove(markReadResp.RequestId, out var markReadTcs))
                    {
                        markReadTcs.TrySetResult(markReadResp);
                    }
                    if (markReadResp is not null)
                        ConversationMarkReadResponse?.Invoke(this, markReadResp);
                    return;
                case PacketCommand.UnreadCountChanged:
                    var unreadChanged = _bodySerializer.Deserialize<UnreadCountChangedDto>(packet.Body);
                    if (unreadChanged is not null)
                        UnreadCountChanged?.Invoke(this, unreadChanged);
                    return;
                case PacketCommand.CallCommandResponse:
                    var callResponse = _bodySerializer.Deserialize<CallCommandResponseDto>(packet.Body);
                    if (callResponse is not null
                        && !string.IsNullOrWhiteSpace(callResponse.RequestId)
                        && _callCommandPending.TryRemove(callResponse.RequestId, out var callTcs))
                    {
                        callTcs.TrySetResult(callResponse);
                    }
                    return;
                case PacketCommand.CallSignal:
                    var callSignal = _bodySerializer.Deserialize<CallSignalDto>(packet.Body);
                    if (callSignal is not null)
                        CallSignalReceived?.Invoke(this, callSignal);
                    return;
                case PacketCommand.CreateGroupResponse:
                    var createGroup = _bodySerializer.Deserialize<CreateGroupResponseDto>(packet.Body);
                    if (createGroup is not null
                        && !string.IsNullOrWhiteSpace(createGroup.RequestId)
                        && _createGroupPending.TryRemove(createGroup.RequestId, out var createGroupTcs))
                    {
                        createGroupTcs.TrySetResult(createGroup);
                    }
                    return;
                case PacketCommand.AddGroupMembersResponse:
                    var addMembers = _bodySerializer.Deserialize<AddGroupMembersResponseDto>(packet.Body);
                    if (addMembers is not null
                        && !string.IsNullOrWhiteSpace(addMembers.RequestId)
                        && _addGroupMembersPending.TryRemove(addMembers.RequestId, out var addMembersTcs))
                    {
                        addMembersTcs.TrySetResult(addMembers);
                    }
                    return;
                case PacketCommand.RemoveGroupMemberResponse:
                    var removeMember = _bodySerializer.Deserialize<RemoveGroupMemberResponseDto>(packet.Body);
                    if (removeMember is not null
                        && !string.IsNullOrWhiteSpace(removeMember.RequestId)
                        && _removeGroupMemberPending.TryRemove(removeMember.RequestId, out var removeMemberTcs))
                    {
                        removeMemberTcs.TrySetResult(removeMember);
                    }
                    return;
                case PacketCommand.LeaveGroupResponse:
                    var leaveGroup = _bodySerializer.Deserialize<LeaveGroupResponseDto>(packet.Body);
                    if (leaveGroup is not null
                        && !string.IsNullOrWhiteSpace(leaveGroup.RequestId)
                        && _leaveGroupPending.TryRemove(leaveGroup.RequestId, out var leaveGroupTcs))
                    {
                        leaveGroupTcs.TrySetResult(leaveGroup);
                    }
                    return;
                case PacketCommand.DissolveGroupResponse:
                    var dissolveGroup = _bodySerializer.Deserialize<DissolveGroupResponseDto>(packet.Body);
                    if (dissolveGroup is not null
                        && !string.IsNullOrWhiteSpace(dissolveGroup.RequestId)
                        && _dissolveGroupPending.TryRemove(dissolveGroup.RequestId, out var dissolveGroupTcs))
                    {
                        dissolveGroupTcs.TrySetResult(dissolveGroup);
                    }
                    return;
                case PacketCommand.ChangeMemberRoleResponse:
                    var changeRole = _bodySerializer.Deserialize<ChangeMemberRoleResponseDto>(packet.Body);
                    if (changeRole is not null
                        && !string.IsNullOrWhiteSpace(changeRole.RequestId)
                        && _changeRolePending.TryRemove(changeRole.RequestId, out var changeRoleTcs))
                    {
                        changeRoleTcs.TrySetResult(changeRole);
                    }
                    return;
                case PacketCommand.ListGroupMembersResponse:
                    var listMembers = _bodySerializer.Deserialize<ListGroupMembersResponseDto>(packet.Body);
                    if (listMembers is not null
                        && !string.IsNullOrWhiteSpace(listMembers.RequestId)
                        && _listGroupMembersPending.TryRemove(listMembers.RequestId, out var listMembersTcs))
                    {
                        listMembersTcs.TrySetResult(listMembers);
                    }
                    return;
                case PacketCommand.MemberJoined:
                    var memberJoined = _bodySerializer.Deserialize<MemberJoinedUpdateDto>(packet.Body);
                    if (memberJoined is not null)
                        GroupMemberJoined?.Invoke(this, memberJoined);
                    return;
                case PacketCommand.MemberLeft:
                    var memberLeft = _bodySerializer.Deserialize<MemberLeftUpdateDto>(packet.Body);
                    if (memberLeft is not null)
                        GroupMemberLeft?.Invoke(this, memberLeft);
                    return;
                case PacketCommand.MemberRemoved:
                    var memberRemoved = _bodySerializer.Deserialize<MemberRemovedUpdateDto>(packet.Body);
                    if (memberRemoved is not null)
                        GroupMemberRemoved?.Invoke(this, memberRemoved);
                    return;
                case PacketCommand.RoleChanged:
                    var roleChanged = _bodySerializer.Deserialize<RoleChangedUpdateDto>(packet.Body);
                    if (roleChanged is not null)
                        GroupRoleChanged?.Invoke(this, roleChanged);
                    return;
                case PacketCommand.MembersAddedUpdate:
                    var membersAdded = _bodySerializer.Deserialize<MembersAddedUpdateDto>(packet.Body);
                    if (membersAdded is not null)
                        GroupMembersAdded?.Invoke(this, membersAdded);
                    return;
                case PacketCommand.ConversationDissolvedUpdate:
                    var dissolved = _bodySerializer.Deserialize<ConversationDissolvedUpdateDto>(packet.Body);
                    if (dissolved is not null)
                        GroupConversationDissolved?.Invoke(this, dissolved);
                    return;
                case PacketCommand.Error:
                    HandleErrorCommand(packet.Body);
                    return;
            }
        }

        private void HandleServerHello(TcpServerHello? hello)
        {
            var generation = Volatile.Read(ref _connectionGeneration);
            var state = Volatile.Read(ref _handshakeState);
            // 只接受当前 ConnectAsync 发起且尚未完成的握手响应；迟到或重复响应不得污染新连接。
            if (state is null ||
                state.Generation != generation ||
                Volatile.Read(ref _helloSentGeneration) != generation)
            {
                return;
            }

            if (hello is null ||
                hello.ProtocolVersion != TcpFrameConstants.CurrentProtocolVersion ||
                !string.Equals(hello.PayloadFormat, "json", StringComparison.Ordinal) ||
                hello.MaxPayloadBytes <= 0 ||
                (hello.FeatureBits & ~(uint)AdvertisedFeatures) != 0)
            {
                var error = new ProtocolErrorDto
                {
                    Command = PacketCommand.ServerHello,
                    ErrorCode = TcpProtocolErrorCode.UnsupportedVersion.ToString(),
                    ErrorMessage = "服务器返回了不受支持的 TCP 握手参数",
                    IsFatal = true
                };
                ProtocolError?.Invoke(this, error);
                state.Tcs.TrySetException(new ProtocolRequestException(error));
                _ = DisconnectAsync(error.ErrorMessage, CancellationToken.None);
                return;
            }

            Volatile.Write(ref _negotiatedProtocolVersion, hello.ProtocolVersion);
            Volatile.Write(ref _negotiatedFeatureBits, hello.FeatureBits);
            Volatile.Write(ref _serverHeartbeatIntervalMs, hello.HeartbeatIntervalMs);
            Volatile.Write(ref _serverMaxPayloadBytes, hello.MaxPayloadBytes);
            Volatile.Write(ref _handshakeGeneration, generation);
            state.Tcs.TrySetResult(hello);
        }

        private void HandleGoAway(TcpGoAway? goAway)
        {
            var reason = string.IsNullOrWhiteSpace(goAway?.Reason)
                ? "服务器正在排空连接"
                : $"服务器正在排空连接: {goAway.Reason}";
            var error = new ProtocolErrorDto
            {
                Command = PacketCommand.GoAway,
                ErrorCode = TcpProtocolErrorCode.Shutdown.ToString(),
                ErrorMessage = reason,
                IsFatal = false,
                RetryAfterMs = goAway?.RetryAfterMs
            };
            ProtocolError?.Invoke(this, error);

            var authState = Volatile.Read(ref _authState);
            authState?.Tcs.TrySetException(new IOException(reason));
            _ = DisconnectAsync(reason, CancellationToken.None);
        }

        private void HandleResumeResponse(TcpResumeResponse? response)
        {
            // 本客户端尚未声明 SessionResume，也从未发送 ResumeRequest。
            // 任何 ResumeResponse 都是未请求的状态推进尝试，必须按协议违规关闭连接。
            var error = new ProtocolErrorDto
            {
                Command = PacketCommand.ResumeResponse,
                ErrorCode = TcpProtocolErrorCode.ProtocolViolation.ToString(),
                ErrorMessage = "收到未请求的 ResumeResponse",
                IsFatal = true,
                RetryAfterMs = response?.RetryAfterMs
            };
            ProtocolError?.Invoke(this, error);

            var exception = new ProtocolRequestException(error);
            Volatile.Read(ref _handshakeState)?.Tcs.TrySetException(exception);
            Volatile.Read(ref _authState)?.Tcs.TrySetException(exception);
            _ = DisconnectAsync(error.ErrorMessage, CancellationToken.None);
        }

        /// <summary>
        /// 处理服务器 Error 命令：优先按共享 ProtocolErrorFrame 反序列化，
        /// 兼容旧格式（ErrorResponseDto / 裸 UTF-8 文本）。
        /// 有 RequestId 时精确完成对应请求；canonical 错误按 OriginCommand 结束同类在途请求；
        /// IsFatal 才触发 AuthenticationFailed，普通业务错误仅发布 ProtocolError 事件。
        /// </summary>
        private void HandleErrorCommand(ReadOnlySequence<byte> body)
        {
            ProtocolErrorDto? error = null;

            // Gateway 当前格式。旧错误 DTO 即使字段完全不匹配也可能被 STJ 构造为空对象，
            // 因此必须先识别 canonical frame，再进入兼容路径。
            TcpProtocolErrorFrame? frame = null;
            try { frame = _bodySerializer.Deserialize<TcpProtocolErrorFrame>(body); } catch { /* 非结构化错误体 */ }
            if (frame is not null && frame.Code != TcpProtocolErrorCode.None)
            {
                error = new ProtocolErrorDto
                {
                    RequestId = null,
                    Command = frame.OriginCommand is { } origin
                        ? (PacketCommand)origin
                        : null,
                    ErrorCode = frame.Code.ToString(),
                    ErrorMessage = frame.Message,
                    IsFatal = frame.Fatal || TcpProtocolErrorCodeExtensions.IsFatal(frame.Code),
                    RetryAfterMs = frame.RetryAfterMs
                };
            }

            if (error is null)
            {
                try { error = _bodySerializer.Deserialize<ProtocolErrorDto>(body); } catch { /* 非结构化错误体 */ }
                if (error is not null &&
                    string.IsNullOrWhiteSpace(error.RequestId) &&
                    error.Command is null &&
                    string.IsNullOrWhiteSpace(error.ErrorCode) &&
                    string.IsNullOrWhiteSpace(error.ErrorMessage) &&
                    !error.IsFatal &&
                    !error.RetryAfterMs.HasValue)
                {
                    error = null;
                }
            }

            if (error is null)
            {
                // 兼容旧格式：ErrorResponseDto 或裸文本。
                ErrorResponseDto? legacy = null;
                try { legacy = _bodySerializer.Deserialize<ErrorResponseDto>(body); } catch { /* 忽略 */ }
                error = new ProtocolErrorDto
                {
                    RequestId = null,
                    Command = PacketCommand.Error,
                    ErrorCode = legacy is null ? "UNKNOWN" : legacy.StatusCode.ToString(),
                    ErrorMessage = legacy is null || string.IsNullOrWhiteSpace(legacy.ErrorMessage)
                        ? (body.IsSingleSegment ? Encoding.UTF8.GetString(body.FirstSpan) : Encoding.UTF8.GetString(body.ToArray()))
                        : legacy.ErrorMessage,
                    IsFatal = false
                };
            }

            // 关联在途请求：完成对应 TCS，调用方自行处理，不弹全局错误。
            if (FailByRequestId(error) || FailByOriginCommand(error))
                return;

            // 鉴权阶段：仅连接级/致命错误显式结束鉴权。
            // 普通业务错误与鉴权请求无关，只广播 ProtocolError，继续等待鉴权响应（超时兜底）。
            var authState = Volatile.Read(ref _authState);
            if (authState is not null && error.IsFatal)
            {
                authState.Tcs.TrySetException(new ProtocolRequestException(error));
                AuthenticationFailed?.Invoke(this, error.ErrorMessage ?? "鉴权失败");
                return;
            }

            ProtocolError?.Invoke(this, error);

            // 致命错误：与鉴权失败同等级，停止心跳与自动重连。
            if (error.IsFatal)
                AuthenticationFailed?.Invoke(this, error.ErrorMessage ?? "服务器致命错误");
        }

        /// <summary>按 RequestId 在全部在途请求中查找并完成对应 TCS。</summary>
        private bool FailByRequestId(ProtocolErrorDto error)
        {
            if (string.IsNullOrWhiteSpace(error.RequestId))
                return false;
            var requestId = error.RequestId;

            if (_listPending.TryRemove(requestId, out var listTcs))
            {
                listTcs.TrySetException(new ProtocolRequestException(error));
                return true;
            }
            if (_relationshipListPending.TryRemove(requestId, out var relationshipTcs))
            {
                relationshipTcs.TrySetException(new ProtocolRequestException(error));
                return true;
            }
            if (_prefsPending.TryRemove(requestId, out var prefsTcs))
            {
                prefsTcs.TrySetException(new ProtocolRequestException(error));
                return true;
            }
            if (_recallPending.TryRemove(requestId, out var recallTcs))
            {
                recallTcs.TrySetException(new ProtocolRequestException(error));
                return true;
            }
            if (_editPending.TryRemove(requestId, out var editTcs))
            {
                editTcs.TrySetException(new ProtocolRequestException(error));
                return true;
            }
            if (_syncPending.TryRemove(requestId, out var syncTcs))
            {
                syncTcs.TrySetException(new ProtocolRequestException(error));
                return true;
            }
            if (_presencePending.TryRemove(requestId, out var presenceTcs))
            {
                presenceTcs.TrySetException(new ProtocolRequestException(error));
                return true;
            }
            if (_historyPending.TryRemove(requestId, out var historyTcs))
            {
                historyTcs.TrySetException(new ProtocolRequestException(error));
                return true;
            }
            if (_receiptPending.TryRemove(requestId, out var receiptTcs))
            {
                receiptTcs.TrySetException(new ProtocolRequestException(error));
                return true;
            }
            if (_markReadPending.TryRemove(requestId, out var markReadTcs))
            {
                markReadTcs.TrySetException(new ProtocolRequestException(error));
                return true;
            }
            if (_callCommandPending.TryRemove(requestId, out var callTcs))
            {
                callTcs.TrySetException(new ProtocolRequestException(error));
                return true;
            }
            return false;
        }

        /// <summary>
        /// canonical Error 不携带 RequestId。按 OriginCommand 失败该命令的全部在途请求，
        /// 避免能力拒绝、限流等明确错误退化为客户端超时。
        /// </summary>
        private bool FailByOriginCommand(ProtocolErrorDto error)
        {
            if (error.Command is not { } command)
                return false;

            var exception = new ProtocolRequestException(error);
            return command switch
            {
                PacketCommand.ConversationListRequest => FailAll(_listPending, exception),
                PacketCommand.RelationshipListRequest => FailAll(_relationshipListPending, exception),
                PacketCommand.CallCommandRequest => FailAll(_callCommandPending, exception),
                PacketCommand.ConversationSetPrefsRequest => FailAll(_prefsPending, exception),
                PacketCommand.MessageRecallRequest => FailAll(_recallPending, exception),
                PacketCommand.MessageEditRequest => FailAll(_editPending, exception),
                PacketCommand.SyncBootstrapRequest => FailAll(_syncPending, exception),
                PacketCommand.PresenceQuery => FailAll(_presencePending, exception),
                PacketCommand.MessageHistoryRequest => FailAll(_historyPending, exception),
                PacketCommand.MessageReceipt => FailAll(_receiptPending, exception),
                PacketCommand.ConversationMarkReadRequest => FailAll(_markReadPending, exception),
                PacketCommand.CreateGroupRequest => FailAll(_createGroupPending, exception),
                PacketCommand.AddGroupMembersRequest => FailAll(_addGroupMembersPending, exception),
                PacketCommand.RemoveGroupMemberRequest => FailAll(_removeGroupMemberPending, exception),
                PacketCommand.LeaveGroupRequest => FailAll(_leaveGroupPending, exception),
                PacketCommand.ChangeMemberRoleRequest => FailAll(_changeRolePending, exception),
                PacketCommand.ListGroupMembersRequest => FailAll(_listGroupMembersPending, exception),
                _ => false
            };
        }

        /// <summary>
        /// HandleAuthResponse 方法负责处理服务器返回的认证响应消息。
        /// 按代际关联在途鉴权请求：迟到/串线的响应不得更新状态或触发事件。
        /// </summary>
        /// <param name="response"></param>
        private void HandleAuthResponse(AuthResponseDto? response)
        {
            var state = Volatile.Read(ref _authState);
            // 无在途鉴权（已完成/超时/未发起）或代际不符（旧连接迟到响应）：整体忽略。
            if (state is null || state.Generation != Volatile.Read(ref _connectionGeneration))
                return;

            if (response is null)
            {
                state.Tcs.TrySetException(new InvalidOperationException("服务器返回的认证响应无效"));
                AuthenticationFailed?.Invoke(this, "服务器返回的认证响应无效");
                return;
            }

            if (response.Success is true && response.UserId.HasValue)
            {
                // 首次完成本次 TCS 才允许推进状态；重复响应不重复触发。
                if (!state.Tcs.TrySetResult(response))
                    return;

                IsAuthenticated = true;
                CurrentUserId = response.UserId.Value;
                Interlocked.Exchange(ref _lastHeartbeatAckTicks, DateTime.UtcNow.Ticks);
                Authenticated?.Invoke(this, CurrentUserId);
            }
            else
            {
                IsAuthenticated = false;
                state.Tcs.TrySetException(new UnauthorizedAccessException(response.ErrorMessage ?? "认证失败，未知错误"));
                AuthenticationFailed?.Invoke(this, response.ErrorMessage ?? "认证失败，未知错误");
            }
        }

        /// <summary>
        /// 鉴权请求状态：TCS + 发起时的连接代际。
        /// 结果区分：
        /// - 外部取消：OperationCanceledException（原样重抛，不触发事件）
        /// - 内部超时：TimeoutException（触发 AuthenticationFailed）
        /// - 连接断开：IOException（断线处理器完成，仅同代际）
        /// - 服务端拒绝：UnauthorizedAccessException（HandleAuthResponse）
        /// - 协议错误：ProtocolRequestException（仅致命错误完成鉴权）
        /// </summary>
        private sealed class AuthRequestState
        {
            public required long Generation { get; init; }
            public required TaskCompletionSource<AuthResponseDto> Tcs { get; init; }
        }

        /// <summary>一次 ConnectAsync 对应的 ServerHello 等待状态。</summary>
        private sealed class HandshakeState
        {
            public required long Generation { get; init; }
            public required TaskCompletionSource<TcpServerHello> Tcs { get; init; }
        }

        public string Name => "network";

        public IReadOnlyDictionary<string, long> Counters => new Dictionary<string, long>
        {
            ["packets_sent"] = Volatile.Read(ref _packetsSent),
            ["packets_received"] = Volatile.Read(ref _packetsReceived),
            ["heartbeats_sent"] = Volatile.Read(ref _heartbeatsSent),
            ["rpc_requests"] = Volatile.Read(ref _rpcsSent),
            ["route_failures"] = Volatile.Read(ref _routeFailures),
            ["history_conversation_ids_inferred"] = Volatile.Read(ref _historyConversationIdsInferred),
            ["history_conversation_mismatches"] = Volatile.Read(ref _historyConversationMismatches),
            ["disconnects"] = Volatile.Read(ref _disconnects),
            ["pending_requests"] = PendingRequestCount
        };

        public IReadOnlyDictionary<string, HistogramSnapshot> Histograms =>
            new Dictionary<string, HistogramSnapshot>
            {
                ["heartbeat_rtt_ms"] = _heartbeatRtt.Snapshot(),
                ["rpc_latency_ms"] = _rpcLatency.Snapshot()
            };
    }
}
