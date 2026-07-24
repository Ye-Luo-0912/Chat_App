using System.Globalization;

namespace Core.Helpers;

/// <summary>单聊会话 Id：<c>dm:{minUserId}:{maxUserId}</c>。</summary>
public static class ConversationId
{
    private const string DirectPrefix = "dm:";

    public static string CreateDirect(long userA, long userB)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userA);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(userB);
        if (userA == userB)
            throw new ArgumentException("单聊双方用户编号不能相同。", nameof(userB));

        var lo = Math.Min(userA, userB);
        var hi = Math.Max(userA, userB);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{DirectPrefix}{lo}:{hi}");
    }

    public static bool TryParseDirect(string? conversationId, out long userLo, out long userHi)
    {
        userLo = 0;
        userHi = 0;
        if (string.IsNullOrWhiteSpace(conversationId)
            || !conversationId.StartsWith(DirectPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = conversationId.AsSpan(DirectPrefix.Length);
        var sep = rest.IndexOf(':');
        if (sep <= 0 || sep >= rest.Length - 1)
            return false;

        if (!long.TryParse(rest[..sep], NumberStyles.None, CultureInfo.InvariantCulture, out userLo)
            || !long.TryParse(rest[(sep + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out userHi)
            || userLo <= 0
            || userHi <= 0
            || userLo >= userHi)
        {
            userLo = 0;
            userHi = 0;
            return false;
        }

        return true;
    }

    public static long? TryGetPeerUserId(string? conversationId, long selfUserId)
    {
        if (!TryParseDirect(conversationId, out var lo, out var hi))
            return null;
        if (selfUserId == lo)
            return hi;
        if (selfUserId == hi)
            return lo;
        return null;
    }
}
