using System.Buffers;
using Chat_App.Infrastructure.Serialization;
using ChatApp.Shared.Protocol.Tcp;
using Core.Models;
using Core.Protocol;

namespace IntegrationTests;

/// <summary>集成测试假网关使用的 canonical ServerHello 帧。</summary>
internal static class TcpHandshakeTestServer
{
    public static ReadOnlyMemory<byte> ServerHelloFrame { get; } = CreateServerHelloFrame();

    private static byte[] CreateServerHelloFrame()
    {
        var serializer = new JsonPacketBodySerializer();
        var body = new ArrayBufferWriter<byte>();
        serializer.Serialize(body, new ServerHello
        {
            ProtocolVersion = 1,
            FeatureBits = (uint)(GatewayFeature.CommandCapabilities |
                                 GatewayFeature.ConversationSync |
                                 GatewayFeature.ConversationPreferences |
                                 GatewayFeature.MessageMutation |
                                 GatewayFeature.PresenceAndTyping |
                                 GatewayFeature.GroupManagement |
                                 GatewayFeature.RelationshipRead),
            ServerDeviceId = "integration-test-gateway",
            ServerTimeMs = 1_700_000_000_000,
            HeartbeatIntervalMs = 15_000,
            MaxPayloadBytes = 1_048_576,
            ResumeSupported = false,
            PayloadFormat = ProtocolPayloadFormat.Json
        });

        var frame = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + body.WrittenCount);
        var packet = new MessagePacket(
            PacketCommand.ServerHello,
            new ReadOnlySequence<byte>(body.WrittenMemory));
        if (!new MessagePacketCodec().TryWrite(packet, frame, out _))
            throw new InvalidOperationException("无法构造集成测试 ServerHello 帧");
        return frame.WrittenSpan.ToArray();
    }
}
