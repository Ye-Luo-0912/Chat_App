using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Contracts.Attachments;
using Core.Interfaces;
using Infrastructure.Networking;
using Serilog;

namespace Chat_App.Services;

public sealed class AttachmentApiService : IAttachmentClientService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly HttpClient S3UploadClient = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private readonly HttpClient _httpClient;

    public AttachmentApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AttachmentPresignResponseDto> PresignAsync(
        AttachmentPresignRequestDto request,
        CancellationToken ct = default)
    {
        using var response = await _httpClient
            .PostAsJsonAsync("/api/attachments/presign", request, JsonOptions, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, "预签名", ct).ConfigureAwait(false);
        var body = await response.Content
            .ReadFromJsonAsync<AttachmentPresignResponseDto>(JsonOptions, ct)
            .ConfigureAwait(false);
        return body ?? throw new InvalidOperationException("预签名响应为空");
    }

    public async Task UploadAsync(
        AttachmentPresignResponseDto ticket,
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

        // 九5: 流式上传内容不可自动缓冲重放。为 401 重试提供请求体重建工厂：
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
            response = await S3UploadClient
                .PutAsync(absolute, streamContent, ct)
                .ConfigureAwait(false);
        }
        else
        {
            var relative = ticket.UploadUrl;
            if (Uri.TryCreate(ticket.UploadUrl, UriKind.Absolute, out var absApi))
                relative = absApi.PathAndQuery;

            using var putRequest = new HttpRequestMessage(HttpMethod.Put, relative);
            putRequest.Content = streamContent;
            // 九5: 提供请求体重建工厂，流式上传遇 401 时由拦截器重建流而非发送空 body。
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

    public async Task<ConfirmAttachmentResponseDto> ConfirmAsync(
        ConfirmAttachmentRequestDto request,
        CancellationToken ct = default)
    {
        using var response = await _httpClient
            .PostAsJsonAsync("/api/attachments/confirm", request, JsonOptions, ct)
            .ConfigureAwait(false);
        await EnsureSuccessAsync(response, "确认附件", ct).ConfigureAwait(false);
        var body = await response.Content
            .ReadFromJsonAsync<ConfirmAttachmentResponseDto>(JsonOptions, ct)
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

        AttachmentPresignResponseDto? ticket = null;
        Exception? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (content.CanSeek)
                    content.Position = 0;

                ticket = await PresignAsync(
                        new AttachmentPresignRequestDto
                        {
                            ContentType = contentType,
                            ContentLength = contentLength,
                            OriginalName = originalName,
                            ClientAttachmentId = clientAttachmentId,
                            Sha256 = sha256
                        },
                        ct)
                    .ConfigureAwait(false);

                // 秒传命中：服务端已有同 hash 文件，跳过上传直接返回
                if (ticket.Deduplicated)
                {
                    return new AttachmentUploadResult
                    {
                        AttachmentId = ticket.AttachmentId,
                        DownloadPath = ticket.DownloadPath,
                        ObjectKey = ticket.ObjectKey,
                        ContentType = contentType,
                        SizeBytes = contentLength,
                        OriginalName = originalName
                    };
                }

                await UploadAsync(ticket, content, contentType, contentLength, progress, ct)
                    .ConfigureAwait(false);

                var confirmed = await ConfirmAsync(
                        new ConfirmAttachmentRequestDto
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
            catch (OperationCanceledException)
            {
                if (ticket is not null)
                    await TryAbandonQuietlyAsync(ticket.AttachmentId, CancellationToken.None)
                        .ConfigureAwait(false);
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
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
        }

        throw lastError ?? new InvalidOperationException("附件上传失败");
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
            FileName = fileName
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
