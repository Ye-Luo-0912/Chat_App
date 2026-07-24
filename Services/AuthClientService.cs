using Chat_App.Infrastructure.Persistence;
using Core.Contracts.Auth;
using Core.Contracts.Common;
using Core.Interfaces;
using Infrastructure.Networking;
using Serilog;
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Services;

/// <summary>
/// 登录服务，负责与认证服务器通信完成用户登录和 Token 刷新。
/// </summary>
public class AuthClientService(HttpClient httpClient, IDatabaseService databaseService) : IAuthClientService
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly IDatabaseService _databaseService = databaseService;

    private string BaseUrlStr(string str) => $"/api/auth/{str}";

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var loginRequest = new LoginRequest
        {
            Username = username,
            Password = password
        };

        var jsonRequest = JsonSerializer.Serialize(loginRequest, LoginJsonContext.Default.LoginRequest);

        // 发送 POST 请求到认证服务器的登录端点
        //var response = await _httpClient.PostAsync($"{_authServerBaseUrl}/api/users/login", content, cancellationToken); 
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrlStr("login"))
        {
            Content = new StringContent(jsonRequest, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"))
        };

        // 在发送请求之前，设置一个标志，告诉认证拦截器跳过这个请求的处理
        //request.Options.Set(RequestOptionKeys.SkipAuthInterceptor, true);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);


        if (response.IsSuccessStatusCode)
        {
            // 处理成功响应 (例如，读取访问令牌和刷新令牌)
            var loginResult = JsonSerializer.Deserialize(jsonResponse, LoginJsonContext.Default.LoginResult);

            if (loginResult is null)
                return new LoginResult { IsSuccess = false, ErrorMessage = "无法解析登录响应。" };

            if (loginResult.IsSuccess && (string.IsNullOrWhiteSpace(loginResult.AccessToken) || string.IsNullOrWhiteSpace(loginResult.RefreshToken)))
                return new LoginResult { IsSuccess = false, ErrorMessage = "登录响应缺少令牌信息。" };

            return loginResult;
        }


        //处理错误情况 (例如，读取错误消息)
        try
        {
            //尝试反序列化为包含错误信息的对象.  
            var errorResult = JsonSerializer.Deserialize(jsonResponse, LoginJsonContext.Default.LoginResult);
            return errorResult ?? new LoginResult
            {
                IsSuccess = false,
                ErrorMessage = "登录失败，未知错误！"
            };
        }
        catch
        {
            //如果无法反序列化为ErrorResult, 就直接返回错误信息
            return new LoginResult { IsSuccess = false, ErrorMessage = $"登录失败: {response.StatusCode} - {jsonResponse}" };
        }
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        var stored = await _databaseService.GetTokenAsync().ConfigureAwait(false);
        if (stored is null || string.IsNullOrWhiteSpace(stored.RefreshToken))
            return;

        try
        {
            var payload = new LogoutRequest { RefreshToken = stored.RefreshToken };
            var json = JsonSerializer.Serialize(payload, LoginJsonContext.Default.LogoutRequest);
            using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrlStr("logout"))
            {
                Content = new StringContent(json, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"))
            };
            if (!string.IsNullOrWhiteSpace(stored.AccessToken))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", stored.AccessToken);
            }

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Log.Warning("服务端退出登录返回 {Status}: {Body}", response.StatusCode, body);
            }
        }
        catch (Exception ex)
        {
            // 本地清理仍继续；网络失败不阻断退出。
            Log.Warning(ex, "调用服务端 logout 失败");
        }
    }

    public async Task<LoginResult> RefreshTokenAsync(string refreshToken, long userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(refreshToken) || userId is 0)
        {
            return new LoginResult { IsSuccess = false, ErrorMessage = "缺少刷新令牌。" };
        }

        // 构建请求
        var refreshRequest = new RefreshTokenRequest { UserId = userId, RefreshToken = refreshToken };
        var jsonRequest = JsonSerializer.Serialize(refreshRequest, LoginJsonContext.Default.RefreshTokenRequest);

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrlStr("refresh-token"))
        {
            Content = new StringContent(jsonRequest, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"))
        };

        request.Options.Set(RequestOptionKeys.SkipAuthInterceptor, true);

        // 发送请求并获取响应
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        // 发送 POST 请求到刷新令牌的端点
        //using var response = await _httpClient.PostAsync($"{_authServerBaseUrl}/api/users/refresh-token", content, cancellationToken);

        // 处理响应
        if (response.IsSuccessStatusCode)
        {
            var refreshResult = JsonSerializer.Deserialize(jsonResponse, LoginJsonContext.Default.LoginResult);

            return refreshResult ?? new LoginResult { IsSuccess = false, ErrorMessage = "刷新令牌失败,无法解析响应" };
        }
        else
        {
            try
            {
                var errorResult = JsonSerializer.Deserialize(jsonResponse, LoginJsonContext.Default.LoginResult);
                return new LoginResult { IsSuccess = false, ErrorMessage = errorResult?.ErrorMessage ?? "刷新令牌失败，未知错误。" };
            }
            catch
            {
                return new LoginResult { IsSuccess = false, ErrorMessage = $"刷新令牌失败: {response.StatusCode} - {jsonResponse}" };
            }
        }
    }

    public async Task<bool> SendRegisterCodeAsync(string email, CancellationToken cancellationToken = default)
    {
        var requestModel = new EamilRequest { Email = email };
        var jsonRequest = JsonSerializer.Serialize(requestModel, LoginJsonContext.Default.EamilRequest);

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrlStr("send-register-code"))
        {
            Content = new StringContent(jsonRequest, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"))
        };

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // 只要是 200 OK，就说明邮件发成功了
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送验证码请求异常: {Email}", email);
            return false;
        }
    }

    public async Task<RegisterResponse> RegisterAsync(string email, string code, string password, CancellationToken cancellationToken = default)
    {
        //构建注册包 
        var registerModel = new RegisterRequest
        {
            Email = email,
            Code = code,
            Password = password
        };
        var jsonRequest = JsonSerializer.Serialize(registerModel, LoginJsonContext.Default.RegisterRequest);

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrlStr("register"))
        {
            Content = new StringContent(jsonRequest, Encoding.UTF8, MediaTypeHeaderValue.Parse("application/json"))
        };

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            Log.Debug(jsonResponse);
            // 提取后端返回的错误信息

            var regResponse = JsonSerializer.Deserialize(jsonResponse, LoginJsonContext.Default.RegisterResponse);

            Log.Debug($"Register response: {regResponse?.IsSuccess}");

            return regResponse ?? new RegisterResponse { IsSuccess = false, Message = "响应解析失败" };

        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "注册请求异常: {Email}", email);
            return new RegisterResponse { IsSuccess = false, Message = "网络连接异常" };
        }
    }
}