using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_OwnerUserId_Status",
                table: "OutboxMessages");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_owner_status_retry",
                table: "OutboxMessages",
                columns: new[] { "OwnerUserId", "Status", "NextRetryAt" });

            migrationBuilder.CreateIndex(
                name: "ix_conversations_owner_list_order",
                table: "Conversations",
                columns: new[] { "OwnerUserId", "IsPinned", "PinnedAtMs", "LastMessageAtMs", "ConversationId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_owner_status_retry",
                table: "OutboxMessages");

            migrationBuilder.DropIndex(
                name: "ix_conversations_owner_list_order",
                table: "Conversations");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_OwnerUserId_Status",
                table: "OutboxMessages",
                columns: new[] { "OwnerUserId", "Status" });
        }
    }
}
