using System.Text.Json;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using ChatApp.Shared.Protocol.Tcp;
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

    /// <summary>
    /// 群组通话授权（GROUP-CALL-1 / MIDJOIN-1）：向 Server 请求群组 call grant；
    /// <paramref name="callId"/> 存在则携带原 callId 重签（更新参与者名单，同一通话持续）。
    /// wire 响应（callKind="group" + participantUserIds 升序名单）映射为
    /// <see cref="CallGrantDto"/>（CallKind=Group）。
    /// </summary>
    public async Task<CallGrantDto?> RequestGroupGrantAsync(
        IReadOnlyList<long> memberUserIds,
        string? callId = null,
        CancellationToken ct = default)
    {
        if (memberUserIds is not { Count: > 0 } || memberUserIds.Any(id => id <= 0))
        {
            Log.Warning("群组通话授权请求失败：成员名单为空或含非法 Id");
            return null;
        }

        try
        {
            using HttpResponseMessage response = await _httpClient
                .PostAsJsonAsync(
                    "/api/calls/grants",
                    new RequestGroupGrantBody
                    {
                        // Server 模型对 CalleeUserId 无 Range 校验（群组路径不读取该字段），
                        // 仍携带首个成员以兼容既有请求形状（GROUP-CALL-GAP-2 兼容通道）。
                        CalleeUserId = memberUserIds[0],
                        CallKind = "group",
                        ParticipantUserIds = memberUserIds,
                        CallId = string.IsNullOrWhiteSpace(callId) ? null : callId,
                    },
                    JsonOptions,
                    ct)
                .ConfigureAwait(false);

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (!response.IsSuccessStatusCode || root.TryGetProperty("error", out _))
            {
                Log.Warning("群组通话授权请求失败 ({(int)StatusCode}): {Body}",
                    response.StatusCode, json);
                return null;
            }

            if (!root.TryGetProperty("data", out JsonElement data) || data.ValueKind != JsonValueKind.Object)
            {
                Log.Warning("群组通话授权响应缺少 data 对象包体");
                return null;
            }

            // callKind 在 HTTP wire 上是字符串（"group"），CallGrantDto 的 CallKind 是数值枚举：
            // 手工映射（与 e2e harness 同款解析），避免字符串→枚举反序列化失败。
            static long ReadInt64(JsonElement parent, string name)
                => parent.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Number
                    ? el.GetInt64()
                    : 0;
            var grant = new CallGrantDto
            {
                CallId = data.GetProperty("callId").GetString() ?? string.Empty,
                CallerUserId = ReadInt64(data, "callerUserId"),
                CalleeUserId = ReadInt64(data, "calleeUserId"),
                ExpiresAtMs = ReadInt64(data, "expiresAtMs"),
                Nonce = data.TryGetProperty("nonce", out var nonce) ? (nonce.GetString() ?? string.Empty) : string.Empty,
                Signature = data.TryGetProperty("signature", out var sig) ? sig.GetString() : null,
                CallKind = TcpCallKind.Group,
            };
            if (data.TryGetProperty("participantUserIds", out var participants)
                && participants.ValueKind == JsonValueKind.Array)
            {
                var list = new List<long>();
                foreach (var item in participants.EnumerateArray())
                    list.Add(item.GetInt64());
                grant.Participants = list;
            }

            if (string.IsNullOrWhiteSpace(grant.CallId)
                || grant.CallerUserId <= 0
                || grant.Participants is not { Count: > 0 })
            {
                Log.Warning("群组通话授权响应解析失败或字段不完整");
                return null;
            }

            return grant;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning("群组通话授权请求被本地取消");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "群组通话授权请求失败");
            return null;
        }
    }

    /// <summary>群组请求体（camelCase wire：callKind="group"、participantUserIds、可选 callId 重签）。</summary>
    private sealed class RequestGroupGrantBody
    {
        public long CalleeUserId { get; set; }

        public string CallKind { get; set; } = "group";

        public IReadOnlyList<long> ParticipantUserIds { get; set; } = [];

        /// <summary>缺省时序列化省略（JsonIgnore）——Server 视为新生成 CallId。</summary>
        [System.Text.Json.Serialization.JsonIgnore(
            Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? CallId { get; set; }
    }
}