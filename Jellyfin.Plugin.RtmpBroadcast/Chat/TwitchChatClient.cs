using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.RtmpBroadcast.Chat;

internal sealed class TwitchChatClient(string channel, Action<IReadOnlyList<ChatLine>> setChatLines)
{
    private static readonly Uri Endpoint = new("wss://irc-ws.chat.twitch.tv/");
    private readonly string room = "#" + channel.Trim().TrimStart('#').ToLowerInvariant();
    private readonly string nick = "justinfan" + RandomNumberGenerator.GetInt32(10_000_000, 100_000_000);
    private readonly ChatHistory history = new();
    private volatile string status = "Connecting";
    public string Status => status;

    public async Task RunAsync(CancellationToken token)
    {
        if (room.Length is < 2 or > 26 || !room[1..].All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            status = "Invalid channel";
            return;
        }
        using var renderStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var render = RenderAsync(renderStop.Token);
        int delay = 2;
        try
        {
            while (!token.IsCancellationRequested)
            {
                long started = TimeProvider.System.GetTimestamp();
                try
                {
                    using var socket = new ClientWebSocket();
                    socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(15);
                    using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
                    connect.CancelAfter(TimeSpan.FromSeconds(15));
                    await socket.ConnectAsync(Endpoint, connect.Token).ConfigureAwait(false);
                    await ReadSessionAsync(socket, token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is WebSocketException or HttpRequestException or IOException or OperationCanceledException)
                {
                    // A failed guest handshake or disconnected socket must not
                    // interrupt the media publisher. Never log chat messages.
                }
                history.Clear(); // Moderation events could be missed while offline.
                status = "Reconnecting";
                if (TimeProvider.System.GetElapsedTime(started) > TimeSpan.FromMinutes(1)) delay = 2;
                await Task.Delay(TimeSpan.FromSeconds(delay), token).ConfigureAwait(false);
                delay = Math.Min(delay * 2, 60);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            await renderStop.CancelAsync().ConfigureAwait(false);
            await render.ConfigureAwait(false);
            history.Clear();
            setChatLines([]);
            status = "Stopped";
        }
    }

    private async Task RenderAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                setChatLines(history.Snapshot()); // The writer compares against its last successful write.
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static ValueTask SendAsync(WebSocket socket, string command, CancellationToken token) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(command).AsMemory(), WebSocketMessageType.Text, true, token);

    private async Task ReadSessionAsync(WebSocket socket, CancellationToken token)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        session.CancelAfter(TimeSpan.FromSeconds(20));
        // Twitch currently accepts this read-only guest login without a token.
        await SendAsync(socket, "CAP REQ :twitch.tv/commands twitch.tv/tags", session.Token).ConfigureAwait(false);
        await SendAsync(socket, "NICK " + nick, session.Token).ConfigureAwait(false);
        await SendAsync(socket, "JOIN " + room, session.Token).ConfigureAwait(false);

        // WebSocket chunks can contain several CRLF-delimited IRC messages,
        // or fragments of one. Buffer at most one unfinished IRC line.
        byte[] buffer = new byte[16 * 1024];
        int used = 0;
        bool joined = false;
        while (!session.IsCancellationRequested)
        {
            var read = await socket.ReceiveAsync(buffer.AsMemory(used), session.Token).ConfigureAwait(false);
            if (read.MessageType != WebSocketMessageType.Text) return;
            int end = used + read.Count;
            int start = 0;
            for (int i = used; i < end; i++)
            {
                if (buffer[i] != '\n') continue;
                int length = i - start;
                if (length > 0 && buffer[i - 1] == '\r') length--;
                string line = Encoding.UTF8.GetString(buffer, start, length);
                start = i + 1;
                string tags = line.StartsWith('@') ? Take(ref line)[1..] : string.Empty;
                string prefix = line.StartsWith(':') ? Take(ref line)[1..] : string.Empty;
                string command = Take(ref line);
                if (command == "PING")
                {
                    if (line.Length <= 256 && !line.Contains('\r') && !line.Contains('\0'))
                        await SendAsync(socket, "PONG " + line, session.Token).ConfigureAwait(false);
                    continue;
                }
                if (command == "RECONNECT" || (!joined && command == "NOTICE")) return;
                if (!string.Equals(Take(ref line), room, StringComparison.OrdinalIgnoreCase)) continue;
                string body = line.StartsWith(':') ? line[1..] : line;
                if (command == "ROOMSTATE")
                {
                    joined = true;
                    status = "Connected";
                    session.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                else if (joined)
                {
                    switch (command)
                    {
                        case "PRIVMSG":
                            var bang = prefix.IndexOf('!');
                            if (bang > 0)
                                history.Add(Tag(tags, "id"), prefix[..bang], Tag(tags, "user-id"),
                                    DisplayName(tags), Tag(tags, "color"), body);
                            break;
                        case "CLEARMSG":
                            history.RemoveMessage(Tag(tags, "target-msg-id"));
                            break;
                        case "CLEARCHAT":
                            string userId = Tag(tags, "target-user-id");
                            if (body.Length == 0 && userId.Length == 0) history.Clear();
                            else history.RemoveUser(body, userId);
                            break;
                    }
                }
            }
            used = end - start;
            if (used == buffer.Length) throw new IOException("IRC line too long.");
            if (start > 0 && used > 0) Buffer.BlockCopy(buffer, start, buffer, 0, used);
        }
    }

    private static string Take(ref string input)
    {
        int space = input.IndexOf(' ');
        string word = space < 0 ? input : input[..space];
        input = space < 0 ? string.Empty : input[(space + 1)..].TrimStart(' ');
        return word;
    }

    private static string Tag(string tags, string key)
    {
        var span = tags.AsSpan();
        foreach (var range in span.Split(';'))
        {
            var field = span[range];
            int eq = field.IndexOf('=');
            if (eq >= 0 && field[..eq].SequenceEqual(key)) return field[(eq + 1)..].ToString();
        }
        return string.Empty;
    }

    private static string DisplayName(string tags)
    {
        string value = Tag(tags, "display-name");
        if (!value.Contains('\\')) return value;
        var result = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (ch == '\\')
            {
                if (++i >= value.Length) break;
                ch = value[i] switch { ':' => ';', 's' or 'r' or 'n' => ' ', var other => other };
            }
            result.Append(ch);
        }
        return result.ToString();
    }
}
