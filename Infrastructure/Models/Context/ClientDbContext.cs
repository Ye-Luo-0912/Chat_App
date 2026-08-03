using Microsoft.EntityFrameworkCore;
using Core.Models;
using Chat_App.Infrastructure.Models;
using Chat_App.Infrastructure.Persistence;

namespace Chat_App.Infrastructure.Models.Context;

public class ClientDbContext(DbContextOptions<ClientDbContext> options) : DbContext(options)
{
    /// <summary>当前数据库文件路径（用于日志/错误提示）。</summary>
    public string DbPath => DbPathProvider.DbPath;

    public DbSet<LocalUser> Users { get; set; }
    public DbSet<ServerEndpoint> Servers { get; set; }
    public DbSet<AuthToken> Tokens { get; set; }
    public DbSet<LocalFriend> Friends { get; set; }

    // ---- 持久化聊天系统 ----
    public DbSet<LocalConversation> Conversations { get; set; }
    public DbSet<LocalMessage> Messages { get; set; }
    public DbSet<LocalOutboxMessage> OutboxMessages { get; set; }
    public DbSet<LocalSyncCursor> SyncCursors { get; set; }
    public DbSet<LocalConversationReadState> ConversationReadStates { get; set; }

    // ---- 阶段 3 附件元数据 ----
    public DbSet<LocalAttachment> Attachments => Set<LocalAttachment>();

    // PRAGMA 由 SqlitePragmaInterceptor（EF Core DbConnectionInterceptor，在 AddPooledDbContextFactory
    // 的 options 中注册）在每个连接打开时统一执行，不在此处调用 ExecuteSqlRaw。
    // OnConfiguring 由池化工厂在租用上下文时调用一次，不适合做 PRAGMA。

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LocalUser>()
            .HasKey(x => x.UserId);

        modelBuilder.Entity<AuthToken>()
            .HasKey(x => x.Id);

        modelBuilder.Entity<LocalFriend>()
            .HasKey(x => x.Id);

        // 账户隔离：同一账户内好友唯一，防止跨账户数据污染与重复写入。
        modelBuilder.Entity<LocalFriend>()
            .HasIndex(x => new { x.OwnerUserId, x.FriendId })
            .IsUnique();

        modelBuilder.Entity<ServerEndpoint>()
            .HasIndex(s => new { s.ServerIpAddress, s.ServerPort });

        // ---- 本地会话（持久化聊天系统）----
        modelBuilder.Entity<LocalConversation>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<LocalConversation>()
            .HasIndex(x => new { x.OwnerUserId, x.ConversationId })
            .IsUnique();

        // ---- 本地消息----
        modelBuilder.Entity<LocalMessage>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<LocalMessage>()
            .HasIndex(x => new { x.OwnerUserId, x.ConversationId });
        // 服务端消息 Id 唯一：同一账户内同一 MessageId 仅允许一行。
        // SQLite 中 NULL 视为互不相同，因此未 ack 的出站消息（MessageId 为 NULL）不会冲突。
        modelBuilder.Entity<LocalMessage>()
            .HasIndex(x => new { x.OwnerUserId, x.MessageId })
            .IsUnique();
        // 客户端消息 Id 唯一：同一账户内同一 ClientMessageId 仅允许一行。
        // SQLite 中 NULL 视为互不相同，因此入站消息（ClientMessageId 为 NULL）不会冲突。
        modelBuilder.Entity<LocalMessage>()
            .HasIndex(x => new { x.OwnerUserId, x.ClientMessageId })
            .IsUnique();
        // 游标分页查询覆盖索引：按会话+时间倒序取一页
        modelBuilder.Entity<LocalMessage>()
            .HasIndex(x => new { x.OwnerUserId, x.ConversationId, x.ReceivedAtMs })
            .HasDatabaseName("ix_messages_owner_conv_time");

        // GetMessagesAfter 查询索引
        modelBuilder.Entity<LocalMessage>()
            .HasIndex(x => new { x.OwnerUserId, x.ConversationId, x.ReceivedAtMs, x.MessageId })
            .HasDatabaseName("ix_messages_owner_conv_time_msgid");

        // ---- 发送 Outbox----
        modelBuilder.Entity<LocalOutboxMessage>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<LocalOutboxMessage>()
            .HasIndex(x => new { x.OwnerUserId, x.ClientMessageId })
            .IsUnique();
        modelBuilder.Entity<LocalOutboxMessage>()
            .HasIndex(x => new { x.OwnerUserId, x.Status });

        // ---- 同步水位----
        modelBuilder.Entity<LocalSyncCursor>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<LocalSyncCursor>()
            .HasIndex(x => new { x.OwnerUserId, x.ConversationId })
            .IsUnique();

        // ---- 会话已读状态----
        modelBuilder.Entity<LocalConversationReadState>()
            .HasKey(x => x.Id);
        modelBuilder.Entity<LocalConversationReadState>()
            .HasIndex(x => new { x.OwnerUserId, x.ConversationId })
            .IsUnique();

        // ---- 附件元数据（阶段 3）----
        var entity = modelBuilder.Entity<LocalAttachment>();
        entity.HasKey(e => e.Id);
        entity.Property(e => e.OwnerUserId).IsRequired();
        entity.Property(e => e.AttachmentId).HasMaxLength(128);
        entity.Property(e => e.ClientAttachmentId).HasMaxLength(128);
        entity.Property(e => e.MessageId).HasMaxLength(64);
        entity.Property(e => e.ConversationId).HasMaxLength(128);
        entity.Property(e => e.FileName).HasMaxLength(512);
        entity.Property(e => e.ContentType).HasMaxLength(256).IsRequired();
        entity.Property(e => e.Sha256).HasMaxLength(64);
        entity.Property(e => e.DownloadPath).HasMaxLength(512);
        entity.Property(e => e.ObjectKey).HasMaxLength(512);
        entity.Property(e => e.ThumbnailPath).HasMaxLength(512);
        entity.Property(e => e.LocalCachePath).HasMaxLength(512);
        entity.Property(e => e.LocalThumbnailPath).HasMaxLength(512);
        entity.Property(e => e.LocalUploadingPath).HasMaxLength(512);
        entity.Property(e => e.RetryCount);
        entity.Property(e => e.FailureReason).HasMaxLength(1024);
        entity.HasIndex(e => new { e.OwnerUserId, e.AttachmentId }).IsUnique().HasDatabaseName("ix_attachments_owner_attid");
        // 客户端附件 Id 唯一：同一账户内同一 ClientAttachmentId 仅允许一行。
        // SQLite 中 NULL 视为互不相同，因此服务端来源附件（ClientAttachmentId 为 NULL）不会冲突。
        entity.HasIndex(e => new { e.OwnerUserId, e.ClientAttachmentId }).IsUnique().HasDatabaseName("ix_attachments_owner_clientattid");
        entity.HasIndex(e => new { e.OwnerUserId, e.MessageId }).HasDatabaseName("ix_attachments_owner_msgid");
        entity.HasIndex(e => new { e.OwnerUserId, e.Sha256 }).HasDatabaseName("ix_attachments_owner_sha256");

        base.OnModelCreating(modelBuilder);
    }
}
