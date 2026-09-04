namespace Core.Models;

/// <summary>
/// 群组通话（Mesh 阶段一）成员变更事件参数：成员加入（participant-joined）或离开（participant-left）。
/// <para>事件由 <see cref="CallSession"/> 在成员集合可见变化后触发，携带会话与涉事成员 Id。</para>
/// </summary>
public sealed class CallParticipantEventArgs : EventArgs
{
    public CallParticipantEventArgs(CallSession session, long participantUserId)
    {
        Session = session;
        ParticipantUserId = participantUserId;
    }

    /// <summary>发生成员变更的通话会话。</summary>
    public CallSession Session { get; }

    /// <summary>加入/离开的成员用户 Id。</summary>
    public long ParticipantUserId { get; }
}
