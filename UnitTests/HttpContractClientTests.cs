using System.Net;
using System.Text;
using System.Text.Json;
using Chat_App.Infrastructure.Services;
using Chat_App.Services;
using ChatApp.Contracts.Http.Attachments;
using ChatApp.Contracts.Http.Auth;
using ChatApp.Contracts.Http.Friends;
using Xunit;

namespace UnitTests;

public sealed class HttpContractClientTests
{
    [Fact]
    public async Task Refresh_SendsDeviceCredential_AndKeepsRotatedExpiryData()
    {
        string? presentedCredential = null;
        string? requestBody = null;
        using var client = CreateClient(async request =>
        {
            presentedCredential = request.Headers.TryGetValues("X-Device-Credential", out var values)
                ? values.Single()
                : null;
            requestBody = await request.Content!.ReadAsStringAsync();
            return Json(HttpStatusCode.OK, """
                {
                  "isSuccess": true,
                  "accessToken": "access-2",
                  "accessTokenExpiresAtUtc": "2099-08-05T02:00:00Z",
                  "refreshToken": "refresh-2",
                  "refreshTokenExpiresAtUtc": "2099-09-05T02:00:00Z",
                  "deviceCredential": "device-2"
                }
                """);
        });

        var service = new AuthClientService(client, databaseService: null!);
        RefreshTokenResponse result = await service.RefreshTokenAsync(
            "refresh-1",
            42,
            "device-1");

        Assert.True(result.IsSuccess);
        Assert.Equal("device-1", presentedCredential);
        Assert.Equal("device-2", result.DeviceCredential);
        Assert.Equal(new DateTime(2099, 8, 5, 2, 0, 0, DateTimeKind.Utc), result.AccessTokenExpiresAtUtc);
        Assert.Contains("\"userId\":42", requestBody);
        Assert.Contains("\"refreshToken\":\"refresh-1\"", requestBody);
    }

    [Fact]
    public async Task Refresh_RejectsSuccessBodyWithoutExpiries()
    {
        using var client = CreateClient(_ => Task.FromResult(Json(HttpStatusCode.OK, """
            {"isSuccess":true,"accessToken":"access-2","refreshToken":"refresh-2","deviceCredential":"device-2"}
            """)));
        var service = new AuthClientService(client, databaseService: null!);

        RefreshTokenResponse result = await service.RefreshTokenAsync("refresh-1", 42, "device-1");

        Assert.False(result.IsSuccess);
        Assert.Equal(AuthErrorType.SystemError, result.ErrorType);
    }

    [Fact]
    public async Task Login_RejectsSuccessBodyWithoutDeviceCredential()
    {
        using var client = CreateClient(_ => Task.FromResult(Json(HttpStatusCode.OK, """
            {
              "isSuccess":true,
              "loginCheckStatus":1,
              "accessToken":"access-1",
              "accessTokenExpiresAtUtc":"2099-08-05T02:00:00Z",
              "refreshToken":"refresh-1",
              "refreshTokenExpiresAtUtc":"2099-09-05T02:00:00Z"
            }
            """)));
        var service = new AuthClientService(client, databaseService: null!);

        LoginResponse result = await service.LoginAsync("alice", "password");

        Assert.False(result.IsSuccess);
        Assert.Contains("设备凭据", result.ErrorMessage);
    }

    [Fact]
    public async Task FriendshipClient_FollowsCursorPages_AndUnwrapsMutationEnvelope()
    {
        var requested = new List<string>();
        using var client = CreateClient(request =>
        {
            string path = request.RequestUri!.PathAndQuery;
            requested.Add(path);
            return Task.FromResult(path switch
            {
                "/api/Friendship/all?limit=100" => Json(HttpStatusCode.OK, """
                    {"items":[{"friendId":7,"friendName":"alice","createdAt":"2026-08-05T00:00:00Z"}],"nextCursor":"c2","hasMore":true}
                    """),
                "/api/Friendship/all?cursor=c2&limit=100" => Json(HttpStatusCode.OK, """
                    {"items":[{"friendId":8,"friendName":"bob","createdAt":"2026-08-05T00:00:00Z"}],"nextCursor":null,"hasMore":false}
                    """),
                "/api/Friendship/requests" => Json(HttpStatusCode.OK, """
                    {"data":{"isSuccess":true,"errorCode":0,"outcome":1,"friend":null}}
                    """),
                _ => Json(HttpStatusCode.NotFound, "{}")
            });
        });
        var currentUser = new CurrentUserContext();
        currentUser.SetCurrentUser(42, "tester");
        var service = new FriendshipApiService(client, currentUser);

        var friends = new List<FriendDto>();
        await foreach (FriendDto friend in service.GetAllFriendsAsync())
            friends.Add(friend);
        Core.Contracts.Friends.SendFriendRequestResult sent =
            await service.SendFriendRequestAsync(99, "hello");

        Assert.Equal([7L, 8L], friends.Select(friend => friend.FriendId));
        Assert.True(sent.IsSuccess);
        Assert.Equal(SendFriendRequestOutcome.RequestSent, sent.Outcome);
        Assert.Equal(
            [
                "/api/Friendship/all?limit=100",
                "/api/Friendship/all?cursor=c2&limit=100",
                "/api/Friendship/requests"
            ],
            requested);
    }

    [Fact]
    public async Task AttachmentUpload_AppliesEveryPresignedUploadHeader()
    {
        string? contentType = null;
        string? encryption = null;
        string? tagging = null;
        using var client = CreateClient(request =>
        {
            contentType = request.Content?.Headers.ContentType?.MediaType;
            encryption = Header(request, "x-amz-server-side-encryption");
            tagging = Header(request, "x-amz-tagging");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var service = new AttachmentApiService(client);
        var ticket = new AttachmentPresignResponse
        {
            AttachmentId = "a1",
            UploadUrl = "/api/attachments/a1/upload",
            UploadHeaders = new Dictionary<string, string>
            {
                ["Content-Type"] = "image/png",
                ["x-amz-server-side-encryption"] = "AES256",
                ["x-amz-tagging"] = "chatapp-scan-state=unconfirmed"
            }
        };
        await using var content = new MemoryStream([1, 2, 3]);

        await service.UploadAsync(ticket, content, "application/octet-stream", 3);

        Assert.Equal("image/png", contentType);
        Assert.Equal("AES256", encryption);
        Assert.Equal("chatapp-scan-state=unconfirmed", tagging);
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) =>
        new(new StubHandler(handler))
        {
            BaseAddress = new Uri("https://chat.test")
        };

    private static HttpResponseMessage Json(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
