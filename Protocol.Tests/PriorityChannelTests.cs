using Chat_App.Infrastructure.Networking;
using Core.Models;
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace Protocol.Tests;

/// <summary>
/// 高优发送通道验收（真实 TCP 回环）：
/// 服务端慢读制造持续发送窗口（大帧流占用发送循环），期间普通帧与高优帧先后入队，
/// 发送循环恢复后必须先排空高优通道——服务端观测到的高优 marker 必先于普通 marker。
/// 无优先级实现下，普通帧（先入队）必然先发，测试即可区分两种行为。
/// </summary>
public class PriorityChannelTests
{
    private static readonly byte[] NormalMarker = { 0x4E, 0x11, 0x22, 0x33, 0x44 };   // 'N' 前缀：普通
    private static readonly byte[] PriorityMarker = { 0x50, 0xAA, 0xBB, 0xCC, 0xDD }; // 'P' 前缀：高优

    /// <summary>拥有 ArrayPool 内存的 IMemoryOwner，供 SendPriorityAsync 使用。</summary>
    private sealed class PooledOwner : IMemoryOwner<byte>
    {
        private byte[] _buffer;

        public PooledOwner(ReadOnlySpan<byte> data)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(data.Length);
            data.CopyTo(_buffer);
            Memory = _buffer.AsMemory(0, data.Length);
        }

        public Memory<byte> Memory { get; }

        public void Dispose()
        {
            if (_buffer is not null)
            {
                _buffer.AsSpan(0, Memory.Length).Clear();
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = null!;
            }
        }
    }

    /// <summary>慢读服务器：每累计 16KB 延迟 1ms，收集全部字节后按 marker 扫描。</summary>
    private sealed class SlowReaderServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<byte> _received = new();
        private readonly Lock _lock = new();
        private readonly Task _runTask;

        public SlowReaderServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _runTask = Task.Run(AcceptAndReadAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>返回普通 marker 与高优 marker 各自首次出现的字节位置；未出现为 -1。</summary>
        public (int NormalIndex, int PriorityIndex) GetMarkerIndexes()
        {
            byte[] bytes;
            lock (_lock) bytes = _received.ToArray();
            return (IndexOf(bytes, NormalMarker), IndexOf(bytes, PriorityMarker));
        }

        private static int IndexOf(byte[] haystack, byte[] needle)
        {
            for (var i = 0; i <= haystack.Length - needle.Length; i++)
            {
                var match = true;
                for (var j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private async Task AcceptAndReadAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                client.NoDelay = true;
                var stream = client.GetStream();
                var buffer = new byte[16 * 1024];
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, _cts.Token);
                    if (read <= 0) return;
                    lock (_lock) _received.AddRange(buffer.AsSpan(0, read).ToArray());
                    // 慢读：每 16KB 延迟 1ms，拉长发送循环的占用窗口。
                    await Task.Delay(1, _cts.Token);
                }
            }
            catch (OperationCanceledException) { }
        }

        public async Task WaitForCompletionAsync() => await _runTask.WaitAsync(TimeSpan.FromSeconds(15));

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
        }
    }

    [Fact]
    public async Task Priority_Frame_ByPasses_Queued_Normal_Frames()
    {
        using var server = new SlowReaderServer();
        using var client = new TcpClientExample();

        await client.ConnectAsync(new ServerEndpoint
        {
            ServerIpAddress = "127.0.0.1",
            ServerPort = server.Port
        });

        // 大帧流（20 × 256KB = 5MB）：慢读下发送循环被持续占用约 320ms。
        // 全部先入队，保证"发送循环忙"是稳定状态，而非单帧竞态。
        var bigTasks = Enumerable.Range(0, 20)
            .Select(_ => client.SendAsync(new byte[256 * 1024]))
            .ToArray();

        // 等待大帧流开始发送（发送循环已取帧），随后普通帧与高优帧先后入队。
        await Task.Delay(60);
        var normalTask = client.SendAsync(NormalMarker.ToArray());
        var priorityTask = client.SendPriorityAsync(new PooledOwner(PriorityMarker));

        // 大帧流全部发送完成：此时两帧均已在队列，发送循环恢复后先排空高优通道。
        await Task.WhenAll(bigTasks).WaitAsync(TimeSpan.FromSeconds(15));
        await Task.WhenAll(normalTask, priorityTask);

        // 断开客户端以结束服务端读循环，再确认服务端收到全部字节。
        client.Disconnect("test done");
        await server.WaitForCompletionAsync();

        var (normalIndex, priorityIndex) = server.GetMarkerIndexes();
        Assert.True(priorityIndex >= 0, "高优帧必须被发送并到达服务端");
        Assert.True(normalIndex >= 0, "普通帧必须被发送并到达服务端");
        Assert.True(priorityIndex < normalIndex,
            $"高优帧应插队先发（高优 @{priorityIndex}，普通 @{normalIndex}）");
    }
}
