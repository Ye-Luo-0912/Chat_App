using System;
using System.Text;

namespace Chat_App.Infrastructure.Persistence;

/// <summary>
/// FTS5 查询构造器：把用户输入转为安全的 FTS MATCH 表达式与 LIKE 兜底模式。
///
/// 分词事实：SQLite unicode61 分词器把连续 CJK 字符合并为单个 token
///（"你好世界" 是一个 token）——只有前缀能命中（"你好世"* 命中，"世界"* 不命中）。
/// 因此：
/// - FTS MATCH：每个词作为前缀短语（"词"*），索引加速命中以词开头的消息；
/// - LIKE 兜底：非前缀子串（词在中间/末尾）由 LIKE '%词%' 补全（本地数据量可接受）。
/// 两者以 OR 组合，返回完整子串匹配。
/// 特殊字符全部转义/替换，杜绝 FTS 或 LIKE 注入。
/// </summary>
public static class FtsQueryBuilder
{
    private static readonly char[] FtsSpecialChars = { '"', '*', ':', '^', '-', '+', '(', ')', '{', '}', '[', ']', '~', '\\' };

    /// <summary>
    /// 用户查询 → FTS MATCH 表达式（每个词为前缀短语 "词"*）；
    /// 空/空白返回 null。
    /// </summary>
    public static string? BuildMatchQuery(string? userQuery)
    {
        if (string.IsNullOrWhiteSpace(userQuery))
            return null;

        var terms = userQuery.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var parts = new string[terms.Length];
        for (var i = 0; i < terms.Length; i++)
            parts[i] = $"\"{EscapeTerm(terms[i])}\"*";
        return string.Join(" AND ", parts);
    }

    /// <summary>
    /// 用户查询 → LIKE 兜底模式（%词%）；空/空白返回 null。
    /// LIKE 通配符 % _ 与转义符 \ 均转义。
    /// </summary>
    public static string? BuildLikePattern(string? userQuery)
    {
        if (string.IsNullOrWhiteSpace(userQuery))
            return null;

        var sb = new StringBuilder(userQuery.Length + 2);
        sb.Append('%');
        foreach (var c in userQuery)
        {
            if (c is '%' or '_' or '\\')
                sb.Append('\\'); // LIKE ESCAPE '\'
            sb.Append(c);
        }
        sb.Append('%');
        return sb.ToString();
    }

    /// <summary>转义单个词中的 FTS 特殊字符；特殊字符替换为空格（token 分隔符）。</summary>
    private static string EscapeTerm(string term)
    {
        var sb = new StringBuilder(term.Length);
        foreach (var c in term)
        {
            if (c == '"' || Array.IndexOf(FtsSpecialChars, c) >= 0)
                sb.Append(' ');
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}
