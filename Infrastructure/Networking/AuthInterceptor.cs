using Chat_App;
using Core.Interfaces;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Infrastructure.Networking;

public class AuthInterceptor(TokenInfo tokenInfo, ILocalDeviceIdentity deviceIdentity) : DelegatingHandler
{
    private readonly TokenInfo _tokenInfo = tokenInfo;
    private readonly ILocalDeviceIdentity _deviceIdentity = deviceIdentity;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        EnsureDeviceHeaders(request);

        var skipAuth = request.Options.TryGetValue(RequestOptionKeys.SkipAuthInterceptor, out var skipToken) && skipToken;

        if (!skipAuth && request.Headers.Authorization is null)
        {
            var token = _tokenInfo.Token?.TokenValue;
            if (!string.IsNullOrWhiteSpace(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var isRefreshed = await _tokenInfo.RefreshTokensAsync(cancellationToken);

            if (isRefreshed)
            {
                var newToken = _tokenInfo.Token?.TokenValue;

                using var clonedRequest = await CloneRequestAsync(request, cancellationToken);
                clonedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                EnsureDeviceHeaders(clonedRequest);

                response.Dispose();
                response = await base.SendAsync(clonedRequest, cancellationToken);
            }
        }

        return response;
    }

    private void EnsureDeviceHeaders(HttpRequestMessage request)
    {
        if (!request.Headers.Contains("X-Device-Id"))
            request.Headers.TryAddWithoutValidation("X-Device-Id", _deviceIdentity.DeviceId);

        if (!request.Headers.Contains("User-Agent"))
            request.Headers.TryAddWithoutValidation("User-Agent", _deviceIdentity.UserAgent);
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
            var bytes = await req.Content.ReadAsByteArrayAsync(cancellation).ConfigureAwait(false);
            clone.Content = new ByteArrayContent(bytes);

            foreach (var h in req.Content.Headers)
                clone.Content.Headers.Add(h.Key, h.Value);
        }

        foreach (var h in req.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);

        foreach (var option in req.Options)
            clone.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);

        return clone;
    }
}
