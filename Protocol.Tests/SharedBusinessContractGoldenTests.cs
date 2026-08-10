using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Chat_App.Infrastructure.Serialization;
using ChatApp.Shared.Protocol.Tcp;
using Xunit;

namespace Chat_App.Protocol.Tests;

public sealed class SharedBusinessContractGoldenTests
{
    private const string HistoryRequestJson =
        "{\"requestId\":\"history-01\",\"conversationId\":\"conversation-01\",\"afterReceivedAtMs\":1735689600100,\"afterMessageId\":\"message-10\",\"limit\":50}";

    private const string HistoryResponseJson =
        "{\"requestId\":\"history-01\",\"conversationId\":\"conversation-01\",\"succeeded\":true,\"items\":[],\"nextCursor\":{\"receivedAtMs\":1735689600000,\"changedAtMs\":1735689600100,\"messageId\":\"message-10\"},\"hasMore\":true}";

    private const string SyncRequestJson =
        "{\"requestId\":\"sync-01\",\"listLimit\":50,\"historyLimitPerConversation\":20,\"maxConversationsWithHistory\":10,\"watermarks\":[{\"conversationId\":\"conversation-01\",\"afterReceivedAtMs\":1735689600100,\"afterMessageId\":\"message-10\"}]}";

    private const string SyncResponseJson =
        "{\"requestId\":\"sync-01\",\"succeeded\":true,\"serverTimeMs\":1735689600200,\"conversations\":[],\"conversationsHasMore\":false,\"catchUps\":[],\"resetsRequired\":[]}";

    [Fact]
    public void ClientWritesGatewayHistoryRequestGolden()
    {
        var value = new MessageHistoryRequest
        {
            RequestId = "history-01",
            ConversationId = "conversation-01",
            AfterReceivedAtMs = 1_735_689_600_100,
            AfterMessageId = "message-10"
        };

        Assert.Equal(
            HistoryRequestJson,
            JsonSerializer.Serialize(
                value,
                TypeInfo<MessageHistoryRequest>()));
    }

    [Fact]
    public void ClientReadsGatewayHistoryResponseGolden()
    {
        var value = JsonSerializer.Deserialize(
            HistoryResponseJson,
            TypeInfo<MessageHistoryResponse>());

        Assert.NotNull(value);
        Assert.Equal("conversation-01", value.ConversationId);
        Assert.Equal(1_735_689_600_100, value.NextCursor?.ChangedAtMs);
    }

    [Fact]
    public void ClientWritesGatewaySyncRequestGolden()
    {
        var value = new SyncBootstrapRequest
        {
            RequestId = "sync-01",
            Watermarks =
            [
                new ConversationSyncWatermark
                {
                    ConversationId = "conversation-01",
                    AfterReceivedAtMs = 1_735_689_600_100,
                    AfterMessageId = "message-10"
                }
            ]
        };

        Assert.Equal(
            SyncRequestJson,
            JsonSerializer.Serialize(
                value,
                TypeInfo<SyncBootstrapRequest>()));
    }

    [Fact]
    public void ClientReadsGatewaySyncResponseGolden()
    {
        var value = JsonSerializer.Deserialize(
            SyncResponseJson,
            TypeInfo<SyncBootstrapResponse>());

        Assert.NotNull(value);
        Assert.True(value.Succeeded);
        Assert.Equal(1_735_689_600_200, value.ServerTimeMs);
    }

    [Fact]
    public void ClientReadsLegacyHistoryResponseWithoutNewOptionalFields()
    {
        const string legacyJson =
            "{\"requestId\":\"history-legacy\",\"succeeded\":true,\"items\":[],\"nextCursor\":{\"receivedAtMs\":1735689600000,\"messageId\":\"message-10\"},\"hasMore\":true}";

        var value = JsonSerializer.Deserialize(
            legacyJson,
            TypeInfo<MessageHistoryResponse>());

        Assert.NotNull(value);
        Assert.Null(value.ConversationId);
        Assert.Null(value.NextCursor?.ChangedAtMs);
    }

    [Fact]
    public void ClientReadsUnknownOptionalFieldAndUnknownResetReason()
    {
        const string futureJson =
            "{\"requestId\":\"sync-future\",\"succeeded\":true,\"serverTimeMs\":1735689600200,\"conversations\":[],\"conversationsHasMore\":false,\"catchUps\":[],\"resetsRequired\":[{\"conversationId\":\"conversation-01\",\"reason\":255}],\"futureOptional\":true}";

        var value = JsonSerializer.Deserialize(
            futureJson,
            TypeInfo<SyncBootstrapResponse>());

        Assert.NotNull(value);
        Assert.Equal((TcpSyncCursorResetReason)255, value.ResetsRequired.Single().Reason);
    }

    [Fact]
    public void ClientWritesBothHistoryCursorDirectionsWithoutChangingUnixMilliseconds()
    {
        var value = new MessageHistoryRequest
        {
            RequestId = "history-bidirectional",
            ConversationId = "conversation-01",
            BeforeReceivedAtMs = 1_735_689_600_001,
            BeforeMessageId = "message-before",
            AfterReceivedAtMs = 1_735_689_600_999,
            AfterMessageId = "message-after",
            Limit = 17
        };

        var json = JsonSerializer.Serialize(value, TypeInfo<MessageHistoryRequest>());
        var roundTrip = JsonSerializer.Deserialize(json, TypeInfo<MessageHistoryRequest>());

        Assert.NotNull(roundTrip);
        Assert.Equal(value.BeforeReceivedAtMs, roundTrip.BeforeReceivedAtMs);
        Assert.Equal(value.AfterReceivedAtMs, roundTrip.AfterReceivedAtMs);
        Assert.Equal(value.BeforeMessageId, roundTrip.BeforeMessageId);
        Assert.Equal(value.AfterMessageId, roundTrip.AfterMessageId);
    }

    [Fact]
    public void ClientRejectsTruncatedGatewayPayload()
    {
        const string truncated =
            "{\"requestId\":\"sync-01\",\"succeeded\":true,\"catchUps\":[{";

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            truncated,
            TypeInfo<SyncBootstrapResponse>()));
    }

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        (JsonTypeInfo<T>)(ChatJsonContext.Default.GetTypeInfo(typeof(T))
            ?? throw new InvalidOperationException($"Missing source-generated JSON metadata for {typeof(T)}."));
}
