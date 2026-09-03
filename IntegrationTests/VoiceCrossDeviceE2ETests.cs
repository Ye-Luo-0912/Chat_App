using System.Buffers;
using System.Collections.Concurrent;
using Chat_App.Infrastructure.Models.Context;
using Chat_App.Infrastructure.Persistence;
using Chat_App.Infrastructure.Serialization;
using Chat_App.Infrastructure.Services;
using Core.Contracts.Attachments;
using ChatApp.Contracts.Http.Attachments;
using Core.Helpers;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Services;
using Core.Services.Voice;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 跨设备语音端到端联调（VOICE-MSG-2）。
/// 内存双设备 harness：设备 A 录制 → 上传 → 发送；内存"服务端路由"把上行消息
/// （仅 AttachmentIds）转成带语音元数据的下行消息注入设备 B；设备 B 经真实
/// <see cref="ChatSessionClient"/> 接收、经 <see cref="AttachmentDownloadService"/> 下载
/// 到本地缓存，再交给 <see cref="IAudioPlayer"/> 播放。全程走真实 wire 编解码与
/// 真正的声音链路口径，仅网络传输与音频后端用确定性替身。
/// </summary>
public class VoiceCrossDeviceE2ETests
{
    private const long DeviceAUserId = 7101;
    private const long DeviceBUserId = 9101;
    private const int SampleRate = 16_000;
    private const short Channels = 1;

    [Fact]
    public async Task VoiceMessage_RecordedOnA_UploadedSent_AndDownloadedPlayedOnB()
    {
        // 共享附件存储（模拟服务端）：attachmentId → 字节 + 语音元数据。
        using var blob = new VoiceBlobStore();
        var fakeAttachments = new FakeAttachmentClient(blob);

        // 设备 A、B 各自的 TCP 模拟连接 + 客户端。
        using var tcpA = new ScriptedTcpClient();
        using var tcpB = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var codec = new MessagePacketCodec();
        using var clientA = new ChatSessionClient(tcpA, codec, serializer);
        using var clientB = new ChatSessionClient(tcpB, codec, serializer);

        SetupAutoAuth(tcpA, serializer, DeviceAUserId);
        SetupAutoAuth(tcpB, serializer, DeviceBUserId);

        await clientA.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await clientA.AuthenticateAsync("token-a", DeviceAUserId, null, null);
        Assert.True(clientA.IsAuthenticated);

        await clientB.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await clientB.AuthenticateAsync("token-b", DeviceBUserId, null, null);
        Assert.True(clientB.IsAuthenticated);

        // 服务端路由：捕获设备 A 的上行 ChatMessage（仅 AttachmentIds），
        // 从共享附件存储回填语音元数据后注入设备 B。
        var receivedB = new TaskCompletionSource<ChatMessageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        clientB.ChatMessageReceived += (_, m) => receivedB.TrySetResult(m);
        WireServerRouter(tcpA, tcpB, serializer, blob);

        // ── 设备 A：录制 → 上传 → 发送 ──
        using var recorder = new VoiceRecorderService(
            new SineToneSampleSource(SampleRate, Channels, maxDuration: TimeSpan.FromSeconds(5)));
        recorder.Start();
        await Task.Delay(150);
        using var recording = recorder.Stop();
        Assert.NotNull(recording);
        Assert.False(recorder.IsRecording);

        var wavStream = recording.WavStream;
        wavStream.Position = 0;
        var upload = await fakeAttachments.UploadAndConfirmAsync(
            wavStream, "audio/wav", wavStream.Length, "voice.wav",
            clientAttachmentId: "voice-cross-device-1");
        blob.AddVoiceMetadata(upload.AttachmentId, recording.Metadata);

        var clientMessageId = await clientA.SendChatMessageAsync(
            DeviceBUserId, content: null, attachmentIds: [upload.AttachmentId]);
        Assert.False(string.IsNullOrWhiteSpace(clientMessageId));

        // ── 设备 B：接收（跨设备投递）──
        var received = await receivedB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(DeviceAUserId, received.SenderUserId);
        Assert.Equal(DeviceBUserId, received.TargetUserId);
        var att = Assert.Single(received.Attachments ?? []);
        Assert.NotNull(att);
        Assert.Equal(upload.AttachmentId, att.AttachmentId);
        Assert.True(att.IsVoice, "下行附件应标记为语音");
        Assert.Equal("pcm", att.VoiceCodec);
        Assert.Equal("wav", att.VoiceContainer);
        Assert.Equal(recording.Metadata.DurationMs, att.VoiceDurationMs);
        Assert.Equal(SampleRate, att.VoiceSampleRateHz);
        Assert.Equal(Channels, att.VoiceChannels);
        Assert.Equal(upload.AttachmentId, att.DownloadApiHint);

        // ── 设备 B：下载到本地缓存 → 播放 ──
        var userContextB = new StubCurrentUserContext(DeviceBUserId);
        var dbB = CreateDatabase();
        var storageB = new AttachmentStorageService(userContextB, TempDir());
        var downloadService = new AttachmentDownloadService(fakeAttachments, storageB, dbB, userContextB);

        var path = await downloadService.GetOrDownloadAsync(
            att.AttachmentId, "voice.wav", att.DownloadApiHint);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));

        var player = new RecordingAudioPlayer();
        player.Play(att.AttachmentId, path!);
        Assert.True(player.IsPlaying);
        Assert.Equal(att.AttachmentId, player.CurrentKey);

        // 播放读取的 WAV 与设备 A 录制产物字节级一致（跨设备送达同一段音频）。
        var playedBytes = File.ReadAllBytes(player.PlayedPath!);
        var recordedBytes = blob.GetBytes(upload.AttachmentId);
        Assert.Equal(recordedBytes, playedBytes);
        Assert.True(playedBytes.Length > 44, "应包含完整 WAV 头 + 数据");
    }

    // ── 服务端路由：A 上行 ChatMessage → 回填语音元数据 → 注入 B ──

    private static void WireServerRouter(
        ScriptedTcpClient tcpA, ScriptedTcpClient tcpB, JsonPacketBodySerializer serializer, VoiceBlobStore blob)
    {
        tcpA.OnFrameSent += (cmd, body) =>
        {
            if (cmd != PacketCommand.ChatMessage)
                return;
            var up = serializer.Deserialize<ChatMessageDto>(new ReadOnlySequence<byte>(body));
            if (up?.AttachmentIds is not { Count: > 0 })
                return;

            var down = new ChatMessageDto
            {
                MessageId = up.MessageId,
                ClientMessageId = up.ClientMessageId,
                ConversationId = ConversationId.CreateDirect(DeviceBUserId, DeviceAUserId),
                TargetUserId = DeviceBUserId,
                SenderUserId = DeviceAUserId,
                Content = up.Content,
                SentUtc = up.SentUtc,
                Attachments = up.AttachmentIds.Select(id => ToDownlinkAttachment(id, blob)).ToList()
            };
            InjectPacket(tcpB, serializer, PacketCommand.ChatMessage, down);
        };
    }

    private static AttachmentRefDto ToDownlinkAttachment(string id, VoiceBlobStore blob)
    {
        var meta = blob.GetMetadata(id);
        return new AttachmentRefDto
        {
            AttachmentId = id,
            FileName = $"voice-{id}.wav",
            ContentType = meta?.Container == "wav" ? "audio/wav" : $"audio/{meta?.Container}",
            SizeBytes = meta?.SizeBytes ?? 0,
            Status = 1,
            DownloadApiHint = id,
            IsVoice = true,
            VoiceCodec = meta?.Codec,
            VoiceContainer = meta?.Container,
            VoiceDurationMs = meta?.DurationMs,
            VoiceSampleRateHz = meta?.SampleRateHz,
            VoiceChannels = meta?.Channels
        };
    }

    private static void SetupAutoAuth(ScriptedTcpClient tcp, IPacketBodySerializer serializer, long userId)
    {
        tcp.OnFrameSent += (cmd, _) =>
        {
            if (cmd == PacketCommand.AuthenticationRequest)
                InjectPacket(tcp, serializer, PacketCommand.AuthenticationResponse,
                    new AuthResponseDto { Success = true, UserId = userId });
        };
    }

    private static void InjectPacket<T>(
        ScriptedTcpClient tcp, IPacketBodySerializer serializer, PacketCommand command, T? payload)
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

    private static DatabaseService CreateDatabase()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chat_voice_e2e_{Guid.NewGuid():N}.db");
        var factory = new DbContextFactoryStub(path);
        var db = new DatabaseService(factory);
        using var ctx = factory.CreateDbContext();
        ctx.Database.EnsureCreated();
        return db;
    }

    private static string TempDir()
        => Path.Combine(Path.GetTempPath(), $"chat_voice_e2e_{Guid.NewGuid():N}");

    // ── 共享附件存储（模拟服务端附件库）──

    private sealed class VoiceBlobStore : IDisposable
    {
        private readonly ConcurrentDictionary<string, byte[]> _bytes = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, VoiceMetadata> _metadata = new(StringComparer.Ordinal);

        public void Store(string id, byte[] bytes) => _bytes[id] = bytes;
        public void AddVoiceMetadata(string id, VoiceMetadata meta) => _metadata[id] = meta;
        public byte[] GetBytes(string id) => _bytes[id];
        public VoiceMetadata? GetMetadata(string id) => _metadata.TryGetValue(id, out var m) ? m : null;

        public void Dispose() { }
    }

    /// <summary>假附件客户端：上传把字节存入共享 blob，下载从共享 blob 返回同一字节。</summary>
    private sealed class FakeAttachmentClient : IAttachmentClientService
    {
        private readonly VoiceBlobStore _blob;

        public FakeAttachmentClient(VoiceBlobStore blob) => _blob = blob;

        public Task<AttachmentUploadResult> UploadAndConfirmAsync(
            Stream content, string contentType, long contentLength, string? originalName = null,
            string? clientAttachmentId = null, IProgress<AttachmentUploadProgress>? progress = null,
            int maxAttempts = 3, string? sha256 = null, CancellationToken ct = default)
        {
            var ms = new MemoryStream();
            content.CopyTo(ms);
            var bytes = ms.ToArray();
            var id = string.IsNullOrWhiteSpace(clientAttachmentId) ? $"att-{Guid.NewGuid():N}" : clientAttachmentId!;
            _blob.Store(id, bytes);
            return Task.FromResult(new AttachmentUploadResult
            {
                AttachmentId = id,
                DownloadPath = id,
                ObjectKey = id,
                ContentType = contentType,
                SizeBytes = bytes.Length,
                OriginalName = originalName
            });
        }

        public Task<AttachmentDownloadResult> DownloadAsync(
            string attachmentIdOrHint, long? rangeFrom = null, long? rangeTo = null, CancellationToken ct = default)
        {
            var bytes = _blob.GetBytes(attachmentIdOrHint);
            var start = rangeFrom is long from ? (int)from : 0;
            var slice = bytes[start..];
            return Task.FromResult(new AttachmentDownloadResult
            {
                Content = new MemoryStream(slice),
                ContentType = "audio/wav",
                ContentLength = slice.Length,
                FileName = "voice.wav",
                IsPartialContent = rangeFrom is not null
            });
        }

        public Task<AttachmentPresignResponse> PresignAsync(AttachmentPresignRequest request, CancellationToken ct = default)
            => Task.FromResult(new AttachmentPresignResponse());

        public Task UploadAsync(AttachmentPresignResponse ticket, Stream content, string contentType, long contentLength, IProgress<AttachmentUploadProgress>? progress = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<ConfirmAttachmentResponse> ConfirmAsync(ConfirmAttachmentRequest request, CancellationToken ct = default)
            => Task.FromResult(new ConfirmAttachmentResponse());

        public Task AbandonAsync(string attachmentId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    /// <summary>录音型播放器替身：记录最近一次 Play 的 key 与本地路径，供测试读取文件校验。</summary>
    private sealed class RecordingAudioPlayer : IAudioPlayer
    {
        public bool IsPlaying { get; private set; }
        public string? CurrentKey { get; private set; }
        public string? PlayedPath { get; private set; }
        public string? SelectedOutputDeviceId { get; private set; }
        public event Action<AudioPlaybackProgress>? Progress;
        public event Action? Stopped;

        public void Play(string key, string wavPath)
        {
            CurrentKey = key;
            PlayedPath = wavPath;
            IsPlaying = true;
        }

        public void Pause() => IsPlaying = false;
        public void Resume() => IsPlaying = true;

        public void Stop()
        {
            IsPlaying = false;
            CurrentKey = null;
            Stopped?.Invoke();
        }

        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public void SelectOutputDevice(string? deviceId) => SelectedOutputDeviceId = deviceId;
        public void Dispose() { }
    }

    // ── 测试替身（与既有 IntegrationTests 一致）──

    private sealed class ScriptedTcpClient : ITcpClient
    {
        private volatile bool _connected;

        public bool IsConnected => _connected;
        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;
        public event Action<PacketCommand, ReadOnlyMemory<byte>>? OnFrameSent;

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
        {
            _connected = true;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Connected));
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
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
            return Task.CompletedTask;
        }

        public Task ReceiveDataAsync(CancellationToken token) => Task.Delay(-1, token);

        public void Disconnect(string? reason = null)
        {
            if (!_connected) return;
            _connected = false;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason));
        }

        public void InjectData(ReadOnlyMemory<byte> chunk)
            => OnDataChunkReceived?.Invoke(this, chunk);

        public void Dispose()
        {
            _connected = false;
            GC.SuppressFinalize(this);
        }
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
        public ClientDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<ClientDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            return new ClientDbContext(options);
        }

        public Task<ClientDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}