using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRelationshipProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RelationshipProjections",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ListType = table.Column<byte>(type: "INTEGER", nullable: false),
                    ResourceId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    OccurredAtMs = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelationshipProjections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RelationshipWatermarks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ListType = table.Column<byte>(type: "INTEGER", nullable: false),
                    AfterSequence = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RelationshipWatermarks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_relationship_projection_owner_type_resource",
                table: "RelationshipProjections",
                columns: new[] { "OwnerUserId", "ListType", "ResourceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RelationshipProjections_OwnerUserId_ListType_IsDeleted_CreatedAtMs",
                table: "RelationshipProjections",
                columns: new[] { "OwnerUserId", "ListType", "IsDeleted", "CreatedAtMs" });

            migrationBuilder.CreateIndex(
                name: "ix_relationship_watermark_owner_type",
                table: "RelationshipWatermarks",
                columns: new[] { "OwnerUserId", "ListType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RelationshipProjections");

            migrationBuilder.DropTable(
                name: "RelationshipWatermarks");
        }
    }
}
