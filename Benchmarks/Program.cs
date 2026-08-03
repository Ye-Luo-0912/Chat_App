using BenchmarkDotNet.Running;

namespace Benchmarks;

internal static class Program
{
    private static void Main(string[] args)
    {
        // 支持 CLI 参数：--job short --exporters json --artifacts-path <dir>（CI 性能门禁用）。
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
