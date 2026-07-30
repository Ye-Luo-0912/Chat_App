using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_messages_owner_conv_time",
                table: "Messages",
                columns: new[] { "OwnerUserId", "ConversationId", "ReceivedAtMs" });

            migrationBuilder.CreateIndex(
                name: "ix_messages_owner_conv_time_msgid",
                table: "Messages",
                columns: new[] { "OwnerUserId", "ConversationId", "ReceivedAtMs", "MessageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_owner_conv_time",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_owner_conv_time_msgid",
                table: "Messages");
        }
    }
}
