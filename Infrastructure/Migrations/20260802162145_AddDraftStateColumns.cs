using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDraftStateColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DraftRevision",
                table: "Conversations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DraftState",
                table: "Conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DraftUpdatedAtMs",
                table: "Conversations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DraftRevision",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "DraftState",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "DraftUpdatedAtMs",
                table: "Conversations");
        }
    }
}
