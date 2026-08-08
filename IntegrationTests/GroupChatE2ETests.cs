using Chat_App.Infrastructure.Serialization;
using Core.Interfaces;
using Core.Models;
using Core.Models.DTO;
using Core.Protocol;
using Core.Services;
using System.Buffers;
using Xunit;

namespace IntegrationTests;

/// <summary>
/// 群聊命令验收测试。
/// 验收场景：6 类群聊请求（创建/添加成员/移除成员/退出/改角色/成员列表）的 RequestId
/// 由发送模板生成一次、与 pending 键一致，服务器回显后按 Id 精确配对返回；
/// 6 类 S2C 群聊通知（加入/退出/被移除/角色变更/批量加入/解散）均能触发客户端事件。
/// </summary>
public class GroupChatE2ETests
{
    private const long OwnerId = 7101;
    private const long MemberId = 9101;
    private const long MemberId2 = 9102;
    private const string GroupId = "conv-grp-1001";
    private const string GroupTitle = "周末爬山群";

    // ── 6 类群聊请求正例：RequestId 由模板生成一次、与 pending 键一致，配对成功 ──

    [Fact]
    public async Task CreateGroup_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.CreateGroupAsync(GroupTitle, [MemberId, MemberId2]);
            Assert.True(resp.Succeeded);
            Assert.Equal(GroupId, resp.ConversationId);
            Assert.Equal(GroupTitle, resp.Title);
            Assert.Equal(3, resp.Members?.Count); // 创建者(Owner) + 2 名成员
            var seen = server.Frames.Single(f => f.Command == PacketCommand.CreateGroupRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
            Assert.False(string.IsNullOrWhiteSpace(seen.RequestId));
        });
    }

    [Fact]
    public async Task AddGroupMembers_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.AddGroupMembersAsync(GroupId, [MemberId]);
            Assert.True(resp.Succeeded);
            Assert.Equal(GroupId, resp.ConversationId);
            Assert.Single(resp.Members!);
            Assert.Equal(ConversationMemberRole.Member, resp.Members![0].Role);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.AddGroupMembersRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    [Fact]
    public async Task RemoveGroupMember_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.RemoveGroupMemberAsync(GroupId, MemberId);
            Assert.True(resp.Succeeded);
            Assert.Equal(GroupId, resp.ConversationId);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.RemoveGroupMemberRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    [Fact]
    public async Task LeaveGroup_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.LeaveGroupAsync(GroupId);
            Assert.True(resp.Succeeded);
            Assert.Equal(GroupId, resp.ConversationId);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.LeaveGroupRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    [Fact]
    public async Task ChangeMemberRole_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.ChangeMemberRoleAsync(GroupId, MemberId, ConversationMemberRole.Admin);
            Assert.True(resp.Succeeded);
            Assert.Equal(GroupId, resp.ConversationId);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.ChangeMemberRoleRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    [Fact]
    public async Task ListGroupMembers_Request_Pairs_By_RequestId()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var resp = await client.ListGroupMembersAsync(GroupId, pageSize: 20);
            Assert.True(resp.Succeeded);
            Assert.Equal(3, resp.Members?.Count);
            Assert.False(resp.HasMore);
            var seen = server.Frames.Single(f => f.Command == PacketCommand.ListGroupMembersRequest);
            Assert.Equal(seen.RequestId, resp.RequestId);
        });
    }

    // ── 入参校验 ──

    [Fact]
    public async Task CreateGroup_Validation_Rejects_Bad_Input()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => client.CreateGroupAsync("  "));
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => client.CreateGroupAsync(new string('长', 101)));
        });
    }

    [Fact]
    public async Task Group_Commands_Validation_Rejects_Bad_Input()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => client.AddGroupMembersAsync("", [MemberId]));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => client.AddGroupMembersAsync(GroupId, []));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => client.RemoveGroupMemberAsync(GroupId, 0));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => client.LeaveGroupAsync(""));
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => client.ChangeMemberRoleAsync(GroupId, MemberId, (ConversationMemberRole)99));
            await Assert.ThrowsAnyAsync<ArgumentException>(() => client.ListGroupMembersAsync(""));
        });
    }

    // ── 6 类 S2C 群聊通知：触发对应事件 ──

    [Fact]
    public async Task MemberJoined_Update_Raises_Event()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var tcs = new TaskCompletionSource<MemberJoinedUpdateDto>();
            client.GroupMemberJoined += (_, e) => tcs.TrySetResult(e);

            server.Inject(PacketCommand.MemberJoined, new MemberJoinedUpdateDto
            {
                ConversationId = GroupId,
                UserId = MemberId,
                Role = ConversationMemberRole.Member,
                ActorUserId = OwnerId,
                Title = GroupTitle,
                OccurredAtMs = 1234567890
            });

            var e = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(GroupId, e.ConversationId);
            Assert.Equal(MemberId, e.UserId);
            Assert.Equal(GroupTitle, e.Title);
        });
    }

    [Fact]
    public async Task MemberLeft_Update_Raises_Event()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var tcs = new TaskCompletionSource<MemberLeftUpdateDto>();
            client.GroupMemberLeft += (_, e) => tcs.TrySetResult(e);

            server.Inject(PacketCommand.MemberLeft, new MemberLeftUpdateDto
            {
                ConversationId = GroupId,
                UserId = MemberId,
                OccurredAtMs = 1234567890
            });

            var e = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(GroupId, e.ConversationId);
            Assert.Equal(MemberId, e.UserId);
        });
    }

    [Fact]
    public async Task MemberRemoved_Update_Raises_Event()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var tcs = new TaskCompletionSource<MemberRemovedUpdateDto>();
            client.GroupMemberRemoved += (_, e) => tcs.TrySetResult(e);

            server.Inject(PacketCommand.MemberRemoved, new MemberRemovedUpdateDto
            {
                ConversationId = GroupId,
                UserId = MemberId,
                ActorUserId = OwnerId,
                OccurredAtMs = 1234567890
            });

            var e = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(MemberId, e.UserId);
            Assert.Equal(OwnerId, e.ActorUserId);
        });
    }

    [Fact]
    public async Task RoleChanged_Update_Raises_Event()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var tcs = new TaskCompletionSource<RoleChangedUpdateDto>();
            client.GroupRoleChanged += (_, e) => tcs.TrySetResult(e);

            server.Inject(PacketCommand.RoleChanged, new RoleChangedUpdateDto
            {
                ConversationId = GroupId,
                UserId = MemberId,
                NewRole = ConversationMemberRole.Admin,
                PreviousRole = ConversationMemberRole.Member,
                ActorUserId = OwnerId,
                OccurredAtMs = 1234567890
            });

            var e = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ConversationMemberRole.Admin, e.NewRole);
            Assert.Equal(ConversationMemberRole.Member, e.PreviousRole);
        });
    }

    [Fact]
    public async Task MembersAdded_Update_Raises_Event()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var tcs = new TaskCompletionSource<MembersAddedUpdateDto>();
            client.GroupMembersAdded += (_, e) => tcs.TrySetResult(e);

            server.Inject(PacketCommand.MembersAddedUpdate, new MembersAddedUpdateDto
            {
                ConversationId = GroupId,
                AddedUserIds = [MemberId, MemberId2],
                ActorUserId = OwnerId,
                Title = GroupTitle,
                OccurredAtMs = 1234567890
            });

            var e = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([MemberId, MemberId2], e.AddedUserIds);
            Assert.Equal(GroupTitle, e.Title);
        });
    }

    [Fact]
    public async Task ConversationDissolved_Update_Raises_Event()
    {
        await RunAsync(async (client, server, serializer) =>
        {
            var tcs = new TaskCompletionSource<ConversationDissolvedUpdateDto>();
            client.GroupConversationDissolved += (_, e) => tcs.TrySetResult(e);

            server.Inject(PacketCommand.ConversationDissolvedUpdate, new ConversationDissolvedUpdateDto
            {
                ConversationId = GroupId,
                ActorUserId = OwnerId,
                OccurredAtMs = 1234567890
            });

            var e = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(GroupId, e.ConversationId);
            Assert.Equal(OwnerId, e.ActorUserId);
        });
    }

    // ── 测试底座 ──

    private static async Task RunAsync(
        Func<ChatSessionClient, SimServer, IPacketBodySerializer, Task> act)
    {
        using var tcp = new ScriptedTcpClient();
        var serializer = new JsonPacketBodySerializer();
        var client = new ChatSessionClient(tcp, new MessagePacketCodec(), serializer);
        SetupAutoAuth(tcp, serializer, OwnerId);
        var server = new SimServer(tcp, serializer);

        await client.ConnectAsync(new ServerEndpoint { ServerIpAddress = "127.0.0.1", ServerPort = 7000 });
        await client.AuthenticateAsync("token", OwnerId, null, null);
        Assert.True(client.IsAuthenticated);

        await act(client, server, serializer);
    }

    private static void InjectPacket<T>(
        ScriptedTcpClient tcp,
        IPacketBodySerializer serializer,
        PacketCommand command,
        T? payload)
    {
        var writer = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + 64);
        serializer.Serialize(writer, payload);
        var bodyLen = writer.WrittenCount;
        var packet = new MessagePacket(command,
            bodyLen == 0 ? ReadOnlySequence<byte>.Empty : new ReadOnlySequence<byte>(writer.WrittenSpan.ToArray()));
        var frameWriter = new ArrayBufferWriter<byte>(MessagePacket.HeaderSize + bodyLen);
        new MessagePacketCodec().TryWrite(packet, frameWriter, out _);
        tcp.InjectData(frameWriter.WrittenMemory);
    }

    /// <summary>设置自动鉴权响应：收到 AuthRequest 后立即注入 AuthResponse。</summary>
    private static void SetupAutoAuth(ScriptedTcpClient tcp, IPacketBodySerializer serializer, long userId)
    {
        tcp.OnFrameSent += (cmd, _) =>
        {
            if (cmd == PacketCommand.AuthenticationRequest)
            {
                InjectPacket(tcp, serializer, PacketCommand.AuthenticationResponse,
                    new AuthResponseDto { Success = true, UserId = userId });
            }
        };
    }

    /// <summary>模拟服务器：记录群聊请求帧的 RequestId，并按原样回显构造响应；支持主动注入 S2C 通知。</summary>
    private sealed class SimServer
    {
        public List<(PacketCommand Command, string RequestId)> Frames { get; } = [];

        private readonly ScriptedTcpClient _tcp;
        private readonly IPacketBodySerializer _serializer;

        public SimServer(ScriptedTcpClient tcp, IPacketBodySerializer serializer)
        {
            _tcp = tcp;
            _serializer = serializer;
            tcp.OnFrameSent += (cmd, body) =>
            {
                try
                {
                    switch (cmd)
                    {
                        case PacketCommand.CreateGroupRequest:
                            {
                                var req = serializer.Deserialize<CreateGroupRequestDto>(new ReadOnlySequence<byte>(body));
                                if (req is null) return;
                                Frames.Add((cmd, req.RequestId!));
                                Inject(PacketCommand.CreateGroupResponse, new CreateGroupResponseDto
                                {
                                    RequestId = req.RequestId,
                                    Succeeded = true,
                                    ConversationId = GroupId,
                                    Title = req.Title,
                                    Members =
                                    [
                                        new ConversationMemberItemDto { UserId = OwnerId, Role = ConversationMemberRole.Owner, JoinedAtMs = 1234567890 },
                                    .. (req.MemberUserIds ?? []).Select(u => new ConversationMemberItemDto
                                    {
                                        UserId = u,
                                        Role = ConversationMemberRole.Member,
                                        JoinedAtMs = 1234567890
                                    })
                                    ]
                                });
                                break;
                            }
                        case PacketCommand.AddGroupMembersRequest:
                            {
                                var req = serializer.Deserialize<AddGroupMembersRequestDto>(new ReadOnlySequence<byte>(body));
                                if (req is null) return;
                                Frames.Add((cmd, req.RequestId!));
                                Inject(PacketCommand.AddGroupMembersResponse, new AddGroupMembersResponseDto
                                {
                                    RequestId = req.RequestId,
                                    Succeeded = true,
                                    ConversationId = req.ConversationId,
                                    Members = req.MemberUserIds.Select(u => new ConversationMemberItemDto
                                    {
                                        UserId = u,
                                        Role = ConversationMemberRole.Member,
                                        JoinedAtMs = 1234567890
                                    }).ToList()
                                });
                                break;
                            }
                        case PacketCommand.RemoveGroupMemberRequest:
                            {
                                var req = serializer.Deserialize<RemoveGroupMemberRequestDto>(new ReadOnlySequence<byte>(body));
                                if (req is null) return;
                                Frames.Add((cmd, req.RequestId!));
                                Inject(PacketCommand.RemoveGroupMemberResponse, new RemoveGroupMemberResponseDto
                                {
                                    RequestId = req.RequestId,
                                    Succeeded = true,
                                    ConversationId = req.ConversationId
                                });
                                break;
                            }
                        case PacketCommand.LeaveGroupRequest:
                            {
                                var req = serializer.Deserialize<LeaveGroupRequestDto>(new ReadOnlySequence<byte>(body));
                                if (req is null) return;
                                Frames.Add((cmd, req.RequestId!));
                                Inject(PacketCommand.LeaveGroupResponse, new LeaveGroupResponseDto
                                {
                                    RequestId = req.RequestId,
                                    Succeeded = true,
                                    ConversationId = req.ConversationId
                                });
                                break;
                            }
                        case PacketCommand.ChangeMemberRoleRequest:
                            {
                                var req = serializer.Deserialize<ChangeMemberRoleRequestDto>(new ReadOnlySequence<byte>(body));
                                if (req is null) return;
                                Frames.Add((cmd, req.RequestId!));
                                Inject(PacketCommand.ChangeMemberRoleResponse, new ChangeMemberRoleResponseDto
                                {
                                    RequestId = req.RequestId,
                                    Succeeded = true,
                                    ConversationId = req.ConversationId
                                });
                                break;
                            }
                        case PacketCommand.ListGroupMembersRequest:
                            {
                                var req = serializer.Deserialize<ListGroupMembersRequestDto>(new ReadOnlySequence<byte>(body));
                                if (req is null) return;
                                Frames.Add((cmd, req.RequestId!));
                                Inject(PacketCommand.ListGroupMembersResponse, new ListGroupMembersResponseDto
                                {
                                    RequestId = req.RequestId,
                                    Succeeded = true,
                                    ConversationId = req.ConversationId,
                                    Members =
                                    [
                                        new ConversationMemberItemDto { UserId = OwnerId, Role = ConversationMemberRole.Owner, JoinedAtMs = 1234567890 },
                                    new ConversationMemberItemDto { UserId = MemberId, Role = ConversationMemberRole.Member, JoinedAtMs = 1234567890 },
                                    new ConversationMemberItemDto { UserId = MemberId2, Role = ConversationMemberRole.Member, JoinedAtMs = 1234567890 }
                                    ],
                                    HasMore = false
                                });
                                break;
                            }
                    }
                }
                catch
                {
                    // 模拟服务器解析失败：忽略该帧
                }
            };
        }

        public void Inject<T>(PacketCommand command, T? payload) =>
            InjectPacket(_tcp, _serializer, command, payload);
    }

    /// <summary>可控的假 TCP 客户端：解析每次发送的帧，触发回调让测试模拟服务端响应。</summary>
    private sealed class ScriptedTcpClient : ITcpClient
    {
        private volatile bool _connected;

        public bool IsConnected => _connected;

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStatusChanged;
        public event EventHandler<ReadOnlyMemory<byte>>? OnDataChunkReceived;

        /// <summary>每次发送一帧后触发（command, bodyBytes）。</summary>
        public event Action<PacketCommand, ReadOnlyMemory<byte>>? OnFrameSent;

        public Task ConnectAsync(ServerEndpoint endpoint, CancellationToken token = default)
        {
            _connected = true;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Connected));
            return Task.CompletedTask;
        }

        public Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken token = default)
        {
            var seq = new ReadOnlySequence<byte>(data);
            while (seq.Length > 0)
            {
                if (!MessagePacket.TryDeserialize(ref seq, out var pkt, out _))
                    break;
                if (pkt.Command == PacketCommand.ClientHello)
                    OnDataChunkReceived?.Invoke(this, TcpHandshakeTestServer.ServerHelloFrame);
                OnFrameSent?.Invoke(pkt.Command, pkt.Body.ToArray());
            }

            return Task.CompletedTask;
        }

        public Task ReceiveDataAsync(CancellationToken token) => Task.Delay(-1, token);

        public void Disconnect(string? reason = null)
        {
            if (!_connected)
                return;
            _connected = false;
            ConnectionStatusChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, reason));
        }

        public void InjectData(ReadOnlyMemory<byte> chunk)
        {
            OnDataChunkReceived?.Invoke(this, chunk);
        }

        public void Dispose()
        {
            _connected = false;
            GC.SuppressFinalize(this);
        }
    }
}
