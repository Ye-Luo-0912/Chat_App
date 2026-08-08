using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Chat_App.Infrastructure.Networking;
using Chat_App.Infrastructure.Persistence;
using ChatApp.Contracts.Http;
using ChatApp.Contracts.Http.Auth;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Services;

/// <summary>HTTP authentication client using the versioned public wire contracts.</summary>
public sealed class AuthClientService(HttpClient httpClient, IDatabaseService databaseService) : IAuthClientService
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly IDatabaseService _databaseService = databaseService;

    private static string Api(string path) => $"/api/auth/{path}";

    public async Task<LoginResponse> LoginAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var body = new LoginRequest { Username = username, Password = password };
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            Api("login"),
            JsonSerializer.Serialize(body, HttpContractsJsonSerializerContext.Default.LoginRequest));
        request.Options.Set(RequestOptionKeys.SkipAuthInterceptor, true);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            LoginResponse? result = JsonSerializer.Deserialize(
                json,
                HttpContractsJsonSerializerContext.Default.LoginResponse);
            if (result is null)
                return LoginFailure("无法解析登录响应。");
            if (result.IsSuccess
                && (string.IsNullOrWhiteSpace(result.AccessToken)
                    || string.IsNullOrWhiteSpace(result.RefreshToken)
                    || string.IsNullOrWhiteSpace(result.DeviceCredential)
                    || result.AccessTokenExpiresAtUtc <= DateTime.UtcNow
                    || result.RefreshTokenExpiresAtUtc <= DateTime.UtcNow))
            {
                return LoginFailure("登录响应缺少令牌、设备凭据或有效过期时间。");
            }

            return result;
        }
        catch (JsonException)
        {
            return LoginFailure($"登录失败: {response.StatusCode} - {json}");
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _databaseService.GetTokenAsync().ConfigureAwait(false);
        if (stored is null || string.IsNullOrWhiteSpace(stored.RefreshToken))
            return;

        try
        {
            var body = new LogoutRequest { RefreshToken = stored.RefreshToken };
            using var request = CreateJsonRequest(
                HttpMethod.Post,
                Api("logout"),
                JsonSerializer.Serialize(body, HttpContractsJsonSerializerContext.Default.LogoutRequest));
            if (!string.IsNullOrWhiteSpace(stored.AccessToken))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", stored.AccessToken);
            AddDeviceCredential(request, stored.DeviceCredential);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Log.Warning("服务端退出登录返回 {Status}: {Body}", response.StatusCode, responseBody);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "调用服务端 logout 失败");
        }
    }

    public async Task<RefreshTokenResponse> RefreshTokenAsync(
        string refreshToken,
        long userId,
        string? deviceCredential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || userId <= 0)
            return RefreshFailure(AuthErrorType.InvalidCredentials);

        var body = new RefreshTokenRequest { UserId = userId, RefreshToken = refreshToken };
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            Api("refresh-token"),
            JsonSerializer.Serialize(body, HttpContractsJsonSerializerContext.Default.RefreshTokenRequest));
        request.Options.Set(RequestOptionKeys.SkipAuthInterceptor, true);
        AddDeviceCredential(request, deviceCredential);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            RefreshTokenResponse? result = JsonSerializer.Deserialize(
                json,
                HttpContractsJsonSerializerContext.Default.RefreshTokenResponse);
            if (result is null)
                return RefreshFailure(AuthErrorType.SystemError);
            if (!response.IsSuccessStatusCode || !result.IsSuccess)
                return result;

            if (string.IsNullOrWhiteSpace(result.AccessToken)
                || string.IsNullOrWhiteSpace(result.RefreshToken)
                || string.IsNullOrWhiteSpace(result.DeviceCredential)
                || result.AccessTokenExpiresAtUtc <= DateTime.UtcNow
                || result.RefreshTokenExpiresAtUtc <= DateTime.UtcNow)
            {
                Log.Warning("刷新响应缺少令牌、轮换设备凭据或有效过期时间");
                return RefreshFailure(AuthErrorType.SystemError);
            }

            return result;
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "刷新令牌响应解析失败 Status={Status}", response.StatusCode);
            return RefreshFailure(AuthErrorType.SystemError);
        }
    }

    public async Task<bool> SendRegisterCodeAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var body = new SendEmailCodeRequest { Email = email };
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            Api("send-register-code"),
            JsonSerializer.Serialize(body, HttpContractsJsonSerializerContext.Default.SendEmailCodeRequest));
        request.Options.Set(RequestOptionKeys.SkipAuthInterceptor, true);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送验证码请求异常: {Email}", email);
            return false;
        }
    }

    public async Task<RegisterResponse> RegisterAsync(
        string email,
        string code,
        string password,
        CancellationToken cancellationToken = default)
    {
        var body = new RegisterRequest { Email = email, Code = code, Password = password };
        using var request = CreateJsonRequest(
            HttpMethod.Post,
            Api("register"),
            JsonSerializer.Serialize(body, HttpContractsJsonSerializerContext.Default.RegisterRequest));
        request.Options.Set(RequestOptionKeys.SkipAuthInterceptor, true);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize(
                       json,
                       HttpContractsJsonSerializerContext.Default.RegisterResponse)
                   ?? new RegisterResponse { Message = "响应解析失败" };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "注册请求异常: {Email}", email);
            return new RegisterResponse { Message = "网络连接异常" };
        }
    }

    private static HttpRequestMessage CreateJsonRequest(HttpMethod method, string uri, string json) => new(method, uri)
    {
        Content = new StringContent(json, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"))
    };

    private static void AddDeviceCredential(HttpRequestMessage request, string? deviceCredential)
    {
        if (!string.IsNullOrWhiteSpace(deviceCredential))
            request.Headers.TryAddWithoutValidation("X-Device-Credential", deviceCredential);
    }

    private static LoginResponse LoginFailure(string message) => new()
    {
        IsSuccess = false,
        LoginCheckStatus = LoginCheckStatus.InvalidCredentials,
        ErrorMessage = message
    };

    private static RefreshTokenResponse RefreshFailure(AuthErrorType errorType) => new()
    {
        IsSuccess = false,
        ErrorType = errorType
    };
}
