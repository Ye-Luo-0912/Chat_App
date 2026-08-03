using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Core.Diagnostics;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Infrastructure.Diagnostics;

/// <summary>
/// 诊断指标聚合与导出服务：注册各 IMetricsSource，按固定周期
/// （默认 60s）将计数与延迟分位汇总输出到日志，便于定位积压来源与 p95/p99。
/// 导出逻辑不阻塞任何业务路径（快照方法均为内存读，个别源允许轻量 DB 统计）。
/// </summary>
public sealed class DiagnosticsService : IDisposable
{
    private readonly List<IMetricsSource> _sources = [];
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _disposed;

    /// <summary>导出周期。</summary>
    public TimeSpan ExportInterval { get; set; } = TimeSpan.FromSeconds(60);

    public void AddSource(IMetricsSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            _sources.Add(source);
        }
    }

    /// <summary>启动周期导出（幂等）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_timer is not null)
                return;
            _timer = new Timer(_ => Export(), null, ExportInterval, ExportInterval);
            Log.Information("诊断指标导出已启动，周期 {Interval}", ExportInterval);
        }
    }

    /// <summary>停止周期导出。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Export()
    {
        try
        {
            IMetricsSource[] sources;
            lock (_gate)
            {
                sources = _sources.ToArray();
            }

            var sb = new StringBuilder();
            sb.AppendLine("诊断指标快照：");
            foreach (var source in sources)
            {
                sb.Append("  [").Append(source.Name).Append(']');
                if (source.Counters.Count > 0)
                {
                    sb.Append(" counters{");
                    sb.Append(string.Join(", ", source.Counters.Select(kv => $"{kv.Key}={kv.Value}")));
                    sb.Append('}');
                }
                if (source.Histograms.Count > 0)
                {
                    sb.Append(" latencies{");
                    sb.Append(string.Join(", ", source.Histograms.Select(kv =>
                        $"{kv.Key}=p50:{kv.Value.P50Ms}ms p90:{kv.Value.P90Ms}ms p95:{kv.Value.P95Ms}ms p99:{kv.Value.P99Ms}ms max:{kv.Value.MaxMs}ms n:{kv.Value.Count}")));
                    sb.Append('}');
                }
                sb.AppendLine();
            }
            Log.Information("{Metrics}", sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "导出诊断指标失败");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        GC.SuppressFinalize(this);
    }
}
