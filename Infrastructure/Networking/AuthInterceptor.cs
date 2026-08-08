using Chat_App.Infrastructure.Identity;
using Core.Interfaces;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Infrastructure.Networking;

public class AuthInterceptor(TokenInfo tokenInfo, ILocalDeviceIdentity deviceIdentity) : DelegatingHandler
{
    private readonly TokenInfo _tokenInfo = tokenInfo;
    private readonly ILocalDeviceIdentity _deviceIdentity = deviceIdentity;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        EnsureDeviceHeaders(request);

        var skipAuth = request.Options.TryGetValue(RequestOptionKeys.SkipAuthInterceptor, out var skipToken) && skipToken;

        // 记录本次请求实际使用的 AccessToken：401 时据此判断是否已被并发刷新。
        string? usedToken = null;
        if (!skipAuth && request.Headers.Authorization is null)
        {
            usedToken = _tokenInfo.Token?.TokenValue;
            if (!string.IsNullOrWhiteSpace(usedToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", usedToken);
            }
        }
        else if (request.Headers.Authorization is { Scheme: "Bearer" } auth)
        {
            usedToken = auth.Parameter;
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized && !skipAuth)
        {
            var currentToken = _tokenInfo.Token?.TokenValue;

            // 令牌已被并发刷新（与请求时不同）：不再触发刷新，直接用新令牌重放。
            if (usedToken is not null
                && currentToken is not null
                && !string.Equals(usedToken, currentToken, StringComparison.Ordinal))
            {
                response.Dispose();
                return await ReplayWithTokenAsync(request, currentToken, cancellationToken).ConfigureAwait(false);
            }

            // 请求使用的令牌即当前令牌：执行 single-flight 刷新后重放一次。
            var isRefreshed = await _tokenInfo.RefreshTokensAsync(cancellationToken);
            if (isRefreshed)
            {
                var newToken = _tokenInfo.Token?.TokenValue;
                if (string.IsNullOrWhiteSpace(newToken))
                    return response;

                response.Dispose();
                return await ReplayWithTokenAsync(request, newToken, cancellationToken).ConfigureAwait(false);
            }
        }

        return response;
    }

    private async Task<HttpResponseMessage> ReplayWithTokenAsync(
        HttpRequestMessage request,
        string accessToken,
        CancellationToken cancellationToken)
    {
        using var clonedRequest = await CloneRequestAsync(request, cancellationToken);
        clonedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        EnsureDeviceHeaders(clonedRequest);
        return await base.SendAsync(clonedRequest, cancellationToken);
    }

    private void EnsureDeviceHeaders(HttpRequestMessage request)
    {
        if (!request.Headers.Contains("X-Device-Id"))
            request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceIdentity.DeviceId);

        if (!request.Headers.Contains("User-Agent"))
            request.Headers.TryAddWithoutValidation("User-Agent", _deviceIdentity.UserAgent);

        if (!request.Headers.Contains("X-Device-Credential")
            && !string.IsNullOrWhiteSpace(_tokenInfo.DeviceCredential))
        {
            request.Headers.TryAddWithoutValidation("X-Device-Credential", _tokenInfo.DeviceCredential);
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage req, CancellationToken cancellation)
    {
        var clone = new HttpRequestMessage(req.Method, req.RequestUri)
        {
            VersionPolicy = req.VersionPolicy,
            Version = req.Version
        };

        if (req.Content != null)
        {
            // 仅可缓冲重放的内容（字节数组/字符串/表单）才自动复制重放。
            if (req.Content is ByteArrayContent or StringContent or FormUrlEncodedContent)
            {
                var bytes = await req.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
                clone.Content = new ByteArrayContent(bytes);
            }
            else
            {
                // 流式内容（如 StreamContent）不可自动缓冲重放（避免大文件 OOM 与已消耗流）。
                // 优先使用上层提供的请求体重建工厂；未提供则抛出明确异常，绝不发送空 body。
                if (req.Options.TryGetValue(RequestOptionKeys.ReplayFactory, out var factory) && factory is not null)
                {
                    clone.Content = await factory(cancellation).ConfigureAwait(false);
                }
                else
                {
                    throw new ReplayNotSupportedException(
                        "流式请求遇到 401 但未提供请求体重建工厂(ReplayFactory)，无法安全重放。上层需重新创建流与请求。");
                }
            }

            if (clone.Content != null)
            {
                foreach (var h in req.Content.Headers)
                    clone.Content.Headers.Add(h.Key, h.Value);
            }
        }

        foreach (var h in req.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);

        foreach (var option in req.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);

        return clone;
    }
}
