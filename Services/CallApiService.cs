using System.Text.Json;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Services;

/// <summary>
/// 通话授权 HTTP 服务实现（CALL-E2E-2）。向 Server 的 <c>POST /api/calls/grants</c>
/// 请求短期 call grant，并把 wire 响应（<c>data</c> 包体内的
/// callId/callerUserId/calleeUserId/expiresAtMs/nonce/signature）映射为
/// <see cref="CallGrantDto"/>。
/// </summary>
public sealed class CallApiService : ICallApiService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;

    public CallApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<CallGrantDto?> RequestGrantAsync(long calleeUserId, CancellationToken ct = default)
    {
        if (calleeUserId <= 0)
        {
            Log.Warning("通话授权请求失败：非法的被叫用户 Id {CalleeUserId}", calleeUserId);
            return null;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient
                .PostAsJsonAsync(
                    "/api/calls/grants",
                    new RequestGrantBody { CalleeUserId = calleeUserId },
                    JsonOptions,
                    ct)
                .ConfigureAwait(false);

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            // 错误包体 { "error": "call_grant_..." } 或非成功状态码 → 视为失败。
            if (!response.IsSuccessStatusCode || root.TryGetProperty("error", out _))
            {
                Log.Warning("通话授权请求失败 ({(int)StatusCode}): {Body}",
                    response.StatusCode, json);
                return null;
            }

            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                Log.Warning("通话授权响应缺少 data 对象包体");
                return null;
            }

            CallGrantDto? grant = data.Deserialize<CallGrantDto>(JsonOptions);
            if (grant is null || string.IsNullOrWhiteSpace(grant.CallId) || grant.CalleeUserId <= 0)
            {
                Log.Warning("通话授权响应解析失败或字段不完整");
                return null;
            }

            return grant;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning("通话授权请求被本地取消");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "通话授权请求失败");
            return null;
        }
    }

    /// <summary>请求体（JsonSerializerOptions(Web) 会将 CalleeUserId 序列化为 camelCase）。</summary>
    private sealed class RequestGrantBody
    {
        public long CalleeUserId { get; set; }
    }
}