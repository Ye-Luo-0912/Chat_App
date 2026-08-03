using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupDomainEntities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Role = table.Column<byte>(type: "INTEGER", nullable: false),
                    JoinedAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    RemovedAtMs = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupStates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ConversationId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    MemberRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    ConversationRevision = table.Column<long>(type: "INTEGER", nullable: false),
                    LastEventAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    DissolvedAtMs = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_group_members_owner_conv_user",
                table: "GroupMembers",
                columns: new[] { "OwnerUserId", "ConversationId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_group_states_owner_conv",
                table: "GroupStates",
                columns: new[] { "OwnerUserId", "ConversationId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupMembers");

            migrationBuilder.DropTable(
                name: "GroupStates");
        }
    }
}
