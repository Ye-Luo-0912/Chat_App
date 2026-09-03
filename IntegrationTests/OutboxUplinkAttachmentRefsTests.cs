using System.Buffers;
using Chat_App.Infrastructure.Events;
using Chat_App.Infrastructure.Models.Context;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Chat_App.Infrastructure.Networking;
using Chat_App.Infrastructure.Serialization;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Services;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Services;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// VOICE-MSG-2：outbox 上行必须携带本地消息行的附件 ref（含语音元数据），
/// 使网关写入附件注册表、历史重建有元数据来源；纯文本消息零额外查询（Attachments 为 null）。
/// </summary>
public class OutboxUplinkAttachmentRefsTests : IDisposable
{
    private const long OwnerId = 8801;
    private const long PeerId = 9901;
    private const string Conv = "conv-uplink";

    private readonly string _dbPath;
    private readonly IDbContextFactory<ClientDbContext> _factory;
    private readonly DatabaseService _db;

    public OutboxUplinkAttachmentRefsTests()
    {
        _dbPath = Path.Combine(PathTemp(), $"chat_uplink_{Guid.NewGuid():N}.db");
        _factory = new DbContextFactoryStub(_dbPath);
        _db = new DatabaseService(_factory);
        using var ctx = _factory.CreateDbContext();
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try { File.Delete(_dbPath); return; }
            catch (IOException) { Thread.Sleep(50); }
        }
    }

    private static string PathTemp() => Path.GetTempPath();

    [Fact]
    public async Task Outbox_Uplink_Carries_Voice_Metadata_Refs()
    {
        const string clientId = "uplink-voice-1";
        var refs = new[]
        {
            new AttachmentRefDto
            {
                AttachmentId = "att-voice-1",
                FileName = "voice.wav",
                ContentType = "audio/wav",
                SizeBytes = 112_044,
                Status = 1,
                IsVoice = true,
                VoiceCodec = "pcm",
                VoiceContainer = "wav",
                VoiceDurationMs = 3_500,
                VoiceSampleRateHz = 16_000,
                VoiceChannels = 1
            }
        };
        var refsJson = AttachmentJson.Serialize(refs);

        var localMessage = new LocalMessage
        {
            OwnerUserId = OwnerId,
            ClientMessageId = clientId,
            ConversationId = Conv,
            SenderUserId = OwnerId,
            ReceiverUserId = PeerId,
            Content = "语音上行",
            ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ChangedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            AttachmentsJson = refsJson
        };
        var outbox = NewOutbox(clientId, withIds: true);

        var (recorder, processor) = await StartProcessorAsync(localMessage, outbox);

        // 入队后回查接口直接可用（outbox 发送侧依赖同一查询）。
        Assert.Equal(refsJson, await _db.GetLocalMessageAttachmentsJsonAsync(OwnerId, clientId));
        try
        {
            await WaitUntilAsync(async () =>
                (await _db.GetOutboxByClientIdAsync(OwnerId, clientId))?.Status == OutboxStatus.Sent,
                TimeSpan.FromSeconds(20));
        }
        finally
        {
            processor.Dispose();
        }

        var sent = recorder.SentChatMessages.Single(m => m.MessageId == clientId);
        Assert.NotNull(sent.Attachments);
        var voice = Assert.Single(sent.Attachments);
        Assert.True(voice.IsVoice);
        Assert.Equal("att-voice-1", voice.AttachmentId);
        Assert.Equal("pcm", voice.VoiceCodec);
        Assert.Equal("wav", voice.VoiceContainer);
        Assert.Equal(3_500, voice.VoiceDurationMs);
        Assert.Equal(16_000, voice.VoiceSampleRateHz);
        Assert.Equal((short)1, voice.VoiceChannels);
    }

    [Fact]
    public async Task Outbox_TextOnly_Uplink_Has_No_Attachments()
    {
        const string clientId = "uplink-text-1";
        var localMessage = new LocalMessage
        {
            OwnerUserId = OwnerId,
            ClientMessageId = clientId,
            ConversationId = Conv,
            SenderUserId = OwnerId,
            ReceiverUserId = PeerId,
            Content = "纯文本",
            ReceivedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ChangedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            AttachmentsJson = null
        };
        var outbox = NewOutbox(clientId, withIds: false);

        var (recorder, processor) = await StartProcessorAsync(localMessage, outbox);
        try
        {
            await WaitUntilAsync(async () =>
                (await _db.GetOutboxByClientIdAsync(OwnerId, clientId))?.Status == OutboxStatus.Sent,
                TimeSpan.FromSeconds(20));
        }
        finally
        {
            processor.Dispose();
        }

        var sent = recorder.SentChatMessages.Single(m => m.MessageId == clientId);
        // 纯文本消息：wire 上不出现附件字段（同时验证未触发回查的路径形状不变）。
        Assert.True(sent.Attachments is null or { Count: 0 });
    }

    private async Task<(RecordingTcpClient Recorder, OutboxProcessor Processor)> StartProcessorAsync(
        LocalMessage localMessage, LocalOutboxMessage outbox)
    {
        await _db.EnqueueOutboxWithMessageAsync(outbox, localMessage);

        var recorder = new RecordingTcpClient();
        var session = new ChatSessionClient(recorder, new MessagePacketCodec(), new JsonPacketBodySerializer());
        var eventBus = new InMemoryEventBus();
        var userContext = new StubCurrentUserContext(OwnerId);

        SetupAutoAuth(recorder, OwnerId);
        SetupAutoAck(recorder);
        await session.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await session.AuthenticateAsync("token", OwnerId, null, null);

        var store = new MessageStore(_db, eventBus, session);
        var coordinator = new ChatMessageCoordinator(session, store, userContext);

        var processor = new OutboxProcessor(_db, session, userContext, eventBus);
        processor.Start();
        eventBus.Publish(new OutboxEnqueuedEvent(outbox.ClientMessageId, Conv, 0));
        return (recorder, processor);
    }

    private LocalOutboxMessage NewOutbox(string clientId, bool withIds) => new()
    {
        OwnerUserId = OwnerId,
        ClientMessageId = clientId,
        ConversationId = Conv,
        TargetUserId = PeerId,
        Content = "uplink refs 测试",
        AttachmentIdsJson = withIds ? AttachmentJson.SerializeIds(["att-voice-1"]) : null,
        Status = OutboxStatus.Queued,
        QueuedAt = DateTime.UtcNow
    };

    private static void SetupAutoAuth(RecordingTcpClient tcp, long userId)
    {
        tcp.OnFrameSent += (cmd, _) =>
        {
            if (cmd == PacketCommand.AuthenticationRequest)
            {
                InjectPacket(tcp, new JsonPacketBodySerializer(), PacketCommand.AuthenticationResponse,
                    new AuthResponseDto { Success = true, UserId = userId });
            }
        };
    }

    private static void SetupAutoAck(RecordingTcpClient tcp)
    {
        var serializer = new JsonPacketBodySerializer();
        tcp.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.ChatMessage)
                return;
            var dto = serializer.Deserialize<ChatMessageDto>(new ReadOnlySequence<byte>(body.ToArray()));
            if (dto is null)
                return;
            tcp.Record(dto);
            InjectPacket(tcp, serializer, PacketCommand.MessageAcknowledgement, new MessageAcknowledgementDto
            {
                ClientMessageId = dto.MessageId,
                CommandId = $"svr-{dto.MessageId}",
                Accepted = true,
                AcknowledgedUtc = DateTime.UtcNow
            });
        };
    }

    private static void InjectPacket<T>(RecordingTcpClient tcp, IPacketBodySerializer serializer, PacketCommand command, T? payload)
    {
        var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
        serializer.Serialize(writer, payload);
        var bodyLen = writer.WrittenCount;
        var packet = new MessagePacket(command,
            bodyLen == 0 ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
        var frameWriter = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + bodyLen);
        new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
        tcp.InjectData(frameWriter.WrittenMemory);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"条件在 {timeout.TotalSeconds:F1}s 内未满足");
    }

    /// <summary>回环 TCP 假客户端：对握手回 ServerHello，记录已发送的 ChatMessageDto。</summary>
    private sealed class RecordingTcpClient : ITcpClient
    {
        private readonly object _lock = new();
        private readonly List<ChatMessageDto> _sent = new();
        private readonly object _injectLock = new();
        private bool _connected;

        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event Action<PacketCommand, ReadOnlyMemory<byte>>? OnFrameSent;

        public bool IsConnected => _connected;

        public IReadOnlyList<ChatMessageDto> SentChatMessages
        {
            get { lock (_lock) return _sent.ToList(); }
        }

        public void Record(ChatMessageDto dto)
        {
            lock (_lock)
                _sent.Add(dto);
        }

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
        {
            _connected = true;
            return Task.CompletedTask;
        }

        public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            var seq = new ReadOnlySequence<byte>(data);
            while (seq.Length > 0)
            {
                if (!MessagePacket.TryDeserialize(ref seq, out var pkt, out _))
                    break;
                if (pkt.Command == PacketCommand.ClientHello)
                    OnDataChunkReceived?.Invoke(this, TcpHandshakeTestServer.ServerHelloFrame);
                OnFrameSent?.Invoke(pkt.Command, pkt.Body.ToArray());
            }
            await Task.CompletedTask;
        }

        public Task ReceiveDataAsync(CancellationToken token) => Task.Delay(-1, token);

        public void Disconnect(string? reason = null)
        {
            if (!_connected) return;
            _connected = false;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason));
        }

        public void InjectData(ReadOnlyMemory<byte> chunk)
        {
            lock (_injectLock)
                OnDataChunkReceived?.Invoke(this, chunk);
        }

        public void Dispose() { _connected = false; GC.SuppressFinalize(this); }
    }

    private sealed class StubCurrentUserContext(long userId) : ICurrentUserContext
    {
        public long Generation => 1;
        public long? UserId => userId;
        public string? UserName => $"user-{userId}";
        public bool IsAuthenticated => true;
        public bool HasUserId => userId > 0;
        public UserSessionSnapshot Snapshot => new(userId, 1, UserName, null, null);
        public long RequireUserId() => userId;
        public bool TryGetUserId(out long id)
        {
            id = userId;
            return userId > 0;
        }
    }

    private sealed class DbContextFactoryStub(string dbPath) : IDbContextFactory<ClientDbContext>
    {
        public ClientDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ClientDbContext>().UseSqlite($"Data Source={dbPath}").Options);
    }
}
