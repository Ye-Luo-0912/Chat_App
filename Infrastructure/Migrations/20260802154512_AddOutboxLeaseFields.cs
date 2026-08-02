using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxLeaseFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AttemptId",
                table: "OutboxMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AttemptStartedAt",
                table: "OutboxMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<byte>(
                name: "FailureKind",
                table: "OutboxMessages",
                type: "INTEGER",
                nullable: false,
                defaultValue: (byte)0);

            migrationBuilder.AddColumn<string>(
                name: "LastErrorCode",
                table: "OutboxMessages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LeaseUntil",
                table: "OutboxMessages",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AttemptId",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "AttemptStartedAt",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "FailureKind",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "LastErrorCode",
                table: "OutboxMessages");

            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                table: "OutboxMessages");
        }
    }
}
