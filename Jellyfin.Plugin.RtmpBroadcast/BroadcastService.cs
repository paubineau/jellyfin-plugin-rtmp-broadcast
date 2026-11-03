using System.Diagnostics;
using Jellyfin.Plugin.RtmpBroadcast.Chat;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RtmpBroadcast;

public sealed record PlaybackSession(string SessionId, string DeviceId, string DeviceName, Guid ItemId, string ItemName, long PositionTicks, bool IsPaused);
public sealed record BroadcastStatus(bool Active, string State, string? SessionId, string? ItemName, string? ChatStatus = null,
    string? AccelerationStatus = null);

public sealed class BroadcastService(
    ISessionManager sessions, IUserManager users, ILibraryManager library, IMediaSourceManager sources,
    IMediaEncoder encoder, ISubtitleEncoder subtitles, ITranscodeManager transcodes, IConfigurationManager configuration, EncodingHelper encoding,
    IHttpContextAccessor http, ILogger<BroadcastService> logger) : IHostedService
{
    // Web sends genuine progress approximately every ten seconds. Jellyfin also
    // synthesizes a tick every second, even after a client disappears.
    private static readonly TimeSpan MaxSilence = TimeSpan.FromSeconds(90);
    private readonly object _gate = new();
    private readonly Dictionary<SessionInfo, Report> _reported = new();
    private Follow? _follow;
    private Task _stopping = Task.CompletedTask;
    private Timer? _watchdog;
    private bool _shuttingDown;

    private sealed record PlayIdentity(Guid User, Guid Item, string Source, string? PlayId);
    private sealed record Report(PlayIdentity Identity, int? SubtitleIndex, PlayMethod? Method, long? PositionTicks, bool Paused)
    {
        public long Timestamp { get; } = Stopwatch.GetTimestamp();
    }

    private sealed class Follow(SessionInfo session, PlaybackSession web, MediaSourceInfo source, PlayIdentity identity, BroadcastPipeline pipeline)
    {
        public SessionInfo Session { get; } = session;
        public PlaybackSession Web { get; } = web;
        public MediaSourceInfo Source { get; set; } = source;
        public PlayIdentity Identity { get; set; } = identity;
        public HashSet<string> RetiredPlayIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public PlayMethod? Method { get; set; } = session.PlayState.PlayMethod;
        public int? SubtitleIndex { get; set; }
        public BroadcastSubtitle? Subtitle { get; set; }
        public CancellationTokenSource? SubtitleRequest { get; set; }
        public BroadcastPipeline Pipeline { get; } = pipeline;
        public TwitchChatClient? Chat { get; set; }
        public CancellationTokenSource? ChatCancellation { get; set; }
        public Task ChatTask { get; set; } = Task.CompletedTask;
        public TimeSpan Position { get; set; }
        public bool Paused { get; set; }
        public long Timestamp { get; set; }
        public TimeSpan ExpectedPosition => Paused ? Position : Position + TimeSpan.FromTicks(
            Math.Min(Stopwatch.GetElapsedTime(Timestamp).Ticks, long.MaxValue - Position.Ticks));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        sessions.PlaybackStart += OnStart;
        sessions.PlaybackProgress += OnProgress;
        sessions.PlaybackStopped += OnStopped;
        sessions.SessionEnded += OnSessionEnded;
        _watchdog = new Timer(CheckSession, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        sessions.PlaybackStart -= OnStart;
        sessions.PlaybackProgress -= OnProgress;
        sessions.PlaybackStopped -= OnStopped;
        sessions.SessionEnded -= OnSessionEnded;
        _watchdog?.Dispose();
        lock (_gate)
        {
            _shuttingDown = true;
            _reported.Clear();
            return EndLocked();
        }
    }

    public IReadOnlyList<PlaybackSession> GetSessions(Guid userId)
    {
        var user = Admin(userId);
        if (user is null) return [];
        var result = new List<PlaybackSession>();
        foreach (var session in sessions.Sessions)
        {
            if (Select(session, user) is { } selected) result.Add(selected.Web);
        }
        return result;
    }

    public BroadcastStatus GetStatus(Guid userId)
    {
        lock (_gate)
        {
            if (_follow is not { } f) return new(false, _stopping.IsCompleted ? "stopped" : "stopping", null, null);
            if (f.Identity.User != userId) return new(true, "busy", null, null);
            var state = f.Pipeline.Status;
            if (f.SubtitleRequest is not null) state += " (preparing subtitles)";
            else if (f.SubtitleIndex.HasValue && f.Subtitle is null) state += " (selected subtitle unavailable)";
            var chatStatus = f.Pipeline.ChatOverlayUnavailable ? "Overlay unavailable in the encoder" : f.Chat?.Status;
            var acceleration = $"Encode: {f.Pipeline.EncoderStatus} · Decode: {f.Pipeline.DecoderStatus}";
            return new(true, state, f.Web.SessionId, f.Web.ItemName, chatStatus, acceleration);
        }
    }

    public BroadcastStatus StartBroadcast(Guid userId, string sessionId, Guid? itemId = null)
    {
        var user = Admin(userId) ?? throw new InvalidOperationException("Sign in with an administrator account.");
        lock (_gate)
        {
            if (_shuttingDown || !_stopping.IsCompleted) throw new InvalidOperationException("The previous broadcast is stopping.");
            var session = sessions.Sessions.FirstOrDefault(s => s.Id == sessionId);
            var selected = session is null ? null : Select(session, user);
            if (session is null || selected is null || (itemId.HasValue && itemId != selected.Value.Web.ItemId))
                throw new InvalidOperationException("Select a local video playing in your web player.");
            var (web, source) = selected.Value;
            if (_follow is { } existing)
            {
                if (existing.Session == session && existing.Identity.User == userId && existing.Web.ItemId == web.ItemId
                    && Same(existing.Identity.Source, source.Id))
                    return GetStatus(userId);
                throw new InvalidOperationException("A broadcast is already active.");
            }
            var plugin = Plugin.Instance!;
            var destination = plugin.Destination;
            if (string.IsNullOrWhiteSpace(destination)) throw new InvalidOperationException("Save a publish URL in the RTMP Broadcast settings first.");
            var settings = plugin.Settings;
            _reported.TryGetValue(session, out var last);
            if (last?.Identity.User != userId || last.Identity.Item != web.ItemId || !Same(last.Identity.Source, source.Id))
                last = null;
            try
            {
                var acceleration = AccelerationPolicy.Select(configuration.GetEncodingOptions(), source, encoder, encoding);
                var pipeline = new BroadcastPipeline(encoder.EncoderPath, destination, settings.OutputHeight, settings.VideoBitrateKbps,
                    settings.ChatEnabled, acceleration);
                _follow = new(session, web, source, new(userId, web.ItemId, source.Id, last?.Identity.PlayId), pipeline);
                if (settings.ChatEnabled)
                {
                    var chat = new TwitchChatClient(settings.ChatChannel, pipeline.SetChatLines);
                    var cancellation = new CancellationTokenSource();
                    _follow.Chat = chat;
                    _follow.ChatCancellation = cancellation;
                    _follow.ChatTask = Task.Run(() => chat.RunAsync(cancellation.Token));
                }
                _follow.Method = last?.Method ?? session.PlayState.PlayMethod;
                SelectSubtitleLocked(_follow, last is null ? NormalizeIndex(session.PlayState.SubtitleStreamIndex) : last.SubtitleIndex);
                var ticks = Math.Max(0, last?.PositionTicks ?? web.PositionTicks);
                if (last?.PositionTicks is not null && !last.Paused)
                    ticks += Math.Min(Stopwatch.GetElapsedTime(last.Timestamp).Ticks, long.MaxValue - ticks);
                ApplyLocked(_follow, TimeSpan.FromTicks(ticks), last?.Paused ?? web.IsPaused);
                return GetStatus(userId);
            }
            catch
            {
                EndLocked();
                throw new InvalidOperationException("Could not start the broadcast encoder.");
            }
        }
    }

    public Task StopBroadcastAsync(Guid userId)
    {
        lock (_gate)
        {
            if (_follow is { } f && f.Identity.User != userId) throw new InvalidOperationException("This broadcast belongs to another session owner.");
            return EndLocked();
        }
    }

    private User? Admin(Guid id) => id != Guid.Empty && users.GetUserById(id) is { } user
        && user.HasPermission(PermissionKind.IsAdministrator) ? user : null;

    private static bool Same(string? first, string? second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    // The Android app supplies its own client name even when it uses Jellyfin Web's player.
    // Android 2.7 renamed the client from "Jellyfin Android" to "Jellyfin for Android".
    internal static bool SupportsPlayerClient(string? client) =>
        Same(client, "Jellyfin Web") || Same(client, "Jellyfin for Android") || Same(client, "Jellyfin Android");

    private static bool Current(SessionInfo s, Guid user) =>
        s.UserId == user && SupportsPlayerClient(s.Client) && s.NowPlayingItem is not null && s.IsActive
        && DateTime.UtcNow - s.LastPlaybackCheckIn < MaxSilence;

    private (PlaybackSession Web, MediaSourceInfo Source)? Select(SessionInfo s, User owner)
    {
        if (!Current(s, owner.Id) || s.NowPlayingItem is not { } playing || s.PlayState is not { } state
            || !string.IsNullOrEmpty(state.LiveStreamId)) return null;
        // Item DTOs in playback reports come from the client. Only Jellyfin's
        // library and the user's authorised static media sources supply paths.
        var item = library.GetItemById<Video>(playing.Id, owner);
        if (item is null || item.IsVirtualItem || item.IsPlaceHolder || item.IsShortcut) return null;
        var source = sources.GetStaticMediaSources(item, false, owner).FirstOrDefault(x => Same(x.Id, state.MediaSourceId));
        if (source is null || source.IsRemote || source.Protocol != MediaProtocol.File
            || string.IsNullOrEmpty(source.Path) || !Path.IsPathFullyQualified(source.Path) || !File.Exists(source.Path)) return null;
        var web = new PlaybackSession(s.Id, s.DeviceId, s.DeviceName, item.Id, item.Name, Math.Max(0, state.PositionTicks ?? 0), state.IsPaused);
        return (web, source);
    }

    private void OnStart(object? sender, PlaybackProgressEventArgs e) => Observe(e, true);
    private void OnProgress(object? sender, PlaybackProgressEventArgs e) => Observe(e, false);

    private void Observe(PlaybackProgressEventArgs e, bool started)
    {
        if (e.IsAutomated || e.Item is null || e.Session is not { } s || !SupportsPlayerClient(s.Client)) return;
        lock (_gate)
        {
            if (_shuttingDown) return;
            // Jellyfin's automatic progress timer also writes s.PlayState. A
            // concurrent timer tick may overwrite it during the real client's
            // update. Prefer the report Jellyfin accepted on this request.
            var client = ClientPlaybackReport.Get(http.HttpContext, s.Id);
            var state = s.PlayState;
            if (client is null && (s.NowPlayingItem?.Id != e.Item.Id || !Same(state?.MediaSourceId, e.MediaSourceId))) return;
            if (client is not null && (client.ItemId != e.Item.Id || !Same(client.PlaySessionId, e.PlaySessionId))) return;
            var identity = new PlayIdentity(s.UserId, e.Item.Id, client?.MediaSourceId ?? e.MediaSourceId, e.PlaySessionId);
            var change = client is null
                ? new Report(identity, NormalizeIndex(state!.SubtitleStreamIndex), state.PlayMethod,
                    e.PlaybackPositionTicks, started ? state.IsPaused : e.IsPaused)
                : new Report(identity, NormalizeIndex(client.SubtitleStreamIndex), client.PlayMethod, client.PositionTicks, client.IsPaused);
            // PlaybackStart may be dispatched after its request has finished
            // and after a newer progress report for the same play.
            if (started && client is null && _reported.TryGetValue(s, out var previous) && previous.Identity == identity) return;
            if (_follow is { } f && s == f.Session)
            {
                var old = f.Identity;
                if (identity.PlayId is { Length: > 0 } reportedId && f.RetiredPlayIds.Contains(reportedId)) return;
                bool otherPlay = old.PlayId is { Length: > 0 } && !Same(old.PlayId, identity.PlayId);
                // A delayed report from a superseded play must neither seek nor
                // terminate the selected one. Web can replace its transcoding
                // URL for a subtitle change without starting a new movie.
                if (otherPlay && !started)
                {
                    if (!SubtitleTransportChanged(f, change)) return;
                    f.RetiredPlayIds.Add(old.PlayId!);
                    f.Identity = identity;
                    otherPlay = false;
                }
                if (otherPlay || old.User != identity.User || old.Item != identity.Item || !Same(old.Source, identity.Source))
                    EndLocked();
                else
                {
                    if (string.IsNullOrEmpty(old.PlayId)) f.Identity = identity;
                    f.Method = change.Method;
                    var removedTrack = SelectSubtitleLocked(f, change.SubtitleIndex);
                    var expected = f.ExpectedPosition;
                    var position = change.PositionTicks is long ticks ? TimeSpan.FromTicks(Math.Max(0, ticks)) : expected;
                    var paused = change.Paused;
                    // The paused clock is stationary. Even a single frame step can change the still.
                    bool moved = paused ? position != expected : Math.Abs((position - expected).TotalSeconds) > 2;
                    if (removedTrack || paused != f.Paused || moved)
                        ApplyLocked(f, position, paused);
                }
            }
            _reported[s] = change;
        }
    }

    private static int? NormalizeIndex(int? index) => index >= 0 ? index : null;

    private bool SubtitleTransportChanged(Follow f, Report change)
    {
        var next = change.Identity;
        if (next.Item != f.Identity.Item || next.User != f.Identity.User || !Same(next.Source, f.Identity.Source)
            || change.SubtitleIndex == f.SubtitleIndex) return false;
        if (change.Method == PlayMethod.Transcode)
        {
            if (!Guid.TryParse(next.PlayId, out _)) return false;
            var id = next.PlayId!;
            var job = transcodes.GetTranscodingJob(id);
            return job is not null && Same(job.DeviceId, f.Session.DeviceId) && Same(job.MediaSource?.Id, f.Identity.Source);
        }
        // When reverting to direct streaming there is no new transcode job.
        // changeStream resumes the replacement URL at the same movie position.
        return f.Method == PlayMethod.Transcode && change.Method is PlayMethod.DirectPlay or PlayMethod.DirectStream
            && string.IsNullOrEmpty(next.PlayId) && change.PositionTicks is long ticks
            && Math.Abs(TimeSpan.FromTicks(ticks).TotalSeconds - f.ExpectedPosition.TotalSeconds) < 10;
    }

    private bool SelectSubtitleLocked(Follow f, int? index)
    {
        if (index == f.SubtitleIndex) return false;
        f.SubtitleRequest?.Cancel();
        f.SubtitleRequest = null;
        var removed = f.Subtitle is not null;
        f.Subtitle = null;
        f.SubtitleIndex = index;
        if (index.HasValue)
        {
            var request = new CancellationTokenSource();
            f.SubtitleRequest = request;
            _ = Task.Run(() => PrepareSubtitleAsync(f, index.Value, request));
        }
        return removed;
    }

    private async Task PrepareSubtitleAsync(Follow f, int index, CancellationTokenSource request)
    {
        var source = f.Source;
        BroadcastSubtitle? result = null;
        try
        {
            request.Token.ThrowIfCancellationRequested();
            // A sidecar downloaded during playback may be absent from the
            // source snapshot taken at Start. Refresh only on a new selection.
            if (!source.MediaStreams.Any(track => track.Index == index) && Admin(f.Identity.User) is { } owner
                && Select(f.Session, owner) is { } updated && updated.Web.ItemId == f.Web.ItemId
                && Same(updated.Source.Id, source.Id) && updated.Source.Path == source.Path)
                source = updated.Source;
            result = await SubtitleResolver.ResolveAsync(subtitles, source, index, request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception) { logger.LogWarning("Could not prepare the selected subtitle."); }
        finally
        {
            lock (_gate)
            {
                if (_follow == f && f.SubtitleRequest == request && !request.IsCancellationRequested)
                {
                    f.SubtitleRequest = null;
                    f.Source = source;
                    f.Subtitle = result;
                    if (result is not null) ApplyLocked(f, f.ExpectedPosition, f.Paused);
                }
                request.Dispose();
            }
        }
    }

    private static void ApplyLocked(Follow f, TimeSpan position, bool paused)
    {
        f.Pipeline.SetPlayback(f.Source.Path, position, paused, f.Subtitle);
        f.Position = position;
        f.Paused = paused;
        f.Timestamp = Stopwatch.GetTimestamp();
    }

    private void OnStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session is not { } s) return;
        bool Matches(PlayIdentity x) => x.User == s.UserId && x.Item == e.Item?.Id
            && (string.IsNullOrEmpty(e.MediaSourceId) || Same(x.Source, e.MediaSourceId))
            && (string.IsNullOrEmpty(x.PlayId) || Same(x.PlayId, e.PlaySessionId));
        lock (_gate)
        {
            if (_follow is { } current && s == current.Session && e.PlaySessionId is { Length: > 0 } id
                && current.RetiredPlayIds.Contains(id)) return;
            if (_reported.TryGetValue(s, out var last) && Matches(last.Identity)) _reported.Remove(s);
            if (_follow is { } f && s == f.Session && Matches(f.Identity)) EndLocked();
        }
    }

    private void OnSessionEnded(object? sender, SessionEventArgs e)
    {
        lock (_gate)
        {
            _reported.Remove(e.SessionInfo);
            if (_follow?.Session == e.SessionInfo) EndLocked();
        }
    }

    private void CheckSession(object? timerState)
    {
        lock (_gate)
        {
            if (_follow is not { } f) return;
            var s = sessions.Sessions.FirstOrDefault(x => x.Id == f.Web.SessionId);
            if (s is null || s != f.Session || !Current(s, f.Identity.User) || s.NowPlayingItem?.Id != f.Identity.Item
                || !Same(s.PlayState?.MediaSourceId, f.Identity.Source)) EndLocked();
        }
    }

    private Task EndLocked()
    {
        if (_follow is { } ended)
        {
            _follow = null;
            ended.SubtitleRequest?.Cancel();
            ended.ChatCancellation?.Cancel();
            _stopping = DisposeBroadcast(ended);
        }
        return _stopping;
    }

    private async Task DisposeBroadcast(Follow ended)
    {
        // Stop publishing immediately, even while a chat socket is closing.
        var encoderShutdown = ended.Pipeline.DisposeAsync();
        try { await ended.ChatTask.ConfigureAwait(false); }
        catch (OperationCanceledException) when (ended.ChatCancellation?.IsCancellationRequested == true) { }
        catch (Exception) { logger.LogWarning("The Twitch chat reader stopped unexpectedly."); }
        finally { ended.ChatCancellation?.Dispose(); }

        try { await encoderShutdown.ConfigureAwait(false); }
        catch (Exception) { logger.LogWarning("Could not cleanly stop the broadcast encoder."); }
    }
}
