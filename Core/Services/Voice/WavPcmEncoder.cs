using System;
using System.Buffers.Binary;
using System.IO;

namespace Core.Services.Voice;

/// <summary>
/// 将 16-bit 有符号 PCM 帧封装为 RIFF/WAVE 容器（跨平台、无外部依赖、字节级确定）。
/// 产物为标准 PCM WAV：fmt 块（PCM=1） + data 块，data 长度在收尾时回填。
/// VOICE-MSG-2 采用 codec=pcm、container=wav。
/// </summary>
public static class WavPcmEncoder
{
    /// <summary>WAV RIFF 头固定长度（44 字节：RIFF 头 + fmt 块 + data 头）。</summary>
    public const int HeaderLength = 44;

    /// <summary>一次写入 data 的最小块大小（字节）；不足则按实际长度回填。</summary>
    private const int MinBlockBytes = 1024 * 64;

    /// <summary>语音波形峰值包络的默认桶数（每桶 1 字节，包络天然上限 48 字节）。</summary>
    public const int WaveformPeakBucketCount = 48;

    /// <summary>16-bit PCM 满幅绝对值（|short.MinValue|）。</summary>
    private const int FullScaleAmplitude = 32_768;

    /// <summary>
    /// 计算 16-bit LE PCM 的峰值包络（VOICE-MSG-2 波形）：
    /// 将样本序列按固定桶数等分，取每桶内样本绝对值的最大值，再线性归一化到 0–255
    /// （0 = 数字静音，255 = 满幅 -32768/32767）。纯函数、字节级确定：同输入同输出。
    /// 多声道交错样本按样本序号一并分桶（声道间并入同一桶取最大）。
    /// 返回长度恒为 <paramref name="bucketCount"/>（样本数为 0 时返回空数组）。
    /// </summary>
    public static byte[] ComputePeakEnvelope(ReadOnlySpan<byte> pcm16, int bucketCount = WaveformPeakBucketCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bucketCount);

        var totalSamples = pcm16.Length / 2; // 不足 2 字节的尾部截断
        if (totalSamples == 0)
            return [];

        var peaks = new byte[bucketCount];
        // 单趟扫描：桶边界由样本序号线性映射确定，桶内只保留绝对值最大者。
        // 使用 long 乘法避免 (i * bucketCount) 在长录音下溢出 int。
        var maxAbs = 0;
        var currentBucket = 0;
        for (var i = 0; i < totalSamples; i++)
        {
            var bucket = (int)((long)i * bucketCount / totalSamples);
            if (bucket != currentBucket)
            {
                peaks[currentBucket] = NormalizePeak(maxAbs);
                currentBucket = bucket;
                maxAbs = 0;
            }

            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm16[(i * 2)..]);
            var abs = sample < 0 ? -sample : sample; // -32768 → 32768，int 域内安全
            if (abs > maxAbs)
                maxAbs = abs;
        }

        peaks[currentBucket] = NormalizePeak(maxAbs);
        return peaks;
    }

    /// <summary>满幅归一化：abs ∈ [0, 32768] → [0, 255]，四舍五入（half-up）整数运算，无浮点误差。</summary>
    private static byte NormalizePeak(int abs)
        => (byte)Math.Min(byte.MaxValue, (abs * 255 + FullScaleAmplitude / 2) / FullScaleAmplitude);

    /// <summary>
    /// 将 PCM 帧流写入目标流，并返回已写入的 data 字节数。
    /// 先写带占位长度的头部，随后逐块写入 PCM 数据，最后回填 data 长度与 RIFF 大小。
    /// 返回的 data 字节数即实际音频负载长度。
    /// </summary>
    public static long WriteWav(
        Stream destination,
        Stream pcmSource,
        int sampleRateHz,
        short channels,
        int bufferSize = MinBlockBytes)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(pcmSource);
        if (sampleRateHz <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRateHz));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));

        long dataBytes = 0;
        var header = CreateHeader(sampleRateHz, channels, dataLength: 0);
        destination.Write(header, 0, header.Length);

        var buffer = new byte[bufferSize];
        int read;
        while ((read = pcmSource.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            dataBytes += read;
        }

        // 回填 data 长度与 RIFF 大小（RIFF size = 36 + dataBytes）。
        destination.Position = 4;
        Span<byte> riffSize = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(riffSize, (int)(36 + dataBytes));
        destination.Write(riffSize);

        destination.Position = 40;
        Span<byte> dataSize = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(dataSize, (int)dataBytes);
        destination.Write(dataSize);

        destination.Position = destination.Length;
        destination.Flush();
        return dataBytes;
    }

    /// <summary>生成 44 字节 PCM WAV 头部（data 长度由调用方传入，后期可回填）。</summary>
    public static byte[] CreateHeader(int sampleRateHz, short channels, long dataLength)
    {
        var blockAlign = (short)(channels * 2); // 16-bit PCM
        var byteRate = sampleRateHz * blockAlign;
        var header = new byte[HeaderLength];

        // RIFF/WAVE
        WriteAscii(header, 0, "RIFF");
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), (int)(36 + dataLength));
        WriteAscii(header, 8, "WAVE");

        // fmt 块（16 字节，PCM=1）
        WriteAscii(header, 12, "fmt ");
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), sampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34), 16); // bits per sample

        // data 块
        WriteAscii(header, 36, "data");
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), (int)dataLength);
        return header;
    }

    private static void WriteAscii(Span<byte> destination, int offset, string ascii)
    {
        for (var i = 0; i < ascii.Length; i++)
            destination[offset + i] = (byte)ascii[i];
    }
}