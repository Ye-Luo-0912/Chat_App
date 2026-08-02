using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat_App.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSessionInfoAndUserProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Gender",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreviousLoginDate",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Signature",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "Status",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AlterColumn<string>(
                name: "RefreshToken",
                table: "Tokens",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "AccessToken",
                table: "Tokens",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DeviceIdHash",
                table: "Tokens",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SessionId",
                table: "Tokens",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "FriendName",
                table: "Friends",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Friends",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<int>(
                name: "GroupId",
                table: "Friends",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupName",
                table: "Friends",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Friends",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Email",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Gender",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PreviousLoginDate",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Signature",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "DeviceIdHash",
                table: "Tokens");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "Tokens");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Friends");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Friends");

            migrationBuilder.DropColumn(
                name: "GroupName",
                table: "Friends");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "Friends");

            migrationBuilder.AlterColumn<string>(
                name: "RefreshToken",
                table: "Tokens",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "AccessToken",
                table: "Tokens",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<string>(
                name: "FriendName",
                table: "Friends",
                type: "TEXT",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
