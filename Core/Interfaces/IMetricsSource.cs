using Core.Diagnostics;
using System.Collections.Generic;

namespace Core.Interfaces;

/// <summary>
/// 指标源：各服务暴露运行计数与延迟分位直方图，
/// 由 DiagnosticsService 周期聚合导出（日志），用于定位积压来源与 p95/p99。
/// </summary>
public interface IMetricsSource
{
    /// <summary>指标源名称（如 "db_queue"、"outbox"、"network"）。</summary>
    string Name { get; }

    /// <summary>单调计数（积压、重试、成功/失败次数等）。</summary>
    IReadOnlyDictionary<string, long> Counters { get; }

    /// <summary>延迟分位直方图（p50/p90/p95/p99/max）。</summary>
    IReadOnlyDictionary<string, HistogramSnapshot> Histograms { get; }
}
