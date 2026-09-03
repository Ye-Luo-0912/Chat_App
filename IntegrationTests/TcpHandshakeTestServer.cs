using System.Buffers;
using ChatApp.Binary.Core;
using ChatApp.Shared.Protocol.Tcp;
using ChatApp.Shared.Protocol.Tcp.Binary;
using ChatApp.Shared.Protocol.Tcp.Binary.Schemas;
using Core.Models;
using Core.Protocol;

namespace IntegrationTests;

/// <summary>集成测试假网关使用的 canonical ServerHello 帧。</summary>
internal static class TcpHandshakeTestServer
{
    public static ReadOnlyMemory<byte> ServerHelloFrame { get; } = CreateServerHelloFrame();

    /// <summary>
    /// 二进制变体：仍以 JSON 握手段（ServerHello 永远 JSON），但声明
    /// PayloadFormat = chatapp-bin-v1 与 BinaryPayload 能力位，完整握手后连接固定为二进制。
    /// </summary>
    public static ReadOnlyMemory<byte> BinaryServerHelloFrame { get; } =
        CreateServerHelloFrame(
            BinaryPayloadFormat.Id,
            GatewayFeature.CommandCapabilities |
            GatewayFeature.ConversationSync |
            GatewayFeature.ConversationPreferences |
            GatewayFeature.MessageMutation |
            GatewayFeature.PresenceAndTyping |
            GatewayFeature.GroupManagement |
            GatewayFeature.RelationshipRead |
            GatewayFeature.CallSignaling |
            GatewayFeature.BinaryPayload);

    private static byte[] CreateServerHelloFrame(
        string payloadFormat = ProtocolPayloadFormat.Json,
        GatewayFeature featureBits =
            GatewayFeature.CommandCapabilities |
            GatewayFeature.ConversationSync |
            GatewayFeature.ConversationPreferences |
            GatewayFeature.MessageMutation |
            GatewayFeature.PresenceAndTyping |
            GatewayFeature.GroupManagement |
            GatewayFeature.RelationshipRead |
            GatewayFeature.CallSignaling)
    {
        var serializer = new Chat_App.Infrastructure.Serialization.JsonPacketBodySerializer();
        var body = new ArrayBufferWriter<byte>();
        serializer.Serialize(body, new ServerHello
        {
            ProtocolVersion = 1,
            FeatureBits = (uint)featureBits,
            ServerDeviceId = "integration-test-gateway",
            ServerTimeMs = 1_700_000_000_000,
            HeartbeatIntervalMs = 15_000,
            MaxPayloadBytes = 1_048_576,
            ResumeSupported = false,
            PayloadFormat = payloadFormat
        });

        return WriteFrame(PacketCommand.ServerHello, body);
    }

    /// <summary>
    /// Resume 场景变体：恢复成功帧。网关契约——携带 ResumeToken 的 ClientHello 恢复成功时
    /// 只发 ResumeResponse（不发 ServerHello），会话直接进入已认证状态。
    /// </summary>
    public static byte[] CreateResumeSuccessFrame(
        long userId,
        string rotatedToken,
        string? sessionId = "resume-session",
        long? lastConversationSequence = 42)
    {
        var serializer = new Chat_App.Infrastructure.Serialization.JsonPacketBodySerializer();
        var body = new ArrayBufferWriter<byte>();
        serializer.Serialize(body, new ResumeResponse
        {
            Success = true,
            ResumeToken = rotatedToken,
            UserId = userId,
            SessionId = sessionId,
            DeviceId = "integration-test-device",
            LastConversationSequence = lastConversationSequence
        });

        return WriteFrame(PacketCommand.ResumeResponse, body);
    }

    /// <summary>
    /// Resume 场景变体：恢复失败帧。网关以 Error 表达失败（ResumeFailed/DependencyUnavailable/
    /// AccountSuspended），随后仍会发 ServerHello，客户端可回退完整认证。
    /// </summary>
    public static byte[] CreateResumeFailureErrorFrame(
        ProtocolErrorCode code = ProtocolErrorCode.ResumeFailed,
        string message = "resume token invalid or expired",
        int? retryAfterMs = null)
    {
        var serializer = new Chat_App.Infrastructure.Serialization.JsonPacketBodySerializer();
        var body = new ArrayBufferWriter<byte>();
        serializer.Serialize(body, new ProtocolErrorFrame
        {
            Code = code,
            Message = message,
            RetryAfterMs = retryAfterMs
        });

        return WriteFrame(PacketCommand.Error, body);
    }

    private static byte[] WriteFrame(PacketCommand command, ArrayBufferWriter<byte> body)
    {
        var frame = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + body.WrittenCount);
        var packet = new MessagePacket(
            command,
            body.WrittenCount == 0
                ? ReadOnlySequence<byte>.Empty
                : new ReadOnlySequence<byte>(body.WrittenMemory));
        if (!new MessagePacketCodec().TryWrite(packet, frame, out _))
            throw new InvalidOperationException($"无法构造集成测试帧 {command}");
        return frame.WrittenSpan.ToArray();
    }

    /// <summary>按共享 schema 编码一帧 S2C 二进制载荷（chatapp-bin-v1）。</summary>
    public static byte[] CreateBinaryFrame<T>(PacketCommand command, T shared) where T : class
    {
        var buffer = new byte[BinaryLimits.Default.MaxMessageBytes];
        var encode = TcpBinaryWireEncoder.TryEncode(shared, buffer, BinaryLimits.Default);
        if (encode.Status != TcpBinaryWireEncodeStatus.Encoded)
            throw new InvalidOperationException($"无法编码二进制测试帧 {command}: {encode.Status}");

        var frame = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + encode.Written);
        var packet = new MessagePacket(
            command,
            new ReadOnlySequence<byte>(buffer.AsSpan(0, encode.Written).ToArray()));
        if (!new MessagePacketCodec().TryWrite(packet, frame, out _))
            throw new InvalidOperationException("无法构造集成测试二进制帧");
        return frame.WrittenSpan.ToArray();
    }
}
