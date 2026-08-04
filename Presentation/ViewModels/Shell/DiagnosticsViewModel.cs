using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia.Threading;
using Chat_App.Infrastructure.Diagnostics;
using Chat_App.Shared.Commands;
using Chat_App.Shared.Mvvm;
using Serilog;

namespace Chat_App.Presentation.ViewModels.Shell;

/// <summary>单个指标源在诊断页的一行展示数据。</summary>
public sealed class DiagnosticsRowViewModel
{
    public string Name { get; }
    public string DetailText { get; }

    public DiagnosticsRowViewModel(string name, string detailText)
    {
        Name = name;
        DetailText = detailText;
    }
}

/// <summary>
/// 本地诊断页 ViewModel：周期（2s）拉取 DiagnosticsService 结构化快照，
/// 以只读文本行展示各指标源的计数与延迟分位，用于本地问题排查。
/// </summary>
public class DiagnosticsViewModel : ViewModelBase, IDisposable
{
    private readonly DiagnosticsService _diagnostics;
    private readonly DispatcherTimer _timer;
    private string _lastUpdatedText = "";

    public ObservableCollection<DiagnosticsRowViewModel> Rows { get; } = [];

    /// <summary>最近一次快照时间（"yyyy-MM-dd HH:mm:ss"）。</summary>
    public string LastUpdatedText
    {
        get => _lastUpdatedText;
        private set => SetProperty(ref _lastUpdatedText, value);
    }

    public RelayCommand RefreshCommand { get; }

    public DiagnosticsViewModel(DiagnosticsService diagnostics)
    {
        _diagnostics = diagnostics;
        RefreshCommand = new RelayCommand(Refresh);
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Refresh());
        _timer.Start();
        Refresh();
        Log.Debug("诊断页已启动，周期 2s 刷新");
    }

    private void Refresh()
    {
        try
        {
            var rows = _diagnostics.GetSnapshot()
                .Select(s => new DiagnosticsRowViewModel(s.Name, FormatDetail(s)))
                .ToList();

            Rows.Clear();
            foreach (var row in rows)
                Rows.Add(row);

            LastUpdatedText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "刷新诊断页指标失败");
        }
    }

    private static string FormatDetail(MetricsSourceSnapshot source)
    {
        var sb = new StringBuilder();
        if (source.Counters.Count > 0)
        {
            sb.Append("counters: ");
            sb.Append(string.Join(", ", source.Counters.Select(kv => $"{kv.Key}={kv.Value}")));
        }
        if (source.Histograms.Count > 0)
        {
            if (sb.Length > 0)
                sb.AppendLine();
            sb.Append("latencies: ");
            sb.Append(string.Join(", ", source.Histograms.Select(kv =>
                $"{kv.Key}=p50:{kv.Value.P50Ms} p95:{kv.Value.P95Ms} p99:{kv.Value.P99Ms} max:{kv.Value.MaxMs}ms n:{kv.Value.Count}")));
        }
        return sb.Length == 0 ? "（无指标）" : sb.ToString();
    }

    public void Dispose()
    {
        _timer.Stop();
        GC.SuppressFinalize(this);
    }
}
