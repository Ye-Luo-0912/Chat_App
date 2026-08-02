using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat_App.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAttachmentUploadFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LocalUploadingPath",
                table: "Attachments",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RetryCount",
                table: "Attachments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LocalUploadingPath",
                table: "Attachments");

            migrationBuilder.DropColumn(
                name: "RetryCount",
                table: "Attachments");
        }
    }
}
