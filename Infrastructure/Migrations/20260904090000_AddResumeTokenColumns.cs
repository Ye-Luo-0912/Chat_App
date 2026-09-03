using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat_App.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddResumeTokenColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResumeToken",
                table: "Tokens",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ResumeTokenUpdatedAtMs",
                table: "Tokens",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ResumeToken",
                table: "Tokens");

            migrationBuilder.DropColumn(
                name: "ResumeTokenUpdatedAtMs",
                table: "Tokens");
        }
    }
}
