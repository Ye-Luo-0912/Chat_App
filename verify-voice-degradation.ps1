#Requires -Version 5.1
<#
.SYNOPSIS
    VOICE-MSG-2 三条降级路径的自动化验证脚本。
.DESCRIPTION
    分组运行三条降级路径的测试并通过命令行输出聚合报告：
      路径 1：录音超时自动收尾（VoiceRecorderTests.Record_ReachesMaxDuration_* / Record_AfterAutoFinalize_*）
      路径 2：播放下载失败防护（VoiceDegradationViewModelTests.PlayVoice_*）
      路径 3：上传失败恢复（VoiceDegradationViewModelTests.SendVoiceRecording_*）
    任一测试组失败即以非零退出码结束，便于 CI / 手动接入。
.PARAMETER SkipBuild
    跳过构建，直接复用现有产物（需已构建过）。
.PARAMETER Configuration
    构建/测试配置，默认 Debug。
.EXAMPLE
    .\verify-voice-degradation.ps1
    .\verify-voice-degradation.ps1 -SkipBuild
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path   # Chat_App 解决方案根
$sln = Join-Path $root "Chat_App.sln"
$unitTests = Join-Path $root "UnitTests\UnitTests.csproj"

function Invoke-TestGroup {
    param(
        [string]$Name,
        [string]$Path,
        [string]$Config,
        [string]$Filter
    )
    Write-Host ""
    Write-Host ("=== {0} ===" -f $Name) -ForegroundColor Cyan
    $output = & dotnet test $Path -c $Config --nologo --no-build --filter $Filter 2>&1 | Out-String
    $exit = $LASTEXITCODE
    Write-Host $output

    # 从汇总行解析失败数（兼容中/英文 locale）
    $failed = 0
    if ($output -match "失败:\s*(\d+)") { $failed = [int]$Matches[1] }
    elseif ($output -match "Failed:\s*(\d+)") { $failed = [int]$Matches[1] }

    $status = if ($exit -eq 0 -and $failed -eq 0) { "PASSED" } else { "FAILED" }
    return [pscustomobject]@{ 路径 = $Name; 状态 = $status; 失败数 = $failed; 退出码 = $exit }
}

Write-Host "VOICE-MSG-2 降级路径验证" -ForegroundColor Green
if (-not $SkipBuild) {
    Write-Host ">> 构建 $sln ..."
    & dotnet build $sln -c $Configuration -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "构建失败，退出码 $LASTEXITCODE" }
}

$results = @()
$results += Invoke-TestGroup "路径1: 录音超时自动收尾" $unitTests $Configuration `
    "FullyQualifiedName~VoiceRecorderTests.Record_ReachesMaxDuration_AutoFinalizesAndFiresAutoCompleted|FullyQualifiedName~VoiceRecorderTests.Record_AfterAutoFinalize_CanRestartFreshRecording"
$results += Invoke-TestGroup "路径2: 播放下载失败防护" $unitTests $Configuration `
    "FullyQualifiedName~VoiceDegradationViewModelTests.PlayVoice_"
$results += Invoke-TestGroup "路径3: 上传失败恢复" $unitTests $Configuration `
    "FullyQualifiedName~VoiceDegradationViewModelTests.SendVoiceRecording_"
$results += Invoke-TestGroup "聚合: 全部降级相关测试类" $unitTests $Configuration `
    "FullyQualifiedName~VoiceRecorderTests|FullyQualifiedName~VoiceDegradationViewModelTests"

Write-Host ""
Write-Host "──────────────────── 汇总 ────────────────────" -ForegroundColor Yellow
$results | Format-Table -AutoSize

if ($results | Where-Object { $_.状态 -eq "FAILED" } | Select-Object -First 1) {
    Write-Host "验证结果: FAILED" -ForegroundColor Red
    exit 1
} else {
    Write-Host "验证结果: 三条降级路径全部 PASSED" -ForegroundColor Green
    exit 0
}
