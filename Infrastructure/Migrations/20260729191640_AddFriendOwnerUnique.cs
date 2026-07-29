using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddFriendOwnerUnique : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsMuted",
                table: "Friends",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsOnline",
                table: "Friends",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPinned",
                table: "Friends",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LastMessagePreview",
                table: "Friends",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MutedUntilMs",
                table: "Friends",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PinnedAtMs",
                table: "Friends",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Friends_OwnerUserId_FriendId",
                table: "Friends",
                columns: new[] { "OwnerUserId", "FriendId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Friends_OwnerUserId_FriendId",
                table: "Friends");

            migrationBuilder.DropColumn(
                name: "IsMuted",
                table: "Friends");

            migrationBuilder.DropColumn(
                name: "IsOnline",
                table: "Friends");

            migrationBuilder.DropColumn(
                name: "IsPinned",
                table: "Friends");

            migrationBuilder.DropColumn(
                name: "LastMessagePreview",
                table: "Friends");

            migrationBuilder.DropColumn(
                name: "MutedUntilMs",
                table: "Friends");

            migrationBuilder.DropColumn(
                name: "PinnedAtMs",
                table: "Friends");
        }
    }
}
