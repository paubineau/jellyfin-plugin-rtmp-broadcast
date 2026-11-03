using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.RtmpBroadcast.Chat;

// Continuation rows contain only message text. Color, when set, is #RRGGBB.
internal sealed record ChatLine(string? Sender, string Text, string? Color);

// Keep just enough messages for the video overlay. IDs stay in memory so a
// moderation command can remove a line even when a display name has changed.
internal sealed class ChatHistory
{
    private const int Rows = 4;
    private const int Columns = 44;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(20);
    private readonly List<Entry> entries = [];
    private readonly object gate = new();

    private sealed record Entry(string Id, string Login, string UserId, ChatLine[] Lines, long ReceivedAt);

    public void Add(string id, string login, string userId, string displayName, string color, string message)
    {
        if (message.StartsWith("\u0001ACTION ", StringComparison.Ordinal) && message.EndsWith('\u0001'))
            message = message[8..^1];
        var text = Clean(message, Columns * 2);
        if (text.Length == 0) return;
        var name = Clean(displayName, 20);
        if (name.Length == 0) name = Clean(login, 20);
        if (name.Length == 0) return;
        if (Fit(name, 20) < name.Length) name = name[..Fit(name, 19)] + "…";
        var senderColor = color.Length == 7 && color[0] == '#' && color.Skip(1).All(char.IsAsciiHexDigit)
            ? color.ToUpperInvariant() : null;
        var lines = Wrap(name, text, senderColor);
        lock (gate)
        {
            if (id.Length > 0 && entries.Any(e => e.Id == id)) return;
            if (entries.Count == Rows) entries.RemoveAt(0);
            entries.Add(new(id, login, userId, lines, TimeProvider.System.GetTimestamp()));
        }
    }

    public void RemoveMessage(string id)
    {
        if (id.Length == 0) return;
        lock (gate) entries.RemoveAll(e => e.Id == id);
    }

    public void RemoveUser(string login, string userId)
    {
        lock (gate) entries.RemoveAll(e => (userId.Length > 0 && e.UserId == userId)
            || (login.Length > 0 && string.Equals(e.Login, login, StringComparison.OrdinalIgnoreCase)));
    }

    public void Clear()
    {
        lock (gate) entries.Clear();
    }

    public IReadOnlyList<ChatLine> Snapshot()
    {
        lock (gate)
        {
            entries.RemoveAll(e => TimeProvider.System.GetElapsedTime(e.ReceivedAt) >= Lifetime);
            var first = entries.Count;
            var rows = 0;
            while (first > 0 && rows + entries[first - 1].Lines.Length <= Rows)
                rows += entries[--first].Lines.Length;
            return entries.Skip(first).SelectMany(e => e.Lines).ToArray();
        }
    }

    private static string Clean(string text, int limit)
    {
        var result = new StringBuilder();
        var space = false;
        var marks = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                space = result.Length > 0;
                continue;
            }
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format) continue;
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                if (result.Length == 0 || ++marks > 2) continue;
            }
            else marks = 0;
            if (space) result.Append(' ');
            space = false;
            result.Append(rune);
            // Bound work and allocations even for long strings of combining marks.
            if (result.Length >= limit * 4) break;
        }
        return result.ToString();
    }

    private static ChatLine[] Wrap(string name, string message, string? color)
    {
        // At most two rows per message, including its display name.
        var text = name + ": " + message;
        int cut = Fit(text, Columns);
        if (cut == text.Length) return [new(name, message, color)];
        int space = text.LastIndexOf(' ', cut);
        if (space > 0) cut = space;
        // A long first word can push the entire body onto the continuation.
        string first = text[Math.Min(name.Length + 2, cut)..cut];
        string rest = text[cut..].TrimStart();
        if (Fit(rest, Columns) < rest.Length)
            rest = rest[..Fit(rest, Columns - 1)].TrimEnd() + "…";
        return [new(name, first, color), new(null, rest, null)];
    }

    // Cell width is approximate; let CJK and emoji take two columns. Truncate
    // only at a Unicode scalar boundary, never halfway through a surrogate.
    private static int Fit(string text, int columns)
    {
        int offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int value = rune.Value;
            int width = Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark ? 0 :
                value is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3
                    or >= 0xf900 and <= 0xfaff or >= 0xff01 and <= 0xff60 or >= 0x1f000 and <= 0x1faff
                    or >= 0x20000 and <= 0x3ffff ? 2 : 1;
            if (width > columns) break;
            columns -= width;
            offset += rune.Utf16SequenceLength;
        }
        return offset;
    }
}
