using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Contracts.Attachments;
using ChatApp.Contracts.Http.Attachments;
using Core.Interfaces;
using Chat_App.Infrastructure.Networking;
using Serilog;

namespace Chat_App.Services;

public sealed class AttachmentApiService : IAttachmentClientService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HttpClient SharedS3UploadClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    /// <summary>
    /// 分块续传的单块大小；也作为启用分块的阈值——小文件单请求即可，不值得多一次往返。
    /// </summary>
    internal static readonly long ChunkSizeBytes = 1 << 20; // 1 MiB

    private readonly HttpClient _httpClient;
    private readonly HttpClient _s3UploadClient;

    public AttachmentApiService(HttpClient httpClient, HttpClient? s3UploadClient = null)
    {
        _httpClient = httpClient;
        _s3UploadClient = s3UploadClient ?? SharedS3UploadClient;
    }

    public async Task<AttachmentPresignResponse> PresignAsync(
        AttachmentPresignRequest request,
        CancellationToken ct = default)
    {
        using var response = await _httpClient
            .PostAsJsonAsync("/api/attachments/presign", request, JsonOptions, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, "预签名", ct).ConfigureAwait(false);
        var body = await response.Content
            .ReadFromJsonAsync<AttachmentPresignResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        return body ?? throw new InvalidOperationException("预签名响应为空");
    }

    public async Task UploadAsync(
        AttachmentPresignResponse ticket,
        Stream content,
        string contentType,
        long contentLength,
        IProgress<AttachmentUploadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        if (string.IsNullOrWhiteSpace(ticket.UploadUrl))
            throw new ArgumentException("UploadUrl 为空");

        await using var progressStream = new ProgressReadStream(
            content,
            contentLength,
            bytes => progress?.Report(new AttachmentUploadProgress
            {
                AttachmentId = ticket.AttachmentId,
                BytesTransferred = bytes,
                TotalBytes = contentLength
            }));

        using var streamContent = new StreamContent(progressStream);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        if (contentLength > 0)
            streamContent.Headers.ContentLength = contentLength;

        // 流式上传内容不可自动缓冲重放。为 401 重试提供请求体重建工厂：
        // 重置底层 seekable 流位置，重新包装 ProgressReadStream + StreamContent。
        Func<CancellationToken, Task<HttpContent?>> replayFactory = _ =>
        {
            if (content.CanSeek)
                content.Position = 0;
            var ps = new ProgressReadStream(content, contentLength, bytes => progress?.Report(new AttachmentUploadProgress
            {
                AttachmentId = ticket.AttachmentId,
                BytesTransferred = bytes,
                TotalBytes = contentLength
            }));
            var sc = new StreamContent(ps);
            sc.Headers.ContentType = new MediaTypeHeaderValue(
                string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
            if (contentLength > 0)
                sc.Headers.ContentLength = contentLength;
            return Task.FromResult<HttpContent?>(sc);
        };

        HttpResponseMessage response;
        if (Uri.TryCreate(ticket.UploadUrl, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            && !IsSameAuthority(absolute, _httpClient.BaseAddress))
        {
            // S3 预签名：不带 Bearer，直接 PUT 到绝对 URL。
            using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, absolute)
            {
                Content = streamContent
            };
            ApplyUploadHeaders(uploadRequest, ticket.UploadHeaders);
            response = await _s3UploadClient.SendAsync(uploadRequest, ct).ConfigureAwait(false);
        }
        else
        {
            var relative = ticket.UploadUrl;
            if (Uri.TryCreate(ticket.UploadUrl, UriKind.Absolute, out var absApi))
                relative = absApi.PathAndQuery;

            using var putRequest = new HttpRequestMessage(HttpMethod.Put, relative);
            putRequest.Content = streamContent;
            ApplyUploadHeaders(putRequest, ticket.UploadHeaders);
            // 提供请求体重建工厂，流式上传遇 401 时由拦截器重建流而非发送空 body。
            putRequest.Options.Set(RequestOptionKeys.ReplayFactory, replayFactory);

            response = await _httpClient
                .SendAsync(putRequest, ct)
                .ConfigureAwait(false);
        }

        using (response)
        {
            await EnsureSuccessAsync(response, "上传", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 上传编排：鉴权 API 且内容超过单块阈值时走分块断点续传（PUT ?offset=），
    /// S3 直传（绝对 URL）或小文件回退整包。分块路径读响应 received 对齐服务端权威偏移，
    /// 传输中断后先探针 progress 再续传；服务端不支持分块时整体回退整包 PUT。
    /// </summary>
    private async Task UploadWithResumeAsync(
        AttachmentPresignResponse ticket,
        Stream content,
        string contentType,
        long contentLength,
        IProgress<AttachmentUploadProgress>? progress,
        int maxAttempts,
        CancellationToken ct)
    {
        // S3 预签名 URL：不经鉴权 API，无 offset 语义，整包直传。
        var isS3Direct = Uri.TryCreate(ticket.UploadUrl, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            && !IsSameAuthority(absolute, _httpClient.BaseAddress);

        // 分块按 offset 切流，要求可 seek；小文件单请求即完成，不引入额外往返。
        var resumable = !isS3Direct && content.CanSeek && contentLength > ChunkSizeBytes;
        if (!resumable)
        {
            await UploadAsync(ticket, content, contentType, contentLength, progress, ct).ConfigureAwait(false);
            return;
        }

        await UploadChunkedAsync(ticket, content, contentType, contentLength, progress, maxAttempts, ct)
            .ConfigureAwait(false);
    }

    private async Task UploadChunkedAsync(
        AttachmentPresignResponse ticket,
        Stream content,
        string contentType,
        long contentLength,
        IProgress<AttachmentUploadProgress>? progress,
        int maxAttempts,
        CancellationToken ct)
    {
        var uploadPath = ResolveRelativeUploadPath(ticket.UploadUrl);
        var mediaType = new MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        long offset = 0;
        var attempts = 0;

        while (offset < contentLength)
        {
            ct.ThrowIfCancellationRequested();
            attempts++;
            var chunkLength = Math.Min(ChunkSizeBytes, contentLength - offset);

            using var request = new HttpRequestMessage(HttpMethod.Put, WithOffsetQuery(uploadPath, offset));
            request.Content = CreateChunkContent(content, offset, chunkLength, mediaType, progress, ticket.AttachmentId, contentLength);
            // 分块流式内容同样为 401 重试提供重建工厂（重新 seek 到块起点）。
            request.Options.Set(RequestOptionKeys.ReplayFactory, _ =>
            {
                request.Content = CreateChunkContent(
                    content, offset, chunkLength, mediaType, progress, ticket.AttachmentId, contentLength);
                return Task.FromResult<HttpContent?>(request.Content);
            });

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRetryable(ex) && attempts < maxAttempts)
            {
                // 传输中断：探针取服务端权威 offset 后续传。
                var (fallBack, resumed) = await ResumeOffsetAsync(ticket.Ticket, ct).ConfigureAwait(false);
                if (fallBack)
                {
                    await FallBackToWholeAsync(ticket, content, contentType, contentLength, progress, ct)
                        .ConfigureAwait(false);
                    return;
                }

                offset = resumed;
                Log.Information("附件分块上传中断，从 {Offset} 续传 Attempt={Attempt}/{Max}", offset, attempts, maxAttempts);
                continue;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    // 响应 received 是服务端权威已接收字节数，以此对齐下一块。
                    offset = TryParseReceived(body) ?? (offset + chunkLength);
                    progress?.Report(new AttachmentUploadProgress
                    {
                        AttachmentId = ticket.AttachmentId,
                        BytesTransferred = offset,
                        TotalBytes = contentLength
                    });
                    continue;
                }

                if (TryParseReceived(body) is { } authoritative && attempts < maxAttempts)
                {
                    // offset 错位（400 + received）：按权威值对齐后续传。
                    offset = authoritative;
                    continue;
                }

                if (IsRetryableStatus(response.StatusCode) && attempts < maxAttempts)
                {
                    var (fallBack, resumed) = await ResumeOffsetAsync(ticket.Ticket, ct).ConfigureAwait(false);
                    if (fallBack)
                    {
                        await FallBackToWholeAsync(ticket, content, contentType, contentLength, progress, ct)
                            .ConfigureAwait(false);
                        return;
                    }

                    offset = resumed;
                    continue;
                }

                // 服务端不支持分块（无 received 的 4xx，如 S3 后端/旧版本）→ 回退整包。
                Log.Information("附件分块上传不被接受 ({Status})，回退整包上传", (int)response.StatusCode);
                await FallBackToWholeAsync(ticket, content, contentType, contentLength, progress, ct)
                    .ConfigureAwait(false);
                return;
            }
        }
    }

    /// <summary>探针服务端权威偏移；端点不可用时请求整包回退。</summary>
    private async Task<(bool FallBack, long Offset)> ResumeOffsetAsync(string ticket, CancellationToken ct)
    {
        var received = await ProbeReceivedAsync(ticket, ct).ConfigureAwait(false);
        return received is null ? (true, 0) : (false, received.Value);
    }

    private async Task FallBackToWholeAsync(
        AttachmentPresignResponse ticket,
        Stream content,
        string contentType,
        long contentLength,
        IProgress<AttachmentUploadProgress>? progress,
        CancellationToken ct)
    {
        if (content.CanSeek)
            content.Position = 0;
        await UploadAsync(ticket, content, contentType, contentLength, progress, ct).ConfigureAwait(false);
    }

    private static HttpContent CreateChunkContent(
        Stream content, long offset, long chunkLength, MediaTypeHeaderValue mediaType,
        IProgress<AttachmentUploadProgress>? progress, string attachmentId, long totalBytes)
    {
        content.Position = offset;
        var slice = new ProgressReadStream(
            new OffsetSliceStream(content, chunkLength),
            chunkLength,
            bytes => progress?.Report(new AttachmentUploadProgress
            {
                AttachmentId = attachmentId,
                BytesTransferred = offset + bytes,
                TotalBytes = totalBytes
            }));
        var streamContent = new StreamContent(slice);
        streamContent.Headers.ContentType = mediaType;
        streamContent.Headers.ContentLength = chunkLength;
        return streamContent;
    }

    /// <summary>
    /// 进度探针：返回服务端权威已接收字节数；端点不可用（旧服务端）返回 null，调用方回退整包。
    /// </summary>
    private async Task<long?> ProbeReceivedAsync(string ticket, CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync(
                $"/api/attachments/upload/progress?ticket={Uri.EscapeDataString(ticket)}", ct)
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? TryParseReceived(body) : null;
    }

    private static string ResolveRelativeUploadPath(string uploadUrl)
        => Uri.TryCreate(uploadUrl, UriKind.Absolute, out var absApi)
            ? absApi.PathAndQuery
            : uploadUrl;

    private static string WithOffsetQuery(string uploadPath, long offset)
        => uploadPath.Contains('?')
            ? $"{uploadPath}&offset={offset}"
            : $"{uploadPath}?offset={offset}";

    private static long? TryParseReceived(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("received", out var received)
                   && received.TryGetInt64(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>是否为可重试的 HTTP 状态：408/429/5xx。</summary>
    private static bool IsRetryableStatus(System.Net.HttpStatusCode statusCode)
        => statusCode == System.Net.HttpStatusCode.RequestTimeout
           || statusCode == System.Net.HttpStatusCode.TooManyRequests
           || (int)statusCode >= 500;

    public async Task<ConfirmAttachmentResponse> ConfirmAsync(
        ConfirmAttachmentRequest request,
        CancellationToken ct = default)
    {
        using var response = await _httpClient
            .PostAsJsonAsync("/api/attachments/confirm", request, JsonOptions, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, "确认附件", ct).ConfigureAwait(false);
        var body = await response.Content
            .ReadFromJsonAsync<ConfirmAttachmentResponse>(JsonOptions, ct)
            .ConfigureAwait(false);
        return body ?? throw new InvalidOperationException("确认响应为空");
    }

    public async Task<AttachmentUploadResult> UploadAndConfirmAsync(
        Stream content,
        string contentType,
        long contentLength,
        string? originalName = null,
        string? clientAttachmentId = null,
        IProgress<AttachmentUploadProgress>? progress = null,
        int maxAttempts = 3,
        string? sha256 = null,
        CancellationToken ct = default)
    {
        if (contentLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(contentLength));
        maxAttempts = Math.Clamp(maxAttempts, 1, 5);

        if (maxAttempts > 1 && !content.CanSeek)
            throw new ArgumentException("非 seekable 流不支持重试，请设置 maxAttempts=1 或传入可 seek 的流", nameof(content));

        AttachmentPresignResponse? ticket = null;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (content.CanSeek)
                    content.Position = 0;

                ticket = await PresignAsync(
                        new AttachmentPresignRequest
                        {
                            ContentType = contentType,
                            ContentLength = contentLength,
                            OriginalName = originalName,
                            ClientAttachmentId = clientAttachmentId,
                            Sha256 = sha256
                        },
                        ct)
                    .ConfigureAwait(false);

                if (ticket.Deduplicated)
                {
                    // 服务端已持有相同 SHA-256 内容：免上传，直接进入确认。
                    Log.Information("附件秒传命中，跳过上传 AttachmentId={AttachmentId} ContentLength={Length}",
                        ticket.AttachmentId, contentLength);
                }
                else
                {
                    await UploadWithResumeAsync(ticket, content, contentType, contentLength, progress, maxAttempts, ct)
                        .ConfigureAwait(false);
                }

                var confirmed = await ConfirmAsync(
                        new ConfirmAttachmentRequest
                        {
                            ObjectKey = ticket.ObjectKey,
                            Ticket = ticket.Ticket,
                            AttachmentId = ticket.AttachmentId
                        },
                        ct)
                    .ConfigureAwait(false);

                return new AttachmentUploadResult
                {
                    AttachmentId = confirmed.AttachmentId,
                    DownloadPath = confirmed.DownloadPath,
                    ObjectKey = confirmed.ObjectKey,
                    ContentType = contentType,
                    SizeBytes = contentLength,
                    OriginalName = originalName
                };
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
            {
                // HttpClient 内部超时（用户未取消）：视为可重试的超时。
                lastError = new TimeoutException("上传超时", ex);
                if (ticket is not null)
                {
                    await TryAbandonQuietlyAsync(ticket.AttachmentId, CancellationToken.None)
                        .ConfigureAwait(false);
                    ticket = null;
                }
                if (attempt < maxAttempts)
                {
                    Log.Warning(ex, "附件上传超时，准备重试 Attempt={Attempt}/{Max}", attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                if (ticket is not null)
                    await TryAbandonQuietlyAsync(ticket.AttachmentId, CancellationToken.None)
                        .ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < maxAttempts)
            {
                lastError = ex;
                Log.Warning(ex, "附件上传失败，准备重试 Attempt={Attempt}/{Max}", attempt, maxAttempts);
                if (ticket is not null)
                {
                    await TryAbandonQuietlyAsync(ticket.AttachmentId, CancellationToken.None)
                        .ConfigureAwait(false);
                    ticket = null;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 不可重试错误，或最后一次失败：同样放弃服务端临时对象，避免残留。
                lastError = ex;
                Log.Warning(ex, "附件上传失败，放弃重试 Attempt={Attempt}/{Max}", attempt, maxAttempts);
                if (ticket is not null)
                {
                    await TryAbandonQuietlyAsync(ticket.AttachmentId, CancellationToken.None)
                        .ConfigureAwait(false);
                    ticket = null;
                }
            }
        }

        throw lastError ?? new InvalidOperationException("附件上传失败");
    }

    /// <summary>
    /// 是否可重试：仅超时、网络中断、408 请求超时、429 限流、5xx 服务端故障。
    /// 400/401（含刷新失败）/403/413/不支持类型等业务性错误不重试。
    /// </summary>
    private static bool IsRetryable(Exception ex)
    {
        switch (ex)
        {
            case HttpRequestException hre when hre.StatusCode is System.Net.HttpStatusCode st:
                return st == System.Net.HttpStatusCode.RequestTimeout
                    || st == System.Net.HttpStatusCode.TooManyRequests
                    || (int)st >= 500;
            case HttpRequestException:
                // 无状态码：连接中断/网络不可达。
                return true;
            case TimeoutException:
                return true;
            case IOException:
                // 传输流中断（网络断开）。
                return true;
            default:
                return false;
        }
    }

    public async Task AbandonAsync(string attachmentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(attachmentId))
            throw new ArgumentException("attachmentId 不能为空");

        using var response = await _httpClient
            .PostAsync($"/api/attachments/{Uri.EscapeDataString(attachmentId)}/abandon", content: null, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, "放弃附件", ct).ConfigureAwait(false);
    }

    public async Task<AttachmentDownloadResult> DownloadAsync(
        string attachmentIdOrHint,
        long? rangeFrom = null,
        long? rangeTo = null,
        CancellationToken ct = default)
    {
        var path = ResolveDownloadPath(attachmentIdOrHint);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (rangeFrom is long from)
        {
            request.Headers.Range = rangeTo is long to
                ? new RangeHeaderValue(from, to)
                : new RangeHeaderValue(from, null);
        }

        var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode
            && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            response.Dispose();
            throw new HttpRequestException($"下载失败 ({(int)response.StatusCode}): {error}");
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var length = response.Content.Headers.ContentLength;
        var fileName = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');

        return new AttachmentDownloadResult
        {
            Content = new HttpResponseStream(stream, response),
            ContentType = contentType,
            ContentLength = length,
            FileName = fileName,
            IsPartialContent = response.StatusCode == System.Net.HttpStatusCode.PartialContent
        };
    }

    private async Task TryAbandonQuietlyAsync(string attachmentId, CancellationToken ct)
    {
        try
        {
            await AbandonAsync(attachmentId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "放弃附件失败（可忽略）AttachmentId={Id}", attachmentId);
        }
    }

    private static string ResolveDownloadPath(string attachmentIdOrHint)
    {
        if (string.IsNullOrWhiteSpace(attachmentIdOrHint))
            throw new ArgumentException("下载目标为空");

        var value = attachmentIdOrHint.Trim();
        if (value.StartsWith("/api/attachments/", StringComparison.OrdinalIgnoreCase))
            return value;
        if (value.Contains("/download", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/content", StringComparison.OrdinalIgnoreCase))
        {
            return value.StartsWith('/') ? value : "/" + value;
        }

        return $"/api/attachments/{Uri.EscapeDataString(value)}/download";
    }

    private static bool IsSameAuthority(Uri absolute, Uri? baseAddress)
    {
        if (baseAddress is null) return false;
        return string.Equals(absolute.Host, baseAddress.Host, StringComparison.OrdinalIgnoreCase)
               && absolute.Port == baseAddress.Port;
    }

    private static void ApplyUploadHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || request.Content is null)
            return;

        foreach (var (name, value) in headers)
        {
            if (name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
            else
            {
                request.Headers.Remove(name);
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException($"{operation}失败 ({(int)response.StatusCode}): {body}");
    }

    /// <summary>只读切片流：从底层流当前位置最多读 <paramref name="length"/> 字节（一个分块）。</summary>
    private sealed class OffsetSliceStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _length;
        private long _remaining;

        public OffsetSliceStream(Stream inner, long length)
        {
            _inner = inner;
            _length = length;
            _remaining = length;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _length - _remaining;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= n;
            return n;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => await ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken)
                .ConfigureAwait(false);
            _remaining -= n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ProgressReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _total;
        private const long MinReportIntervalTicks = 500000; // 50ms

        private readonly Action<long> _onProgress;
        private long _read;
        private long _lastReportTicks;

        private void MaybeReportProgress()
        {
            var now = DateTime.UtcNow.Ticks;
            if (now - _lastReportTicks >= MinReportIntervalTicks || _read >= _total)
            {
                _lastReportTicks = now;
                _onProgress(_read);
            }
        }

        public ProgressReadStream(Stream inner, long total, Action<long> onProgress)
        {
            _inner = inner;
            _total = total;
            _onProgress = onProgress;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _total > 0 ? _total : _inner.Length;
        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            if (n > 0)
            {
                _read += n;
                MaybeReportProgress();
            }

            return n;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var n = await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken)
                .ConfigureAwait(false);
            if (n > 0)
            {
                _read += n;
                MaybeReportProgress();
            }

            return n;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var n = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n > 0)
            {
                _read += n;
                MaybeReportProgress();
            }

            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // 不释放 inner：调用方拥有文件流生命周期。
            base.Dispose(disposing);
        }
    }

    private sealed class HttpResponseStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;

        public HttpResponseStream(Stream inner, HttpResponseMessage response)
        {
            _inner = inner;
            _response = response;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _response.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
