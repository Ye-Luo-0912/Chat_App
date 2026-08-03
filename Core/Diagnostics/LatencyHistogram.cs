using System;

namespace Core.Diagnostics;

/// <summary>延迟直方图快照：保留最近窗口内样本的分位数。</summary>
public sealed record HistogramSnapshot(
    long Count,
    long P50Ms,
    long P90Ms,
    long P95Ms,
    long P99Ms,
    long MaxMs)
{
    /// <summary>单点样本（Count=1，全分位相等）。</summary>
    public static HistogramSnapshot Point(long ms) => new(1, ms, ms, ms, ms, ms);

    /// <summary>空快照。</summary>
    public static HistogramSnapshot Empty => new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// 轻量延迟直方图：固定窗口环形缓冲保存最近样本，查询时排序取分位数。
/// 线程安全（锁内读写）。窗口大小决定分位数精度：默认 512 样本。
/// </summary>
public sealed class LatencyHistogram
{
    private readonly long[] _samples;
    private readonly object _gate = new();
    private int _cursor;
    private long _totalCount;

    public LatencyHistogram(int window = 512)
    {
        _samples = new long[window];
    }

    public void Add(TimeSpan elapsed) => AddMs((long)Math.Max(0, elapsed.TotalMilliseconds));

    public void AddMs(long elapsedMs)
    {
        lock (_gate)
        {
            _samples[_cursor % _samples.Length] = elapsedMs;
            _cursor++;
            _totalCount++;
        }
    }

    /// <summary>当前窗口内样本的分位数快照（Count 为累计样本总数）。</summary>
    public HistogramSnapshot Snapshot()
    {
        lock (_gate)
        {
            var n = (int)Math.Min(_totalCount, _samples.Length);
            var buf = new long[n];
            var length = _samples.Length;
            for (var i = 0; i < n; i++)
            {
                var idx = (_cursor - n + i) % length;
                buf[i] = _samples[(idx + length) % length];
            }
            Array.Sort(buf);
            return new HistogramSnapshot(
                _totalCount,
                Percentile(buf, 0.50),
                Percentile(buf, 0.90),
                Percentile(buf, 0.95),
                Percentile(buf, 0.99),
                n == 0 ? 0 : buf[n - 1]);
        }
    }

    private static long Percentile(long[] sorted, double p) =>
        sorted.Length == 0 ? 0 : sorted[(int)((sorted.Length - 1) * p)];
}
