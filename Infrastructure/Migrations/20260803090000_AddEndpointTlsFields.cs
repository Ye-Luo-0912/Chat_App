using Chat_App.Infrastructure.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// Servers 表 TLS 字段迁移。
    /// 注意：此迁移历史上缺少 [DbContext]/[Migration] 特性与 Designer 文件，
    /// 导致 EF 从未发现并应用它（模型含 UseTls/TlsServerName，库中缺失）。
    /// 补上特性后按迁移 ID 顺序正常应用。
    /// </summary>
    [DbContext(typeof(ClientDbContext))]
    [Migration("20260803090000_AddEndpointTlsFields")]
    public partial class AddEndpointTlsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UseTls",
                table: "Servers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TlsServerName",
                table: "Servers",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TlsServerName",
                table: "Servers");

            migrationBuilder.DropColumn(
                name: "UseTls",
                table: "Servers");
        }
    }
}
