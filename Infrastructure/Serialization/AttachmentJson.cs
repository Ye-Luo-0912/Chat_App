using System.Collections.Generic;
using System.Text.Json;
using Core.Models.DTO;

namespace Infrastructure.Serialization;

/// <summary>
/// 附件 JSON 序列化/反序列化的统一入口（P0-代码复用）。
/// 所有附件元数据与附件 Id 列表的 (de)serialize 均经由此处，
/// 统一使用 <see cref="ChatJsonContext.Default.Options"/>，避免散落各处的 try-catch 与选项漂移。
/// </summary>
public static class AttachmentJson
{
    /// <summary>序列化附件元数据列表；null 或空集合返回 null（DB 列可空，避免存 "[]"）。</summary>
    public static string? Serialize(IReadOnlyList<AttachmentRefDto>? attachments)
        => attachments is null || attachments.Count == 0
            ? null
            : JsonSerializer.Serialize(attachments, ChatJsonContext.Default.Options);

    /// <summary>序列化附件 Id 列表；null 或空集合返回 null。</summary>
    public static string? SerializeIds(IReadOnlyList<string>? ids)
        => ids is null || ids.Count == 0
            ? null
            : JsonSerializer.Serialize(ids, ChatJsonContext.Default.Options);

    /// <summary>反序列化附件元数据列表；空白或解析失败返回 null（best-effort，损坏 JSON 不抛异常）。</summary>
    public static IReadOnlyList<AttachmentRefDto>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<AttachmentRefDto>>(json, ChatJsonContext.Default.Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>反序列化附件 Id 列表；空白或解析失败返回 null。</summary>
    public static IReadOnlyList<string>? DeserializeIds(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, ChatJsonContext.Default.Options);
        }
        catch
        {
            return null;
        }
    }
}
