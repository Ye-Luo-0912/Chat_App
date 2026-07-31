using Core.Buffers;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using System.Diagnostics;

namespace Core.Services
{
    public class ChatSessionClient : IChatSessionClient
    {
        private readonly ITcpClient _tcpClient;
        private readonly IMessagePacketCodec _codec;
        private TaskCompletionSource<bool>? _authTcs;
        private readonly IPacketBodySerializer _bodySerializer;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ConversationListResponseDto>> _listPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ConversationSetPrefsResponseDto>> _prefsPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageRecallAcknowledgementDto>> _recallPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageEditAcknowledgementDto>> _editPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<SyncBootstrapResponseDto>> _syncPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<PresenceSnapshotResponseDto>> _presencePending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageHistoryPageDto>> _historyPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageReceiptAckDto>> _receiptPending = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ConversationMarkReadResponseDto>> _markReadPending = new(StringComparer.Ordinal);

        private long _lastHeartbeatAckTicks;
        private const int HeartbeatTimeoutSeconds = 60;

        public bool IsConnected => _tcpClient.IsConnected;

        public bool IsAuthenticated { get; private set; }

        public long CurrentUserId { get; private set; }

        public event EventHandler? Connected;
        public event EventHandler<long>? Authenticated;
        public event EventHandler<string>? AuthenticationFailed;
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

        public ChatSessionClient(ITcpClient tcpClient, IMessagePacketCodec codec, IPacketBodySerializer bodySerializer)
        {
            _tcpClient = tcpClient;
            _codec = codec;
            _bodySerializer = bodySerializer;

            _tcpClient.ConnectionStatusChanged += OnConnectionStatusChanged;
            _tcpClient.OnDataChunkReceived += OnDataChunkReceived;
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
                    Debug.WriteLine($"路由帧失败 Command={packet.Command}, Error={ex.Message}");
                }
            }
        }

        /// <summary>
        /// 当 TCP 客户端的连接状态发生改变时触发该事件处理程序，负责根据新的连接状态来更新 ChatSessionClient 的状态，并触发相应的事件通知外部订阅者。
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="status"></param>
        private void OnConnectionStatusChanged(object? sender, string status)
        {
            if (status == "Connected")
            {
                Connected?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                IsAuthenticated = false;
                Interlocked.Exchange(ref _lastHeartbeatAckTicks, 0);
                FailPendingRequests(new IOException(status));
                ConnectionClosed?.Invoke(this, status);
            }
        }

        public async Task AuthenticateAsync(string accessToken, long userId, string? sessionId, ulong? deviceIdHash, CancellationToken ct = default)
        {
            if (!IsConnected)
                throw new InvalidOperationException("TCP 尚未连接！");

            _authTcs = new TaskCompletionSource<bool>();

            var authRequest = new AuthRequestDto
            {
                AccessToken = accessToken,
                UserId = userId,
                SessionId = sessionId,
                DeviceIdHash = deviceIdHash
            };

            await SendPacketAsync(PacketCommand.AuthRequest, authRequest, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

            try
            {
                await _authTcs.Task.WaitAsync(timeoutCts.Token);
            }
            catch (TimeoutException)
            {
                AuthenticationFailed?.Invoke(this, "鉴权超时，服务器未响应");
                throw;
            }
        }

        public async Task ConnectAsync(ServerEndpoint endpoint, CancellationToken ct = default)
        {
            // 在连接到服务器之前，重置消息包解码器的状态，以确保之前的任何未完成的消息包都被清除掉，避免对新的连接造成干扰。
            _codec.Reset();
            // 连接到服务器后，ChatSessionClient 将等待服务器发送认证结果消息，以确定认证是否成功，并根据认证结果更新状态和触发相应的事件通知外部订阅者。
            await _tcpClient.ConnectAsync(endpoint, ct);
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
            Interlocked.Exchange(ref _lastHeartbeatAckTicks, 0);
            _tcpClient.Disconnect(reason);
            await Task.CompletedTask; // 占位，保持异步签名
        }

        public void Dispose()
        {
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
            CancellationToken ct = default)
        {
            var hasAttachments = attachmentIds is { Count: > 0 };
            if (string.IsNullOrWhiteSpace(content) && !hasAttachments)
                throw new ArgumentException("消息文本与附件至少需要其一。");

            if (attachmentIds is { Count: > 32 })
                throw new ArgumentException("单条消息最多 32 个附件。");

            if (attachmentIds?.Any(static id =>
                    string.IsNullOrWhiteSpace(id) || id.Length > 64) == true)
            {
                throw new ArgumentException("附件 Id 无效。");
            }

            var hasReply = !string.IsNullOrWhiteSpace(replyToMessageId);
            var hasForward = !string.IsNullOrWhiteSpace(forwardedFromMessageId);
            if (hasReply && hasForward)
                throw new ArgumentException("回复与转发不能同时设置。");

            if (hasReply)
            {
                if (replyToMessageId!.Length > 64)
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
                if (forwardedFromMessageId!.Length > 64)
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
                : (replyToPreview.Length <= 256 ? replyToPreview : replyToPreview[..256]);
            var forwardPreview = string.IsNullOrWhiteSpace(forwardedFromPreview)
                ? null
                : (forwardedFromPreview.Length <= 256
                    ? forwardedFromPreview
                    : forwardedFromPreview[..256]);

            var clientMessageId = string.IsNullOrWhiteSpace(clientMessageIdParam)
                ? Guid.CreateVersion7().ToString("N")
                : clientMessageIdParam;
            var chatPayload = new ChatMessageDto
            {
                MessageId = clientMessageId,
                TargetUserId = targetUserId,
                Content = content,
                SentUtc = DateTime.UtcNow,
                AttachmentIds = hasAttachments ? attachmentIds : null,
                ReplyToMessageId = hasReply ? replyToMessageId : null,
                ReplyToSenderUserId = hasReply ? replyToSenderUserId : null,
                ReplyToPreview = hasReply ? preview : null,
                ForwardedFromMessageId = hasForward ? forwardedFromMessageId : null,
                ForwardedFromSenderUserId = hasForward ? forwardedFromSenderUserId : null,
                ForwardedFromPreview = hasForward ? forwardPreview : null
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

            await SendPacketAsync(PacketCommand.Heartbeat, (object?)null, ct);
        }

        public async Task<ConversationListResponseDto> QueryConversationListAsync(
            int limit = 50,
            bool? beforeIsPinned = null,
            long? beforePinnedAtMs = null,
            long? beforeLastMessageAtMs = null,
            string? beforeConversationId = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<ConversationListResponseDto>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_listPending.TryAdd(requestId, tcs))
                throw new InvalidOperationException("会话列表请求 Id 冲突");

            try
            {
                await SendPacketAsync(
                    PacketCommand.ConversationListRequest,
                    new ConversationListRequestDto
                    {
                        RequestId = requestId,
                        BeforeIsPinned = beforeIsPinned,
                        BeforePinnedAtMs = beforePinnedAtMs,
                        BeforeLastMessageAtMs = beforeLastMessageAtMs,
                        BeforeConversationId = beforeConversationId,
                        Limit = Math.Clamp(limit, 1, 100)
                    },
                    ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _listPending.TryRemove(requestId, out _);
            }
        }

        public async Task<ConversationSetPrefsResponseDto> SetConversationPrefsAsync(
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

            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<ConversationSetPrefsResponseDto>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_prefsPending.TryAdd(requestId, tcs))
                throw new InvalidOperationException("会话偏好请求 Id 冲突");

            try
            {
                await SendPacketAsync(
                    PacketCommand.ConversationSetPrefsRequest,
                    new ConversationSetPrefsRequestDto
                    {
                        RequestId = requestId,
                        ConversationId = conversationId.Trim(),
                        Pinned = pinned,
                        Muted = muted,
                        MutedUntilMs = mutedUntilMs
                    },
                    ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _prefsPending.TryRemove(requestId, out _);
            }
        }

        public async Task<MessageRecallAcknowledgementDto> RecallMessageAsync(
            string messageId,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 64)
                throw new ArgumentException("messageId 无效");

            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<MessageRecallAcknowledgementDto>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_recallPending.TryAdd(requestId, tcs))
                throw new InvalidOperationException("消息撤回请求 Id 冲突");

            try
            {
                await SendPacketAsync(
                    PacketCommand.MessageRecallRequest,
                    new MessageRecallRequestDto
                    {
                        RequestId = requestId,
                        MessageId = messageId.Trim()
                    },
                    ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _recallPending.TryRemove(requestId, out _);
            }
        }

        public async Task<MessageEditAcknowledgementDto> EditMessageAsync(
            string messageId,
            string content,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(messageId) || messageId.Length > 64)
                throw new ArgumentException("messageId 无效");
            ArgumentNullException.ThrowIfNull(content);
            var trimmed = content.Trim();
            if (trimmed.Length == 0)
                throw new ArgumentException("编辑内容不能为空");
            if (trimmed.Length > 4000)
                throw new ArgumentException("编辑内容过长");

            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<MessageEditAcknowledgementDto>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_editPending.TryAdd(requestId, tcs))
                throw new InvalidOperationException("消息编辑请求 Id 冲突");

            try
            {
                await SendPacketAsync(
                    PacketCommand.MessageEditRequest,
                    new MessageEditRequestDto
                    {
                        RequestId = requestId,
                        MessageId = messageId.Trim(),
                        Content = trimmed
                    },
                    ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _editPending.TryRemove(requestId, out _);
            }
        }

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

        public async Task<PresenceSnapshotResponseDto> QueryPresenceAsync(
            IReadOnlyList<long> userIds,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            ArgumentNullException.ThrowIfNull(userIds);

            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<PresenceSnapshotResponseDto>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_presencePending.TryAdd(requestId, tcs))
                throw new InvalidOperationException("在线状态请求 Id 冲突");

            try
            {
                await SendPacketAsync(
                    PacketCommand.PresenceQuery,
                    new PresenceQueryRequestDto
                    {
                        RequestId = requestId,
                        UserIds = userIds
                            .Where(static id => id > 0)
                            .Distinct()
                            .Take(100)
                            .ToArray()
                    },
                    ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _presencePending.TryRemove(requestId, out _);
            }
        }

        public async Task UnwatchPresenceAsync(
            IReadOnlyList<long> userIds,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            ArgumentNullException.ThrowIfNull(userIds);

            var ids = userIds
                .Where(static id => id > 0)
                .Distinct()
                .Take(100)
                .ToArray();
            if (ids.Length == 0)
                return;

            await SendPacketAsync(
                PacketCommand.PresenceUnwatch,
                new PresenceUnwatchRequestDto { UserIds = ids },
                ct);
        }

        public async Task<SyncBootstrapResponseDto> QuerySyncBootstrapAsync(
            int listLimit = 50,
            int historyLimitPerConversation = 20,
            int maxConversationsWithHistory = 10,
            IReadOnlyList<ConversationSyncWatermarkDto>? watermarks = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<SyncBootstrapResponseDto>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_syncPending.TryAdd(requestId, tcs))
                throw new InvalidOperationException("同步引导请求 Id 冲突");

            try
            {
                await SendPacketAsync(
                    PacketCommand.SyncBootstrapRequest,
                    new SyncBootstrapRequestDto
                    {
                        RequestId = requestId,
                        ListLimit = Math.Clamp(listLimit, 1, 100),
                        HistoryLimitPerConversation = Math.Clamp(historyLimitPerConversation, 1, 50),
                        MaxConversationsWithHistory = Math.Clamp(maxConversationsWithHistory, 0, 20),
                        Watermarks = watermarks
                    },
                    ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _syncPending.TryRemove(requestId, out _);
            }
        }

        public async Task<MessageHistoryPageDto> QueryMessageHistoryAsync(
            string conversationId,
            int limit = 50,
            long? beforeReceivedAtMs = null,
            string? beforeMessageId = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("会话 Id 不能为空", nameof(conversationId));

            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<MessageHistoryPageDto>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_historyPending.TryAdd(requestId, tcs))
                throw new InvalidOperationException("历史拉取请求 Id 冲突");

            try
            {
                await SendPacketAsync(
                    PacketCommand.MessageHistoryRequest,
                    new MessageHistoryRequestDto
                    {
                        RequestId = requestId,
                        ConversationId = conversationId.Trim(),
                        BeforeReceivedAtMs = beforeReceivedAtMs,
                        BeforeMessageId = beforeMessageId,
                        Limit = Math.Clamp(limit, 1, 100)
                    },
                    ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _historyPending.TryRemove(requestId, out _);
            }
        }
        public async Task<MessageReceiptAckDto> SendMessageReceiptAsync(
            string conversationId,
            string? lastReadMessageId,
            long? lastReadAtMs,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("会话 Id 不能为空", nameof(conversationId));

            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<MessageReceiptAckDto>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_receiptPending.TryAdd(requestId, tcs))
                throw new InvalidOperationException("已读回执请求 Id 冲突");

            try
            {
                await SendPacketAsync(
                    PacketCommand.MessageReceipt,
                    new MessageReceiptDto
                    {
                        RequestId = requestId,
                        ConversationId = conversationId.Trim(),
                        LastReadMessageId = lastReadMessageId,
                        LastReadAtMs = lastReadAtMs,
                        ReaderUserId = CurrentUserId
                    },
                    ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _receiptPending.TryRemove(requestId, out _);
            }
        }
        public async Task<ConversationMarkReadResponseDto> MarkConversationReadAsync(
            string conversationId,
            string? lastReadMessageId = null,
            long? lastReadAtMs = null,
            CancellationToken ct = default)
        {
            EnsureAuthenticated();
            if (string.IsNullOrWhiteSpace(conversationId))
                throw new ArgumentException("会话 Id 不能为空", nameof(conversationId));

            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<ConversationMarkReadResponseDto>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_markReadPending.TryAdd(requestId, tcs))
                throw new InvalidOperationException("标记已读请求 Id 冲突");

            try
            {
                await SendPacketAsync(
                    PacketCommand.ConversationMarkReadRequest,
                    new ConversationMarkReadRequestDto
                    {
                        RequestId = requestId,
                        ConversationId = conversationId.Trim(),
                        LastReadMessageId = lastReadMessageId,
                        LastReadAtMs = lastReadAtMs
                    },
                    ct);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                return await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            finally
            {
                _markReadPending.TryRemove(requestId, out _);
            }
        }
        private void EnsureAuthenticated()
        {
            if (!IsConnected || !IsAuthenticated)
                throw new InvalidOperationException("TCP 未连接或未鉴权");
        }

        private void FailPendingRequests(Exception ex)
        {
            foreach (var pair in _listPending)
                pair.Value.TrySetException(ex);
            _listPending.Clear();

            foreach (var pair in _prefsPending)
                pair.Value.TrySetException(ex);
            _prefsPending.Clear();

            foreach (var pair in _recallPending)
                pair.Value.TrySetException(ex);
            _recallPending.Clear();

            foreach (var pair in _presencePending)
                pair.Value.TrySetException(ex);
            _presencePending.Clear();

            foreach (var pair in _syncPending)
                pair.Value.TrySetException(ex);
            _syncPending.Clear();

            foreach (var pair in _historyPending)
                pair.Value.TrySetException(ex);
            _historyPending.Clear();

            foreach (var pair in _receiptPending)
                pair.Value.TrySetException(ex);
            _receiptPending.Clear();

            foreach (var pair in _markReadPending)
                pair.Value.TrySetException(ex);
            _markReadPending.Clear();
        }


        /// <summary>
        /// SendPacketAsync 方法负责将指定的命令和可选的字符串负载封装成一个消息包，并通过 TCP 客户端发送到服务器。该方法首先将字符串负载转换为 UTF-8 编码的字节数组，然后创建一个 MessagePacket 实例，包含命令和负载数据。接下来，使用 IMessagePacketCodec 将消息包序列化到一个内存缓冲区中，并通过 TCP 客户端的 SendAsync 方法将序列化后的数据发送到服务器。这个方法是 ChatSessionClient 中用于发送各种类型消息（如认证请求、聊天消息、心跳等）的核心方法，通过它可以实现与服务器的通信。
        /// </summary>
        /// <param name="command"></param>
        /// <param name="payload"></param>
        /// <param name="ct"></param>
        /// <returns></returns>
        private async Task SendPacketAsync<T>(PacketCommand command, T? payload, CancellationToken ct)
        {
            // P0-十 热路径：池化出站帧缓冲，JSON 直写同一缓冲，无中间 byte[] 分配，
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
            switch (packet.Command)
            {
                case PacketCommand.AuthResponse:
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
                    if (sync is not null
                        && !string.IsNullOrWhiteSpace(sync.RequestId)
                        && _syncPending.TryRemove(sync.RequestId, out var syncTcs))
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
                case PacketCommand.HeartbeatAck:
                    Interlocked.Exchange(ref _lastHeartbeatAckTicks, DateTime.UtcNow.Ticks);
                    return;
                case PacketCommand.MessageAck:
                    var ack = _bodySerializer.Deserialize<MessageAcknowledgementDto>(packet.Body);
                    if (ack is not null)
                        MessageAcknowledged?.Invoke(this, ack);
                    return;
                case PacketCommand.MessageReceipt:
                    var receipt = _bodySerializer.Deserialize<MessageReceiptDto>(packet.Body);
                    if (receipt is not null)
                        MessageReceiptReceived?.Invoke(this, receipt);
                    return;
                case PacketCommand.MessageReceiptAck:
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
                    if (historyPage is not null
                        && !string.IsNullOrWhiteSpace(historyPage.RequestId)
                        && _historyPending.TryRemove(historyPage.RequestId, out var historyTcs))
                    {
                        historyTcs.TrySetResult(historyPage);
                    }
                    if (historyPage is not null)
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
                case PacketCommand.Error:
                    var errorMsg = Encoding.UTF8.GetString(packet.Body.ToArray());
                    AuthenticationFailed?.Invoke(this, $"服务器错误：{errorMsg}");
                    return;
            }
        }

        /// <summary>
        /// HandleAuthResponse 方法负责处理服务器返回的认证响应消息，根据响应内容更新 ChatSessionClient 的认证状态，并触发相应的事件通知外部订阅者。如果认证成功，则将 IsAuthenticated 设置为 true，更新 CurrentUserId，并触发 Authenticated 事件；如果认证失败，则将 IsAuthenticated 设置为 false，并触发 AuthenticationFailed 事件，传递错误消息给订阅者。这个方法是 ChatSessionClient 中处理认证结果的核心逻辑，通过它可以根据服务器的响应来确定认证是否成功，并及时通知外部订阅者相关的状态变化和错误信息。
        /// </summary>
        /// <param name="response"></param>
        private void HandleAuthResponse(AuthResponseDto? response)
        {
            // 处理服务器返回的认证响应消息，根据响应内容更新 ChatSessionClient 的认证状态，并触发相应的事件通知外部订阅者。如果认证成功，则将 IsAuthenticated 设置为 true，更新 CurrentUserId，并触发 Authenticated 事件；如果认证失败，则将 IsAuthenticated 设置为 false，并触发 AuthenticationFailed 事件，传递错误消息给订阅者。这个方法是 ChatSessionClient 中处理认证结果的核心逻辑，通过它可以根据服务器的响应来确定认证是否成功，并及时通知外部订阅者相关的状态变化和错误信息。
            if (response is null)
            {
                _authTcs?.TrySetException(new InvalidOperationException("服务器返回的认证响应无效"));
                AuthenticationFailed?.Invoke(this, "服务器返回的认证响应无效");
                return;
            }

            
            if (response.Success is true && response.UserId.HasValue)
            {
                IsAuthenticated = true;
                CurrentUserId = response.UserId.Value;
                Interlocked.Exchange(ref _lastHeartbeatAckTicks, DateTime.UtcNow.Ticks);
                _authTcs?.TrySetResult(true);
                Authenticated?.Invoke(this, CurrentUserId);

            }
            else
            {
                IsAuthenticated = false;
                _authTcs?.TrySetException(new UnauthorizedAccessException(response.ErrorMessage ?? "认证失败，未知错误"));
                AuthenticationFailed?.Invoke(this, response.ErrorMessage ?? "认证失败，未知错误");
            }
        }
    }
}
