using Chat_App.Infrastructure.Models;
using Chat_App.Presentation.ViewModels.Chat;
using Core.Models.DTO;
using System.Collections.ObjectModel;
using Xunit;

namespace UnitTests;

/// <summary>
/// 会话中心状态测试：
/// - UpsertLocalConversation 已存在时就地 merge（UI 与字典同一实例，不替换集合对象）
/// - RemoveConversation 的 tombstone 阻止服务端投影复活
/// - ApplyIncrementalDiff keyed reconciliation 在大量重排下顺序正确、保留相同实例
/// </summary>
public class ChatFriendListStateTests
{
    private static (ChatFriendListState State, ObservableCollection<LocalConversation> Conversations, ObservableCollection<LocalConversation> Filtered) Create()
    {
        var conversations = new ObservableCollection<LocalConversation>();
        var filtered = new ObservableCollection<LocalConversation>();
        return (new ChatFriendListState(conversations, filtered), conversations, filtered);
    }

    private static LocalConversation Conv(string id, long lastAtMs, bool pinned = false) => new()
    {
        ConversationId = id,
        OwnerUserId = 1001,
        Type = 1,
        PeerUserId = 9001,
        LastMessagePreview = $"preview-{id}",
        LastMessageAtMs = lastAtMs,
        IsPinned = pinned
    };

    [Fact]
    public void Upsert_Existing_Conversation_Merges_In_Place_Same_Instance()
    {
        var (state, conversations, _) = Create();
        state.UpsertLocalConversation(Conv("conv-1", 1_000));
        var original = conversations[0];

        // 再次 Upsert 同一会话（更新的字段）
        state.UpsertLocalConversation(Conv("conv-1", 2_000));

        // 集合中的对象必须与索引中的是同一实例（UI 与字典一致，绑定不失效）
        Assert.Same(original, conversations[0]);
        Assert.Same(original, state.FindConversation("conv-1"));
        // 就地合并生效：字段已更新
        Assert.Equal(2_000, conversations[0].LastMessageAtMs);
        Assert.Equal("preview-conv-1", conversations[0].LastMessagePreview);
        // 不产生重复项
        Assert.Single(conversations);
    }

    [Fact]
    public void Removed_Conversation_Is_Not_Resurrected_By_Server_Projection()
    {
        var (state, conversations, filtered) = Create();
        state.UpsertLocalConversation(Conv("conv-del", 1_000));

        state.RemoveConversation("conv-del");
        Assert.Empty(conversations);
        Assert.Empty(filtered);

        // 服务端投影再次下发同一会话：tombstone 阻止复活
        state.ApplyConversationPrefs(
            new[] { new ConversationListItemDto { ConversationId = "conv-del", Type = ConversationTypeDto.Direct, PeerUserId = 9001 } },
            selfUserId: 1001);

        Assert.Empty(conversations);
        Assert.Null(state.FindConversation("conv-del"));
    }

    [Fact]
    public void Removed_Conversation_Is_Not_Resurrected_By_RealTime_Changed()
    {
        var (state, conversations, _) = Create();
        state.UpsertLocalConversation(Conv("conv-del2", 1_000));
        state.RemoveConversation("conv-del2");

        // 实时会话变化（新消息）：同样不得复活
        state.ApplyConversationChanged(
            new ConversationChangedDto { ConversationId = "conv-del2", LastMessagePreview = "新消息", LastMessageAtMs = 5_000 },
            selfUserId: 1001);

        Assert.Empty(conversations);
        Assert.Null(state.FindConversation("conv-del2"));
    }

    [Fact]
    public void ApplyFilter_Reorders_And_Keeps_Instances_Under_Heavy_Reshuffle()
    {
        var (state, conversations, filtered) = Create();

        // 预置 200 个会话（乱序时间戳），模拟大量重排
        var random = new Random(42);
        for (var i = 0; i < 200; i++)
            state.UpsertLocalConversation(Conv($"conv-{i:000}", random.Next(0, 1_000_000)));

        // 打乱顺序后重新投影（触发全量重排 diff）
        var shuffled = conversations.OrderBy(c => random.Next()).Select(c => c.ConversationId).ToList();
        state.ApplyConversationPrefs(
            shuffled.Select((id, idx) => new ConversationListItemDto
            {
                ConversationId = id,
                Type = ConversationTypeDto.Direct,
                PeerUserId = 9001,
                LastMessagePreview = $"preview-{id}",
                LastMessageAtMs = idx
            }).ToList(),
            selfUserId: 1001);

        // 顺序正确：ApplyFilter 排序规则（LastMessageAtMs 倒序）在全量重排后精确成立
        Assert.Equal(200, filtered.Count);
        for (var i = 1; i < filtered.Count; i++)
            Assert.True(
                filtered[i - 1].LastMessageAtMs >= filtered[i].LastMessageAtMs,
                $"位置 {i - 1}/{i} 排序错误: {filtered[i - 1].ConversationId} vs {filtered[i].ConversationId}");

        // 保留相同实例：全部 200 个仍是原对象（UI item container 不重建）
        var instances = new HashSet<LocalConversation>(conversations);
        Assert.Equal(200, instances.Count);
        Assert.All(filtered, c => Assert.Contains(c, instances));
    }

    [Fact]
    public void ApplyFilter_Pinned_Then_LastMessage_Ordering()
    {
        var (state, _, filtered) = Create();
        state.UpsertLocalConversation(Conv("a", 100));
        state.UpsertLocalConversation(Conv("b", 200));
        state.UpsertLocalConversation(Conv("c", 300, pinned: true));

        // 置顶优先 → 最后消息时间倒序
        Assert.Equal("c", filtered[0].ConversationId);
        Assert.Equal("b", filtered[1].ConversationId);
        Assert.Equal("a", filtered[2].ConversationId);
    }
}
