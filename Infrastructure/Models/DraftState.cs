using System.Collections.Generic;

namespace Chat_App.Infrastructure.Models;

/// <summary>草稿回复目标：引用待回复的消息。</summary>
public sealed class DraftReplyTarget
{
    public string MessageId { get; init; } = string.Empty;
    public string? Preview { get; init; }
    public long? SenderUserId { get; init; }
}

/// <summary>草稿编辑目标：引用待编辑的消息。</summary>
public sealed class DraftEditTarget
{
    public string MessageId { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public int EditVersion { get; init; }
}

/// <summary>草稿附件项（附件已上传到服务端，仅记录元数据用于恢复；语音附件携带 VOICE-MSG-2 元数据）。</summary>
public sealed class DraftAttachment
{
    public string AttachmentId { get; init; } = string.Empty;
    public string? FileName { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public long SizeBytes { get; init; }

    public bool IsVoice { get; init; }
    public string? VoiceCodec { get; init; }
    public string? VoiceContainer { get; init; }
    public long? VoiceDurationMs { get; init; }
    public int? VoiceSampleRateHz { get; init; }
    public short? VoiceChannels { get; init; }
}

/// <summary>
/// 完整会话草稿快照：文本 + 回复目标 + 编辑目标 + 待发送附件。
/// 序列化为 JSON 持久化；UpdatedAtMs/Revision 用于多窗口乐观并发（新者胜）。
/// </summary>
public sealed class DraftState
{
    public string Text { get; init; } = string.Empty;
    public DraftReplyTarget? ReplyTarget { get; init; }
    public DraftEditTarget? EditTarget { get; init; }
    public List<DraftAttachment>? Attachments { get; init; }
    public long UpdatedAtMs { get; init; }
    public int Revision { get; init; }
}
