using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Contracts.Sessions;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Services;

public sealed class SessionApiService : ISessionApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _httpClient;

    public SessionApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<SessionDeviceDto>> ListSessionsAsync(CancellationToken ct = default)
    {
        using var response = await _httpClient
            .GetAsync("/api/users/me/sessions", ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"获取设备列表失败 ({(int)response.StatusCode}): {body}");
        }

        var items = await response.Content
            .ReadFromJsonAsync<List<SessionDeviceDto>>(JsonOptions, ct)
            .ConfigureAwait(false);
        return items ?? [];
    }

    public async Task RevokeSessionAsync(string deviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId 不能为空");

        using var response = await _httpClient
            .DeleteAsync($"/api/users/me/sessions/{Uri.EscapeDataString(deviceId)}", ct)
            .ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException($"撤销设备失败 ({(int)response.StatusCode}): {body}");
    }

    public async Task<int> RevokeOtherSessionsAsync(CancellationToken ct = default)
    {
        using var response = await _httpClient
            .PostAsync("/api/users/me/sessions/revoke-others", content: null, ct)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"撤销其他设备失败 ({(int)response.StatusCode}): {body}");
        }

        try
        {
            using var doc = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
                    cancellationToken: ct)
                .ConfigureAwait(false);
            if (doc.RootElement.TryGetProperty("revoked", out var revoked)
                || doc.RootElement.TryGetProperty("Revoked", out revoked))
            {
                return revoked.GetInt32();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "解析 revoke-others 响应失败");
        }

        return 0;
    }
}
