using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat_App.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationDraft : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Draft",
                table: "Conversations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Draft",
                table: "Conversations");
        }
    }
}
