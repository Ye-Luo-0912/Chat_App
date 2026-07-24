using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdataFriend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<byte>(
                name: "Status",
                table: "Friends",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "Friends",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Note",
                table: "Friends");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "Friends",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(byte),
                oldType: "INTEGER");
        }
    }
}
