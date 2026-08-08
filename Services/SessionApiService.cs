using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Contracts.Http;
using ChatApp.Contracts.Http.Sessions;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Services;

public sealed class SessionApiService : ISessionApiService
{
    private readonly HttpClient _httpClient;

    public SessionApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<SessionDevice>> ListSessionsAsync(CancellationToken ct = default)
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
            .ReadFromJsonAsync(HttpContractsJsonSerializerContext.Default.ListSessionDevice, ct)
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

        RevokeSessionsResponse? result = await response.Content
            .ReadFromJsonAsync(HttpContractsJsonSerializerContext.Default.RevokeSessionsResponse, ct)
            .ConfigureAwait(false);
        return result?.Revoked ?? 0;
    }
}
