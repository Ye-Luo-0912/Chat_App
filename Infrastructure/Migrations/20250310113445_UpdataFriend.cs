using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat_App.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdataFriend : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 账户隔离重构为 Friends 增加了 OwnerUserId，但历史上从未生成对应迁移，
            // 迁移快照被重新生成后 EF 的表重建 INSERT-SELECT 会引用该列，
            // 在严格模式 SQLite（SQLitePCLRaw.lib.e_sqlite3 2.1.12+）下全新安装迁移必然失败。
            // 此处补加该列（默认值 0 兜底存量行），保证历史迁移链与模型自洽。
            // 注意：此迁移已被存量数据库应用过（EF 按 ID 判重，不做校验和），
            // 存量库无需重跑；该列在存量库由实际数据流填充。
            migrationBuilder.AddColumn<long>(
                name: "OwnerUserId",
                table: "Friends",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

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

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "Friends");
        }
    }
}
