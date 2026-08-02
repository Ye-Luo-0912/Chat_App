using Chat_App.Services;
using Core.Contracts.Friends;
using Core.Contracts.Friends.Enums;
using Core.Interfaces;
using Chat_App.Infrastructure.Models;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Chat_App.Services;

public class FriendshipApiService : IFriendshipService
{
    private readonly HttpClient _httpClient;
    private readonly ICurrentUserContext _currentUserContext;

    public FriendshipApiService(HttpClient httpClient, ICurrentUserContext currentUserContext)
    {
        _currentUserContext = currentUserContext;
        _httpClient = httpClient;
    }

    private static string API(string t) => $"/api/Friendship/{t}";

    // ── 好友列表 ─────────────────────────────────────────

    public IAsyncEnumerable<FriendDto> GetAllFriendsAsync(CancellationToken ct = default)
    {
        var userId = _currentUserContext.UserId;
        if (!userId.HasValue)
        {
            Log.Warning("当前用户未登录，无法获取好友列表");
            return AsyncEnumerable.Empty<FriendDto>();
        }

        return  StreamJsonAsync<FriendDto> (API("all"), "获取好友列表", ct);
    }

    public async Task<OperationResult> DeleteFriendAsync(long friendId, CancellationToken ct = default)
    {
        return await ExecuteOperationAsync<OperationResult>(async (ct) =>
        {
            var response = await _httpClient
                .DeleteAsync(API($"{friendId}"), ct)
                .ConfigureAwait(false);

            Log.Information("删除好友成功 -> {FriendId}", friendId);

            return response;

        }, "删除好友", ct).ConfigureAwait(false);
    }

    // ── 好友申请 ─────────────────────────────────────────

    public async Task<OperationResult<FriendDto>> AcceptRequestAsync(
        long requesterId, CancellationToken ct = default)
    {
        var userId = _currentUserContext.UserId;
        if (!userId.HasValue)
        {
            Log.Warning("当前用户未登录，无法接受好友申请");
            return OperationResult<FriendDto>.LocalFail(
				LocalOperationErrorCode.Unauthorized, "未登录");
        }


		return await ExecuteOperationAsync<OperationResult<FriendDto>>(async (ct) =>
		{
			var response = await _httpClient
			   .PutAsync(API($"requests/{requesterId}/accept"), null, ct)
			   .ConfigureAwait(false);

			Log.Information("接受好友申请 {RequesterId}", requesterId);

			return response;

		}, "接受好友申请", ct).ConfigureAwait(false);

    }

    public async Task<OperationResult> DeclineRequestAsync(
        long requesterId, CancellationToken ct = default)
    {
        return await ExecuteOperationAsync<OperationResult>(async (ct) =>
        {
            var response = await _httpClient
                .PutAsync(API($"requests/{requesterId}/decline"), null, ct)
                .ConfigureAwait(false);

            Log.Information("拒绝好友申请 {RequesterId}", requesterId);

            return response;

        }, "拒绝好友申请", ct).ConfigureAwait(false);
    }

    public async Task<SendFriendRequestResult> SendFriendRequestAsync(
        long targetUserId, string? message, CancellationToken ct = default)
    {
        return await ExecuteOperationAsync<SendFriendRequestResult>(async (ct) =>
        {
            var payload = new { TargetUserId = targetUserId, Message = message };
            var response = await _httpClient
                .PostAsJsonAsync(API("requests"), payload, ct)
                .ConfigureAwait(false);

            Log.Information("发送好友申请 -> {TargetUserId}", targetUserId);

            return response;

        }, "发送好友申请", ct).ConfigureAwait(false);
    }

	public Task<OperationResult<List<FriendRequestDto>>> GetIncomingRequestsAsync(CancellationToken ct = default)
	{
		return ExecuteJsonAsync<List<FriendRequestDto>>(
			c => _httpClient.GetAsync(API("requests/incoming"), c),
			"获取收到的申请",
			ct);
	}

	public Task<OperationResult<List<FriendRequestDto>>> GetOutgoingRequestsAsync(CancellationToken ct = default)
	{
		return ExecuteJsonAsync<List<FriendRequestDto>>(
			c => _httpClient.GetAsync(API("requests/outgoing"), c),
			"获取发出的申请",
			ct);
	}

	// ── 黑名单 ───────────────────────────────────────────

	public Task<OperationResult<List<BlockedUserDto>>> GetBlockedUsersAsync(CancellationToken ct = default)
	{
		return ExecuteJsonAsync<List<BlockedUserDto>>(
			c => _httpClient.GetAsync(API("blocked"), c),
			"获取黑名单",
			ct);
	}

	public async Task<OperationResult> UnblockUserAsync(
        long blockedUserId, CancellationToken ct = default)
    {
        return await ExecuteOperationAsync<OperationResult>(async (ct) =>
        {
            var response = await _httpClient
                .DeleteAsync(API($"block/{blockedUserId}"), ct)
                .ConfigureAwait(false);

            Log.Information("解除拉黑 -> {UserId}", blockedUserId);

            return response;
        }, "解除拉黑", ct).ConfigureAwait(false);
    }

	private async IAsyncEnumerable<T> StreamJsonAsync<T>(
	string url,
	string operationName,
	[EnumeratorCancellation] CancellationToken ct = default)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		using var response = await _httpClient
			.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
			.ConfigureAwait(false);

		if (!response.IsSuccessStatusCode)
		{
			Log.Warning("{Operation} 失败，状态码：{StatusCode}", operationName, response.StatusCode);
			yield break;
		}

		await using var stream = await response.Content
			.ReadAsStreamAsync(ct)
			.ConfigureAwait(false);

		await foreach (var item in JsonSerializer.DeserializeAsyncEnumerable<T>(
						   stream,
						   cancellationToken: ct).ConfigureAwait(false))
		{
			if (item is not null)
				yield return item;
		}
	}


	/// <summary>
	/// 统一执行无返回值的操作，返回 OperationResult。
	/// </summary>
	private static async Task<TResult> ExecuteOperationAsync<TResult>(
		Func<CancellationToken, Task<HttpResponseMessage>> requestFunc,
		string operationName,
        CancellationToken ct = default) where TResult : OperationResult, new()
	{
        try
        {
            using var response = await requestFunc(ct).ConfigureAwait(false);

            // 直接反序列化为 OperationResult
            var result = await response.Content
                .ReadFromJsonAsync<TResult>(cancellationToken: ct)
                .ConfigureAwait(false);

			return result ?? new TResult
			{
				IsSuccess = false,
				ErrorCode = 0,
				Message = "响应体为空",
				IsLocal = true,
				LocalErrorCode = LocalOperationErrorCode.EmptyResponse
			};
		}
        catch (OperationCanceledException)
        {
            Log.Warning("{Operation} 操作被取消", operationName);
			return new TResult
			{
				IsSuccess = false,
				ErrorCode = 0,
				Message = "操作已取消",
				IsLocal = true,
				LocalErrorCode = LocalOperationErrorCode.Cancelled
			};
		}
		catch (HttpRequestException ex)
		{
			Log.Error(ex, "{Operation} 网络请求失败", operationName);

			return new TResult
			{
				IsSuccess = false,
				ErrorCode = 0,
				Message = "网络请求失败",
				IsLocal = true,
				LocalErrorCode = LocalOperationErrorCode.NetworkError
			};
		}
		catch (System.Text.Json.JsonException ex)
		{
			Log.Error(ex, "{Operation} 响应反序列化失败", operationName);

			return new TResult
			{
				IsSuccess = false,
				ErrorCode = 0,
				Message = "响应解析失败",
				IsLocal = true,
				LocalErrorCode = LocalOperationErrorCode.SerializationError
			};
		}
		catch (Exception ex)
		{
			Log.Error(ex, "{Operation} 发生未知错误", operationName);

			return new TResult
			{
				IsSuccess = false,
				ErrorCode = 0,
				Message = "操作失败，请稍后重试",
				IsLocal = true,
				LocalErrorCode = LocalOperationErrorCode.Unknown
			};
		}
	}

	private async Task<OperationResult<T>> ExecuteJsonAsync<T>(
	Func<CancellationToken, Task<HttpResponseMessage>> requestFunc,
	string operationName,
	CancellationToken ct = default)
	{
		try
		{
			using var response = await requestFunc(ct).ConfigureAwait(false);

			if (!response.IsSuccessStatusCode)
			{
				Log.Warning("{Operation} 失败，状态码：{StatusCode}", operationName, response.StatusCode);
				return OperationResult<T>.LocalFail(
					LocalOperationErrorCode.ServerError,
					$"服务器返回 {(int)response.StatusCode}");
			}

			var data = await response.Content
				.ReadFromJsonAsync<T>(cancellationToken: ct)
				.ConfigureAwait(false);

			if (data is null)
			{
				return OperationResult<T>.LocalFail(
					LocalOperationErrorCode.EmptyResponse,
					"响应体为空");
			}

			return OperationResult<T>.Ok(data);
		}
		catch (OperationCanceledException)
		{
			Log.Warning("{Operation} 操作被取消", operationName);
			throw;
		}
		catch (HttpRequestException ex)
		{
			Log.Error(ex, "{Operation} 网络请求失败", operationName);
			return OperationResult<T>.LocalFail(
				LocalOperationErrorCode.NetworkError,
				"网络请求失败");
		}
		catch (JsonException ex)
		{
			Log.Error(ex, "{Operation} 响应解析失败", operationName);
			return OperationResult<T>.LocalFail(
				LocalOperationErrorCode.SerializationError,
				"响应解析失败");
		}
		catch (Exception ex)
		{
			Log.Error(ex, "{Operation} 发生未知错误", operationName);
			return OperationResult<T>.LocalFail(
				LocalOperationErrorCode.Unknown,
				"操作失败，请稍后重试");
		}
	}
}