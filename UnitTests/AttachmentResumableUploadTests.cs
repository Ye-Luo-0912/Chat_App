using System.Net;
using System.Text;
using System.Text.Json;
using Chat_App.Services;
using ChatApp.Contracts.Http.Attachments;
using Core.Contracts.Attachments;
using Xunit;

namespace UnitTests;

/// <summary>
/// 附件分块断点续传上传测试（fake server）：
/// 分块顺序 PUT ?offset= 且按响应 received 对齐；传输中断后探针续传；
/// 服务端不支持分块（无 received 的 400）回退整包；S3 绝对 URL 直传跳过分块。
/// </summary>
public sealed class AttachmentResumableUploadTests
{
    private const long Chunk = 1L << 20; // 与 AttachmentApiService.ChunkSizeBytes 一致（1 MiB）
    private const long Total = 5 * (Chunk / 2); // 2.5 MiB → 3 块：1MB/1MB/0.5MB

    [Fact]
    public async Task ChunkedUpload_SplitsByChunk_AlignsReceived_AndConfirms()
    {
        var server = new FakeAttachmentServer(Total) { PresignUploadUrl = "/api/attachments/upload?ticket=t-1" };
        using var client = CreateClient(server);
        var service = new AttachmentApiService(client);
        var payload = server.Payload;
        var progress = new ProgressCollector();

        var result = await service.UploadAndConfirmAsync(
            new MemoryStream(payload), "application/octet-stream", Total, progress: progress);

        Assert.Equal(new long[] { 0, Chunk, 2 * Chunk }, server.ChunkOffsets);
        Assert.Equal(0, server.WholePuts);
        Assert.Equal(1, server.Confirms);
        Assert.Equal("att-1", result.AttachmentId);
        Assert.Equal(Total, result.SizeBytes);
        Assert.Equal(payload, server.Stored);
        // 进度按已接收/总长推进，最终到达 100%。
        Assert.Equal(Total, progress.Bytes.Last());
    }

    [Fact]
    public async Task ChunkedUpload_ConnectionDropDuringFirstChunk_ResumesFromServerOffset()
    {
        var server = new FakeAttachmentServer(Total)
        {
            PresignUploadUrl = "/api/attachments/upload?ticket=t-1",
            DropFirstChunkAfterBytes = Chunk / 2, // 首块传到 512KB 断开
        };
        using var client = CreateClient(server);
        var service = new AttachmentApiService(client);

        var result = await service.UploadAndConfirmAsync(
            new MemoryStream(server.Payload), "application/octet-stream", Total);

        // 首块 0（中断）→ 探针 → 从 512KB 续传。
        Assert.Equal(new long[] { 0, Chunk / 2, Chunk + Chunk / 2 }, server.ChunkOffsets);
        Assert.Equal(1, server.ProgressProbes);
        Assert.Equal(0, server.WholePuts);
        Assert.Equal(1, server.Confirms);
        Assert.Equal(server.Payload, server.Stored);
        Assert.Equal("att-1", result.AttachmentId);
    }

    [Fact]
    public async Task ChunkedUpload_ServerRejectsChunks_FallsBackToWholeFile()
    {
        var server = new FakeAttachmentServer(Total)
        {
            PresignUploadUrl = "/api/attachments/upload?ticket=t-1",
            ChunkingSupported = false, // S3 后端/旧服务端：offset PUT 一律 400 且无 received
        };
        using var client = CreateClient(server);
        var service = new AttachmentApiService(client);

        var result = await service.UploadAndConfirmAsync(
            new MemoryStream(server.Payload), "application/octet-stream", Total);

        Assert.Single(server.ChunkOffsets);
        Assert.Equal(1, server.WholePuts);
        Assert.Equal(1, server.Confirms);
        Assert.Equal(server.Payload, server.Stored);
        Assert.Equal("att-1", result.AttachmentId);
    }

    [Fact]
    public async Task S3AbsoluteUploadUrl_PutsDirectly_WithoutChunking()
    {
        var server = new FakeAttachmentServer(Total)
        {
            PresignUploadUrl = "https://s3.test/bucket/att-1?X-Amz-Signature=abc",
        };
        using var authClient = CreateClient(server);
        using var s3Client = new HttpClient(server);
        var service = new AttachmentApiService(authClient, s3Client);

        var result = await service.UploadAndConfirmAsync(
            new MemoryStream(server.Payload), "application/octet-stream", Total);

        Assert.Empty(server.ChunkOffsets);
        Assert.Equal(1, server.WholePuts);
        Assert.Equal("s3.test", server.LastPutHost);
        Assert.DoesNotContain("offset", server.LastPutQuery);
        Assert.Equal(server.Payload, server.Stored);
        Assert.Equal(1, server.Confirms);
        Assert.Equal("att-1", result.AttachmentId);
    }

    private static HttpClient CreateClient(FakeAttachmentServer server)
        => new(server)
        {
            BaseAddress = new Uri("https://chat.test")
        };

    /// <summary>同步收集进度（Progress&lt;T&gt; 异步投递不适合断言）。</summary>
    private sealed class ProgressCollector : IProgress<AttachmentUploadProgress>
    {
        public List<long> Bytes { get; } = [];
        public void Report(AttachmentUploadProgress value) => Bytes.Add(value.BytesTransferred);
    }

    /// <summary>伪造 presign/PUT/progress/confirm 端点的服务器，按 offset 落盘字节。</summary>
    private sealed class FakeAttachmentServer(long total) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public byte[] Payload { get; } = CreatePayload(total);
        public byte[] Stored = new byte[total];
        public string PresignUploadUrl { get; init; } = "/api/attachments/upload?ticket=t-1";

        public bool ChunkingSupported { get; init; } = true;
        public long? DropFirstChunkAfterBytes { get; init; }
        private bool _dropped;

        public List<long> ChunkOffsets { get; } = [];
        public int WholePuts { get; private set; }
        public int ProgressProbes { get; private set; }
        public int Confirms { get; private set; }
        public long ServerReceived { get; private set; }
        public string? LastPutHost { get; private set; }
        public string LastPutQuery { get; private set; } = string.Empty;

        private static byte[] CreatePayload(long total)
        {
            var bytes = new byte[total];
            new Random(20260905).NextBytes(bytes);
            return bytes;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = uri.AbsolutePath;
            LastPutHost = request.Method == HttpMethod.Put ? uri.Host : LastPutHost;
            LastPutQuery = request.Method == HttpMethod.Put ? uri.Query : LastPutQuery;

            if (request.Method == HttpMethod.Post && path == "/api/attachments/presign")
            {
                var presign = new AttachmentPresignResponse
                {
                    AttachmentId = "att-1",
                    UploadUrl = PresignUploadUrl,
                    DownloadPath = "/api/attachments/att-1/download",
                    ObjectKey = "9101/att-1",
                    Ticket = "ticket-1",
                    ExpiresAt = new DateTimeOffset(2099, 8, 5, 3, 0, 0, TimeSpan.Zero),
                    Deduplicated = false,
                };
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(presign, JsonOptions));
            }

            if (request.Method == HttpMethod.Get && path == "/api/attachments/upload/progress")
            {
                ProgressProbes++;
                return Json(HttpStatusCode.OK, $$"""{"received":{{ServerReceived}}}""");
            }

            if (request.Method == HttpMethod.Post && path == "/api/attachments/confirm")
            {
                Confirms++;
                return Json(HttpStatusCode.OK, """
                    {"attachmentId":"att-1","downloadPath":"/api/attachments/att-1/download","objectKey":"9101/att-1","status":"Scanning"}
                    """);
            }

            if (request.Method == HttpMethod.Put)
            {
                return await HandlePutAsync(request, uri, cancellationToken).ConfigureAwait(false);
            }

            return Json(HttpStatusCode.NotFound, "{}");
        }

        private async Task<HttpResponseMessage> HandlePutAsync(
            HttpRequestMessage request, Uri uri, CancellationToken cancellationToken)
        {
            var offsetValue = QueryValue(uri, "offset");
            await using var body = await request.Content!.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            if (offsetValue is null)
            {
                // 整包 PUT：全量落盘。
                WholePuts++;
                using var whole = new MemoryStream();
                await body.CopyToAsync(whole, cancellationToken).ConfigureAwait(false);
                var bytes = whole.ToArray();
                Assert.Equal(Payload.Length, bytes.Length);
                Array.Copy(bytes, Stored, bytes.Length);
                ServerReceived = bytes.Length;
                return Json(HttpStatusCode.OK, "{}");
            }

            var offset = long.Parse(offsetValue);
            ChunkOffsets.Add(offset);

            if (!ChunkingSupported)
            {
                // 模拟不支持分块的后端：明确 400 且响应不含 received。
                return Json(HttpStatusCode.BadRequest,
                    """{"message":"S3 直传不支持分块续传，请整包 PUT 预签名 URL"}""");
            }

            if (DropFirstChunkAfterBytes is { } dropAt && !_dropped)
            {
                // 服务端已收到前 dropAt 字节后连接中断：状态已推进但响应丢失。
                var received = new byte[dropAt];
                var n = await body.ReadAsync(received.AsMemory(), cancellationToken).ConfigureAwait(false);
                Array.Copy(received, 0, Stored, offset, n);
                ServerReceived = offset + n;
                _dropped = true;
                throw new HttpRequestException("模拟连接中断");
            }

            var expected = (int)Math.Min(Chunk, Payload.Length - offset);
            var chunk = new byte[expected];
            var read = 0;
            while (read < expected)
            {
                var n = await body.ReadAsync(chunk.AsMemory(read), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                    break;
                read += n;
            }

            Array.Copy(chunk, 0, Stored, offset, read);
            ServerReceived = offset + read;
            return Json(HttpStatusCode.OK, $$"""{"received":{{ServerReceived}}}""");
        }

        private static string? QueryValue(Uri uri, string name)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] == name)
                    return Uri.UnescapeDataString(kv[1]);
            }

            return null;
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}
