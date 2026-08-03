using Chat_App.Infrastructure.Models.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat_App.Infrastructure.Migrations
{
    /// <summary>
    /// Conversations.Draft 列迁移。
    /// 注意：此迁移历史上缺少 [DbContext]/[Migration] 特性与 Designer 文件，
    /// 导致 EF 从未发现并应用它，模型与库结构长期漂移（模型含 Draft，库中无该列，
    /// 任何按当前模型 INSERT Conversations 都会失败）。补上特性后按迁移 ID 顺序正常应用。
    /// </summary>
    [DbContext(typeof(ClientDbContext))]
    [Migration("20260731120000_AddConversationDraft")]
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
