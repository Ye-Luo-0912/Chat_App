using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <summary>
    /// SQLite FTS5 本地消息全文搜索：
    /// - MessagesFts 外部内容表（content='Messages'，rowid 对齐 Messages.Id）
    /// - AI/AD/AU 触发器保证 Messages 的增删改自动同步索引（应用层零改动）
    /// - unicode61 分词器：中文按单字 token，短语查询（"词组"）可匹配连续 token
    /// </summary>
    public partial class AddMessageFtsSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE VIRTUAL TABLE "MessagesFts" USING fts5(
                    OwnerUserId,
                    ConversationId,
                    Content,
                    content='Messages',
                    content_rowid='Id',
                    tokenize='unicode61'
                );
                """);

            // 插入同步
            migrationBuilder.Sql("""
                CREATE TRIGGER "MessagesFts_AI" AFTER INSERT ON "Messages" BEGIN
                    INSERT INTO "MessagesFts" (rowid, OwnerUserId, ConversationId, Content)
                    VALUES (new."Id", new."OwnerUserId", new."ConversationId", new."Content");
                END;
                """);

            // 更新同步（外部内容表不支持 UPDATE，需 delete+insert）
            migrationBuilder.Sql("""
                CREATE TRIGGER "MessagesFts_AU" AFTER UPDATE ON "Messages" BEGIN
                    INSERT INTO "MessagesFts" ("MessagesFts", rowid, OwnerUserId, ConversationId, Content)
                    VALUES ('delete', old."Id", old."OwnerUserId", old."ConversationId", old."Content");
                    INSERT INTO "MessagesFts" (rowid, OwnerUserId, ConversationId, Content)
                    VALUES (new."Id", new."OwnerUserId", new."ConversationId", new."Content");
                END;
                """);

            // 删除同步
            migrationBuilder.Sql("""
                CREATE TRIGGER "MessagesFts_AD" AFTER DELETE ON "Messages" BEGIN
                    INSERT INTO "MessagesFts" ("MessagesFts", rowid, OwnerUserId, ConversationId, Content)
                    VALUES ('delete', old."Id", old."OwnerUserId", old."ConversationId", old."Content");
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"MessagesFts_AD\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"MessagesFts_AU\";");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS \"MessagesFts_AI\";");
            migrationBuilder.Sql("DROP TABLE IF EXISTS \"MessagesFts\";");
        }
    }
}
