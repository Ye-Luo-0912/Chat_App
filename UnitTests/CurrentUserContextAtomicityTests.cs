using Chat_App.Infrastructure.Services;
using Core.Interfaces;
using System.Threading.Tasks;
using Xunit;

namespace UnitTests;

/// <summary>
/// CurrentUserContext 原子性与三代际语义测试：
/// - 并发写入不丢失更新（CAS read-modify-write）
/// - 同账户重连不递增账户代际（仅更新连接代际）
/// - 账户切换才递增账户代际；令牌代际独立递增
/// </summary>
public class CurrentUserContextAtomicityTests
{
    [Fact]
    public void Concurrent_Updates_Do_Not_Lose_Writes()
    {
        var ctx = new CurrentUserContext();
        ctx.SetAuthenticatedSession(1001, "alice", "s1", 42, connectionGeneration: 1);

        // 并发：令牌刷新（BumpTokenGeneration）与连接代际更新交错执行
        Parallel.For(0, 200, _ => ctx.BumpTokenGeneration());
        Parallel.For(0, 200, _ => ctx.BumpConnectionGeneration(7));

        var snap = ctx.Snapshot;
        Assert.Equal(200, snap.TokenGeneration);          // 200 次刷新全部生效
        Assert.Equal(7, snap.ConnectionGeneration);       // 连接代际一致
        Assert.Equal(1001, snap.OwnerUserId);
        Assert.Equal("alice", snap.UserName);
    }

    [Fact]
    public void Concurrent_Account_Switch_And_Clear_Are_Atomic()
    {
        var ctx = new CurrentUserContext();
        ctx.SetAuthenticatedSession(1001, "alice", "s1", 42, connectionGeneration: 1);

        // 并发账户切换与登出：最终状态必须是两者之一，不存在中间态
        Parallel.For(0, 100, _ =>
        {
            ctx.SetAuthenticatedSession(2002, "bob", "s2", 43, connectionGeneration: 2);
            ctx.Clear();
        });

        var snap = ctx.Snapshot;
        // 要么是 bob（2002），要么是空（Clear 后 0）——绝不可能是 alice 残留
        Assert.True(snap.IsEmpty || snap.OwnerUserId == 2002);
        if (!snap.IsEmpty)
            Assert.Equal("bob", snap.UserName);
    }

    [Fact]
    public void Same_Account_Reconnect_Does_Not_Bump_Account_Generation()
    {
        var ctx = new CurrentUserContext();
        ctx.SetAuthenticatedSession(1001, "alice", "s1", 42, connectionGeneration: 1);
        var genAfterLogin = ctx.Generation;

        // 同一账户传输重连：账户代际不变，连接代际更新
        ctx.SetAuthenticatedSession(1001, "alice", "s1", 42, connectionGeneration: 5);
        Assert.Equal(genAfterLogin, ctx.Generation);
        Assert.Equal(5, ctx.ConnectionGeneration);
    }

    [Fact]
    public void Account_Switch_Bumps_Account_Generation()
    {
        var ctx = new CurrentUserContext();
        ctx.SetAuthenticatedSession(1001, "alice", "s1", 42, connectionGeneration: 1);
        var genAfterA = ctx.Generation;

        ctx.SetAuthenticatedSession(2002, "bob", "s2", 43, connectionGeneration: 1);
        Assert.True(ctx.Generation > genAfterA);
        Assert.Equal(2002, ctx.UserId);
    }

    [Fact]
    public void Token_Generation_Is_Independent_Of_Account_Generation()
    {
        var ctx = new CurrentUserContext();
        ctx.SetAuthenticatedSession(1001, "alice", "s1", 42, connectionGeneration: 1);
        var genAfterLogin = ctx.Generation;

        ctx.BumpTokenGeneration();
        ctx.BumpTokenGeneration();

        Assert.Equal(2, ctx.TokenGeneration);
        Assert.Equal(genAfterLogin, ctx.Generation); // 账户代际不受令牌刷新影响
    }
}
