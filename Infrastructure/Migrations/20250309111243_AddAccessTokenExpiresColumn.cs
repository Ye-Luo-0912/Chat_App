using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessTokenExpiresColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Servers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ServerName = table.Column<string>(type: "TEXT", nullable: false),
                    ServerIpAddress = table.Column<string>(type: "TEXT", nullable: false),
                    ServerPort = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastConnected = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Servers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tokens",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    AccessToken = table.Column<string>(type: "TEXT", nullable: true),
                    AccessTokenExpires = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RefreshToken = table.Column<string>(type: "TEXT", nullable: true),
                    RefreshTokenExpires = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    AvatarUrl = table.Column<string>(type: "TEXT", nullable: true),
                    LastLoginTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.UserId);
                });

            migrationBuilder.CreateTable(
                name: "Friends",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<int>(type: "INTEGER", nullable: false),
                    FriendId = table.Column<long>(type: "INTEGER", nullable: false),
                    FriendName = table.Column<string>(type: "TEXT", nullable: false),
                    AvatarUrl = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: false),
                    LastSynced = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LocalUserUserId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Friends", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Friends_Users_LocalUserUserId",
                        column: x => x.LocalUserUserId,
                        principalTable: "Users",
                        principalColumn: "UserId");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Friends_LocalUserUserId",
                table: "Friends",
                column: "LocalUserUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_ServerIpAddress_ServerPort",
                table: "Servers",
                columns: new[] { "ServerIpAddress", "ServerPort" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Friends");

            migrationBuilder.DropTable(
                name: "Servers");

            migrationBuilder.DropTable(
                name: "Tokens");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
