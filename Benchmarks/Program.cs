using BenchmarkDotNet.Running;

namespace Benchmarks;

internal static class Program
{
    private static void Main(string[] args)
    {
        BenchmarkRunner.Run<ProtocolCodecBenchmarks>();
        BenchmarkRunner.Run<SerializationBenchmarks>();
    }
}
