using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Chat_App.Infrastructure.Diagnostics;
using Core.Diagnostics;
using Core.Interfaces;
using Xunit;

namespace UnitTests;

/// <summary>
/// DiagnosticsService 结构化快照（UI 本地诊断页数据源）：
/// 按注册顺序返回各源计数与延迟分位，且快照不阻塞/不修改业务路径。
/// </summary>
public class DiagnosticsServiceSnapshotTests
{
    private sealed class FakeSource : IMetricsSource
    {
        public string Name { get; }
        public IReadOnlyDictionary<string, long> Counters { get; }
        public IReadOnlyDictionary<string, HistogramSnapshot> Histograms { get; }

        public FakeSource(string name,
            IReadOnlyDictionary<string, long>? counters = null,
            IReadOnlyDictionary<string, HistogramSnapshot>? histograms = null)
        {
            Name = name;
            Counters = counters ?? new Dictionary<string, long>();
            Histograms = histograms ?? new Dictionary<string, HistogramSnapshot>();
        }
    }

    [Fact]
    public void GetSnapshot_Returns_Sources_In_Registration_Order_With_Values()
    {
        using var service = new DiagnosticsService();
        service.AddSource(new FakeSource("first", new Dictionary<string, long> { ["a"] = 1 }));
        service.AddSource(new FakeSource("second",
            new Dictionary<string, long> { ["b"] = 2 },
            new Dictionary<string, HistogramSnapshot> { ["lat"] = HistogramSnapshot.Point(5) }));

        var snapshot = service.GetSnapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal("first", snapshot[0].Name);
        Assert.Equal(1, snapshot[0].Counters["a"]);
        Assert.Empty(snapshot[0].Histograms);

        Assert.Equal("second", snapshot[1].Name);
        Assert.Equal(2, snapshot[1].Counters["b"]);
        Assert.Equal(5, snapshot[1].Histograms["lat"].P95Ms);
        Assert.Equal(1, snapshot[1].Histograms["lat"].Count);
    }

    [Fact]
    public void GetSnapshot_Is_Empty_When_No_Sources()
    {
        using var service = new DiagnosticsService();
        Assert.Empty(service.GetSnapshot());
    }

    [Fact]
    public async Task GetSnapshot_Does_Not_Deadlock_With_Concurrent_AddSource()
    {
        using var service = new DiagnosticsService();
        var stop = new CancellationTokenSource();
        var addTask = Task.Run(async () =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                service.AddSource(new FakeSource($"s{i++}"));
                await Task.Delay(1);
            }
        });
        var readTask = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = service.GetSnapshot();
                await Task.Delay(1);
            }
        });

        await Task.Delay(100);
        stop.Cancel();
        await Task.WhenAll(addTask, readTask);
    }
}
