using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Net.Http.Json;
using ChatApp.Contracts.Http.Common;
using ChatApp.Contracts.Http.Friends;
using Core.Contracts.Friends;
using Core.Contracts.Friends.Enums;
using Core.Interfaces;
using Serilog;

namespace Chat_App.Services;

/// <summary>
/// Adapts the Server's current cursor and mutation-envelope wire shapes to the
/// client's local operation model.
/// </summary>
public sealed class FriendshipApiService(
    HttpClient httpClient,
    ICurrentUserContext currentUserContext) : IFriendshipService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = httpClient;
    private readonly ICurrentUserContext _currentUserContext = currentUserContext;

    private static string Api(string path) => $"/api/Friendship/{path}";

    public async IAsyncEnumerable<FriendDto> GetAllFriendsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_currentUserContext.UserId.HasValue)
        {
            Log.Warning("当前用户未登录，无法获取好友列表");
            yield break;
        }

        IReadOnlyList<FriendDto> items;
        try
        {
            items = await ReadAllPagesAsync<FriendDto>(Api("all"), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            Log.Warning(ex, "获取好友列表失败");
            yield break;
        }

        foreach (FriendDto item in items)
            yield return item;
    }

    public Task<OperationResult> DeleteFriendAsync(long friendId, CancellationToken ct = default) =>
        ExecuteOperationAsync(
            token => _httpClient.DeleteAsync(Api(friendId.ToString()), token),
            "删除好友",
            ct);

    public async Task<SendFriendRequestResult> SendFriendRequestAsync(
        long targetUserId,
        string? message,
        CancellationToken ct = default)
    {
        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                    Api("requests"),
                    new SendFriendRequestRequest { TargetUserId = targetUserId, Message = message },
                    JsonOptions,
                    ct)
                .ConfigureAwait(false);

            JsonElement payload = await ReadPayloadAsync(response, ct).ConfigureAwait(false);
            SendFriendRequestResponse? wire = payload.Deserialize<SendFriendRequestResponse>(JsonOptions);
            if (wire is null)
                return SendFriendRequestResult.LocalFail(LocalOperationErrorCode.EmptyResponse, "响应体为空");

            return new SendFriendRequestResult
            {
                IsSuccess = wire.IsSuccess,
                ErrorCode = (int)wire.ErrorCode,
                Message = wire.Message,
                Outcome = wire.Outcome,
                Friend = wire.Friend
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SendFriendRequestResult.LocalFail(LocalOperationErrorCode.Cancelled, "操作已取消");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "发送好友申请失败");
            return SendFriendRequestResult.LocalFail(MapLocalError(ex), MapLocalMessage(ex));
        }
    }

    public Task<OperationResult<List<FriendRequestDto>>> GetIncomingRequestsAsync(
        CancellationToken ct = default) =>
        ReadPageResultAsync<FriendRequestDto>(Api("requests/incoming"), "获取收到的申请", ct);

    public Task<OperationResult<List<FriendRequestDto>>> GetOutgoingRequestsAsync(
        CancellationToken ct = default) =>
        ReadPageResultAsync<FriendRequestDto>(Api("requests/outgoing"), "获取发出的申请", ct);

    public async Task<OperationResult<FriendDto>> AcceptRequestAsync(
        long requesterId,
        CancellationToken ct = default)
    {
        if (!_currentUserContext.UserId.HasValue)
            return OperationResult<FriendDto>.LocalFail(LocalOperationErrorCode.Unauthorized, "未登录");

        try
        {
            using HttpResponseMessage response = await _httpClient.PutAsync(
                    Api($"requests/{requesterId}/accept"),
                    content: null,
                    ct)
                .ConfigureAwait(false);
            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (response.IsSuccessStatusCode
                && root.TryGetProperty("data", out JsonElement data)
                && data.ValueKind == JsonValueKind.Object)
            {
                FriendDto? friend = data.Deserialize<FriendDto>(JsonOptions);
                return friend is null
                    ? OperationResult<FriendDto>.LocalFail(LocalOperationErrorCode.EmptyResponse, "响应体为空")
                    : OperationResult<FriendDto>.Ok(friend);
            }

            FriendshipGenericOperationResponse<FriendDto>? failure =
                root.Deserialize<FriendshipGenericOperationResponse<FriendDto>>(JsonOptions);
            return failure is null
                ? OperationResult<FriendDto>.LocalFail(LocalOperationErrorCode.SerializationError, "响应解析失败")
                : new OperationResult<FriendDto>
                {
                    IsSuccess = failure.Succeeded,
                    ErrorCode = (int)failure.ErrorCode,
                    Message = failure.Message,
                    Data = failure.Data
                };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return OperationResult<FriendDto>.LocalFail(LocalOperationErrorCode.Cancelled, "操作已取消");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "接受好友申请失败");
            return OperationResult<FriendDto>.LocalFail(MapLocalError(ex), MapLocalMessage(ex));
        }
    }

    public Task<OperationResult> DeclineRequestAsync(long requesterId, CancellationToken ct = default) =>
        ExecuteOperationAsync(
            token => _httpClient.PutAsync(Api($"requests/{requesterId}/decline"), content: null, token),
            "拒绝好友申请",
            ct);

    public Task<OperationResult<List<BlockedUserDto>>> GetBlockedUsersAsync(CancellationToken ct = default) =>
        ReadPageResultAsync<BlockedUserDto>(Api("blocked"), "获取黑名单", ct);

    public Task<OperationResult> UnblockUserAsync(long blockedUserId, CancellationToken ct = default) =>
        ExecuteOperationAsync(
            token => _httpClient.DeleteAsync(Api($"block/{blockedUserId}"), token),
            "解除拉黑",
            ct);

    private async Task<OperationResult> ExecuteOperationAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        string operation,
        CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await send(ct).ConfigureAwait(false);
            JsonElement payload = await ReadPayloadAsync(response, ct).ConfigureAwait(false);
            FriendshipOperationResponse? wire = payload.Deserialize<FriendshipOperationResponse>(JsonOptions);
            return wire is null
                ? OperationResult.LocalFail(LocalOperationErrorCode.EmptyResponse, "响应体为空")
                : new OperationResult
                {
                    IsSuccess = wire.IsSuccess,
                    ErrorCode = (int)wire.ErrorCode,
                    Message = wire.Message
                };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return OperationResult.LocalFail(LocalOperationErrorCode.Cancelled, "操作已取消");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Operation}失败", operation);
            return OperationResult.LocalFail(MapLocalError(ex), MapLocalMessage(ex));
        }
    }

    private async Task<OperationResult<List<T>>> ReadPageResultAsync<T>(
        string path,
        string operation,
        CancellationToken ct)
    {
        try
        {
            List<T> items = await ReadAllPagesAsync<T>(path, ct).ConfigureAwait(false);
            return OperationResult<List<T>>.Ok(items);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "{Operation}失败", operation);
            return OperationResult<List<T>>.LocalFail(MapLocalError(ex), MapLocalMessage(ex));
        }
    }

    private async Task<List<T>> ReadAllPagesAsync<T>(string basePath, CancellationToken ct)
    {
        var items = new List<T>();
        string? cursor = null;
        var seenCursors = new HashSet<string>(StringComparer.Ordinal);

        do
        {
            string path = cursor is null
                ? $"{basePath}?limit=100"
                : $"{basePath}?cursor={Uri.EscapeDataString(cursor)}&limit=100";
            using HttpResponseMessage response = await _httpClient.GetAsync(path, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            CursorPage<T>? page = await response.Content.ReadFromJsonAsync<CursorPage<T>>(JsonOptions, ct)
                .ConfigureAwait(false);
            if (page is null)
                throw new JsonException("Cursor page response is empty.");

            items.AddRange(page.Items);
            if (!page.HasMore || string.IsNullOrWhiteSpace(page.NextCursor))
                break;
            if (!seenCursors.Add(page.NextCursor))
                throw new JsonException("Server repeated a friendship cursor.");
            cursor = page.NextCursor;
        } while (true);

        return items;
    }

    private static async Task<JsonElement> ReadPayloadAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        return root.TryGetProperty("data", out JsonElement data) ? data.Clone() : root.Clone();
    }

    private static LocalOperationErrorCode MapLocalError(Exception ex) => ex switch
    {
        HttpRequestException => LocalOperationErrorCode.NetworkError,
        JsonException => LocalOperationErrorCode.SerializationError,
        _ => LocalOperationErrorCode.Unknown
    };

    private static string MapLocalMessage(Exception ex) => ex switch
    {
        HttpRequestException => "网络请求失败",
        JsonException => "响应解析失败",
        _ => "操作失败，请稍后重试"
    };
}
