using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat_App.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPersistenceChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConversationReadStates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                    LastReadMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    LastReadAtMs = table.Column<long>(type: "INTEGER", nullable: true),
                    UnreadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationReadStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<byte>(type: "INTEGER", nullable: false),
                    PeerUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    LastMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    LastMessagePreview = table.Column<string>(type: "TEXT", nullable: true),
                    LastMessageAtMs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSenderUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UnreadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastReadMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    LastReadAtMs = table.Column<long>(type: "INTEGER", nullable: true),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    PinnedAtMs = table.Column<long>(type: "INTEGER", nullable: true),
                    IsMuted = table.Column<bool>(type: "INTEGER", nullable: false),
                    MutedUntilMs = table.Column<long>(type: "INTEGER", nullable: true),
                    LastSynced = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", nullable: true),
                    ClientMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                    SenderUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceiverUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    ReceivedAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    DeliveredAtMs = table.Column<long>(type: "INTEGER", nullable: true),
                    ReadAtMs = table.Column<long>(type: "INTEGER", nullable: true),
                    RecalledAtMs = table.Column<long>(type: "INTEGER", nullable: true),
                    EditVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    EditedAtMs = table.Column<long>(type: "INTEGER", nullable: true),
                    AttachmentsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ReplyToMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    ReplyToSenderUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    ReplyToPreview = table.Column<string>(type: "TEXT", nullable: true),
                    ForwardedFromMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    ForwardedFromSenderUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    ForwardedFromPreview = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ClientMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    MessageId = table.Column<string>(type: "TEXT", nullable: true),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                    TargetUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: true),
                    AttachmentIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ReplyToMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    ReplyToSenderUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    ReplyToPreview = table.Column<string>(type: "TEXT", nullable: true),
                    ForwardedFromMessageId = table.Column<string>(type: "TEXT", nullable: true),
                    ForwardedFromSenderUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    ForwardedFromPreview = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    QueuedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRetryAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncCursors",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", nullable: false),
                    AfterReceivedAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    AfterMessageId = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncCursors", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationReadStates_OwnerUserId_ConversationId",
                table: "ConversationReadStates",
                columns: new[] { "OwnerUserId", "ConversationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_OwnerUserId_ConversationId",
                table: "Conversations",
                columns: new[] { "OwnerUserId", "ConversationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_OwnerUserId_ClientMessageId",
                table: "Messages",
                columns: new[] { "OwnerUserId", "ClientMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_OwnerUserId_ConversationId",
                table: "Messages",
                columns: new[] { "OwnerUserId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_OwnerUserId_MessageId",
                table: "Messages",
                columns: new[] { "OwnerUserId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_OwnerUserId_ClientMessageId",
                table: "OutboxMessages",
                columns: new[] { "OwnerUserId", "ClientMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_OwnerUserId_Status",
                table: "OutboxMessages",
                columns: new[] { "OwnerUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_SyncCursors_OwnerUserId_ConversationId",
                table: "SyncCursors",
                columns: new[] { "OwnerUserId", "ConversationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationReadStates");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "OutboxMessages");

            migrationBuilder.DropTable(
                name: "SyncCursors");
        }
    }
}
