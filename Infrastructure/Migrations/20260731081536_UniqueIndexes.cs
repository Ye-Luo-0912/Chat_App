using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_OwnerUserId_ClientMessageId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_OwnerUserId_MessageId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "ix_attachments_owner_clientattid",
                table: "Attachments");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_OwnerUserId_ClientMessageId",
                table: "Messages",
                columns: new[] { "OwnerUserId", "ClientMessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_OwnerUserId_MessageId",
                table: "Messages",
                columns: new[] { "OwnerUserId", "MessageId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attachments_owner_clientattid",
                table: "Attachments",
                columns: new[] { "OwnerUserId", "ClientAttachmentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_OwnerUserId_ClientMessageId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Messages_OwnerUserId_MessageId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "ix_attachments_owner_clientattid",
                table: "Attachments");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_OwnerUserId_ClientMessageId",
                table: "Messages",
                columns: new[] { "OwnerUserId", "ClientMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_OwnerUserId_MessageId",
                table: "Messages",
                columns: new[] { "OwnerUserId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_owner_clientattid",
                table: "Attachments",
                columns: new[] { "OwnerUserId", "ClientAttachmentId" });
        }
    }
}
