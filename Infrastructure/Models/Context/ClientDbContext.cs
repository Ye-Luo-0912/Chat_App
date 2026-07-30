using Microsoft.EntityFrameworkCore;
using Core.Models;
using Infrastructure.Data;

namespace Infrastructure.Models.Context;

public class ClientDbContext(DbContextOptions<ClientDbContext> options) : DbContext(options)
{
    public DbSet<LocalUser> Users { get; set; }
    public DbSet<ServerEndpoint> Servers { get; set; }
    public DbSet<AuthToken> Tokens { get; set; }
    public DbSet<LocalFriend> Friends { get; set; }

    // ---- P0-6 持久化聊天系统 ----
    public DbSet<LocalConversation> Conversations { get; set; }
    public DbSet<LocalMessage> Messages { get; set; }
    public DbSet<LocalOutboxMessage> OutboxMessages { get; set; }
    public DbSet<LocalSyncCursor> SyncCursors { get; set; }
    public DbSet<LocalConversationReadState> ConversationReadStates { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LocalUser>()
            .HasKey(x => x.UserId);

        modelBuilder.Entity<AuthToken>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<LocalFriend>()
            .HasKey(x => x.Id);

        // 账户隔离：同一账户内好友唯一，防止跨账户数据污染与重复写入（P0-5）。
        modelBuilder.Entity<LocalFriend>()
            .HasIndex(x => new { x.OwnerUserId, x.FriendId })
            .IsUnique();

        modelBuilder.Entity<ServerEndpoint>()
            .HasIndex(s => new { s.ServerIpAddress, s.ServerPort });

        // ---- 本地会话（P0-6 持久化聊天系统）----
        modelBuilder.Entity<LocalConversation>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<LocalConversation>()
            .HasIndex(x => new { x.OwnerUserId, x.ConversationId })
            .IsUnique();

        // ---- 本地消息（P0-6）----
        modelBuilder.Entity<LocalMessage>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<LocalMessage>()
            .HasIndex(x => new { x.OwnerUserId, x.ConversationId });
        modelBuilder.Entity<LocalMessage>()
            .HasIndex(x => new { x.OwnerUserId, x.MessageId });
        modelBuilder.Entity<LocalMessage>()
            .HasIndex(x => new { x.OwnerUserId, x.ClientMessageId });

        // ---- 发送 Outbox（P0-6）----
        modelBuilder.Entity<LocalOutboxMessage>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<LocalOutboxMessage>()
            .HasIndex(x => new { x.OwnerUserId, x.ClientMessageId })
            .IsUnique();
        modelBuilder.Entity<LocalOutboxMessage>()
            .HasIndex(x => new { x.OwnerUserId, x.Status });

        // ---- 同步水位（P0-6）----
        modelBuilder.Entity<LocalSyncCursor>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<LocalSyncCursor>()
            .HasIndex(x => new { x.OwnerUserId, x.ConversationId })
            .IsUnique();

        // ---- 会话已读状态（P0-6）----
        modelBuilder.Entity<LocalConversationReadState>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<LocalConversationReadState>()
            .HasIndex(x => new { x.OwnerUserId, x.ConversationId })
            .IsUnique();

        base.OnModelCreating(modelBuilder);
    }
}
