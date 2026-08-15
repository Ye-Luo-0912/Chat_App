using System;
using Chat_App.Infrastructure.Services;
using Core.Diagnostics;
using Xunit;

namespace UnitTests;

/// <summary>
/// 同步失败诊断回归测试：结构化失败记录（错误码/信息/时间/可重试性）、
/// 累计与连续失败计数、成功归零连续失败且保留最近失败记录。
/// </summary>
public sealed class SyncDiagnosticsTests
{
    [Fact]
    public void Initial_State_Has_No_Failure()
    {
        var d = new SyncDiagnostics();

        Assert.Null(d.LastError);
        Assert.Null(d.LastFailure);
        Assert.Equal(0, d.FailCount);
        Assert.Equal(0, d.ConsecutiveFailures);
        Assert.Equal(0, d.SyncCount);
        Assert.False(d.IsRunning);
    }

    [Fact]
    public void MarkFailed_Records_Structured_Transient_Failure()
    {
        var d = new SyncDiagnostics();
        var before = DateTime.UtcNow;

        d.MarkFailed("BOOTSTRAP_FAILED", "网络不可达", transient: true);

        var f = Assert.IsType<SyncFailureRecord>(d.LastFailure);
        Assert.Equal("BOOTSTRAP_FAILED", f.ErrorCode);
        Assert.Equal("网络不可达", f.Message);
        Assert.True(f.Transient, "BOOTSTRAP_FAILED 应标记为可自动重试");
        Assert.InRange(f.OccurredAtUtc, before, DateTime.UtcNow);
        Assert.Equal("BOOTSTRAP_FAILED: 网络不可达", d.LastError);
        Assert.Equal(1, d.FailCount);
        Assert.Equal(1, d.ConsecutiveFailures);
    }

    [Fact]
    public void MarkFailed_With_Null_Code_Defaults_To_UNKNOWN()
    {
        var d = new SyncDiagnostics();

        d.MarkFailed(null, null, transient: false);

        var f = d.LastFailure!;
        Assert.Equal("UNKNOWN", f.ErrorCode);
        Assert.Null(f.Message);
        Assert.False(f.Transient);
        // LastError 回退为错误码本身。
        Assert.Equal("UNKNOWN", d.LastError);
    }

    [Fact]
    public void Permanent_Failure_Is_Marked_NonTransient()
    {
        var d = new SyncDiagnostics();

        d.MarkFailed("INVALID_SESSION", "会话失效", transient: false);

        Assert.False(d.LastFailure!.Transient, "会话失效应标记为永久失败，不可盲目重试");
        Assert.Equal("INVALID_SESSION", d.LastFailure.ErrorCode);
    }

    [Fact]
    public void Consecutive_Failures_Accumulate_Until_Success()
    {
        var d = new SyncDiagnostics();

        d.MarkFailed("SYNC_ERROR", "超时", transient: true);
        d.MarkFailed("SYNC_ERROR", "超时", transient: true);
        Assert.Equal(2, d.FailCount);
        Assert.Equal(2, d.ConsecutiveFailures);

        d.MarkSuccess(100);

        // 成功归零连续失败，但累计失败计数与最近失败记录保留（便于回溯）。
        Assert.Equal(0, d.ConsecutiveFailures);
        Assert.Equal(2, d.FailCount);
        Assert.Equal("SYNC_ERROR", d.LastFailure!.ErrorCode);
        Assert.Null(d.LastError);
        Assert.Equal(1, d.SyncCount);
        Assert.Equal(100, d.LastDurationMs);
    }

    [Fact]
    public void MarkSuccess_Resets_IsRunning_And_Clears_LastError()
    {
        var d = new SyncDiagnostics();
        d.MarkFailed("BOOTSTRAP_FAILED", "x", transient: true);

        d.MarkSuccess(10);

        Assert.False(d.IsRunning);
        Assert.Null(d.LastError);
        Assert.NotNull(d.LastSyncUtc);
    }
}