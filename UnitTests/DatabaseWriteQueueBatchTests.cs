using Chat_App.Infrastructure.Persistence;
using Xunit;

namespace UnitTests;

/// <summary>
/// DatabaseWriteQueue 攒批调度验收：
/// 并发入队的大量操作必须全部完成、按操作隔离（单点失败不影响其余）、
/// batch_size 指标反映每轮实际排空粒度（合法范围 1..64）。
/// </summary>
public class DatabaseWriteQueueBatchTests
{
    [Fact]
    public async Task Concurrent_Enqueue_All_Operations_Complete_And_Isolated()
    {
        await using var queue = new DatabaseWriteQueue();
        var operations = Enumerable.Range(0, 30)
            .Select(i => queue.EnqueueAsync(ct =>
            {
                // 注入一个失败操作验证隔离：第 7 个抛异常，其余必须全部成功。
                if (i == 7)
                    throw new InvalidOperationException($"injected-failure-{i}");
                return Task.CompletedTask;
            }))
            .ToArray();

        // 成功操作的 tcs 必须全部完成（失败操作的异常仅由 EnqueueAsync 抛出）。
        var results = await Task.WhenAll(
            operations.Select((op, i) => RunAndCaptureAsync(op, i)));

        Assert.Equal(30, results.Length);
        Assert.Equal(29, results.Count(r => r == "ok"));
        Assert.Equal(1, results.Count(r => r == "injected-failure-7"));

        // 指标闭环：处理计数与失败计数准确，批大小在调度粒度合法范围。
        // 失败操作不计入 processed（仅记 failed），与消费者实现语义一致。
        Assert.Equal(29, queue.Counters["processed"]);
        Assert.Equal(1, queue.Counters["failed"]);
        Assert.InRange(queue.Counters["batch_size"], 1, 64);
    }

    private static async Task<string> RunAndCaptureAsync(Task task, int index)
    {
        try
        {
            await task;
            return "ok";
        }
        catch (InvalidOperationException ex) when (ex.Message == $"injected-failure-{index}")
        {
            return ex.Message;
        }
    }
}
