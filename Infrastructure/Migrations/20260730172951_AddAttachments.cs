using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    AttachmentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ClientAttachmentId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    MessageId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DownloadPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ObjectKey = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    ThumbnailPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    LocalCachePath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    LocalThumbnailPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Status = table.Column<byte>(type: "INTEGER", nullable: false),
                    FailureReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_owner_attid",
                table: "Attachments",
                columns: new[] { "OwnerUserId", "AttachmentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attachments_owner_clientattid",
                table: "Attachments",
                columns: new[] { "OwnerUserId", "ClientAttachmentId" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_owner_msgid",
                table: "Attachments",
                columns: new[] { "OwnerUserId", "MessageId" });

            migrationBuilder.CreateIndex(
                name: "ix_attachments_owner_sha256",
                table: "Attachments",
                columns: new[] { "OwnerUserId", "Sha256" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Attachments");
        }
    }
}
