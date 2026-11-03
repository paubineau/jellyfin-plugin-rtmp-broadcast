using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.RtmpBroadcast.Chat;

namespace Jellyfin.Plugin.RtmpBroadcast;

/// <summary>One continuously clocked RTMP publisher for a Jellyfin playback session.</summary>
internal sealed class BroadcastPipeline : IAsyncDisposable
{
    private sealed record Playback(string File, TimeSpan Position, bool Paused, long ChangedAt, BroadcastSubtitle? Subtitle);
    private sealed record Standby(byte[] Header, byte[] Frame);
    private sealed record PausedStill(Playback Request, byte[] Frame, bool WithoutSubtitles);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    private readonly string ffmpeg;
    private readonly string destination;
    private readonly int width;
    private readonly int height;
    private readonly int videoBitrateKbps;
    private readonly ChatOverlay? chat;
    private readonly HardwareAcceleration? acceleration;
    private bool useHardwareEncoder;
    private bool decodeFallback;
    private string encoderStatus;
    private string decoderStatus = "Idle";
    private Playback? playback;
    private PausedStill? pausedStill;
    private string status = "Connecting";
    private int disposed;

    public BroadcastPipeline(string ffmpegPath, string destination, int height, int videoBitrateKbps, bool showChat = false,
        HardwareAcceleration? acceleration = null)
    {
        width = height switch
        {
            480 => 854,
            720 => 1280,
            1080 => 1920,
            _ => throw new ArgumentOutOfRangeException(nameof(height)),
        };
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(videoBitrateKbps);
        this.height = height;
        this.videoBitrateKbps = videoBitrateKbps;
        ffmpeg = ffmpegPath;
        this.destination = destination;
        this.acceleration = acceleration;
        useHardwareEncoder = acceleration?.Encode == true;
        encoderStatus = useHardwareEncoder ? "Checking " + acceleration!.Name : "Software (libx264)";
        chat = showChat ? new ChatOverlay(width, height) : null;
        worker = Task.Run(() => RunAsync(shutdown.Token));
    }

    public string Status => Volatile.Read(ref status);
    public string EncoderStatus => Volatile.Read(ref encoderStatus);
    public string DecoderStatus => Volatile.Read(ref decoderStatus);
    public bool ChatOverlayUnavailable => chat?.Unavailable == true;

    public void SetChatLines(IReadOnlyList<ChatLine> lines) => chat?.SetLines(lines);

    public void SetPlayback(string filePath, TimeSpan position, bool paused, BroadcastSubtitle? subtitle = null)
    {
        Volatile.Write(ref playback, new Playback(filePath, position, paused, Stopwatch.GetTimestamp(), subtitle));
    }

    public async ValueTask DisposeAsync()
    {
        bool first = Interlocked.Exchange(ref disposed, 1) == 0;
        if (first)
        {
            shutdown.Cancel();
        }

        await worker.ConfigureAwait(false);
        if (first)
        {
            shutdown.Dispose();
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        Standby? standby = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    standby ??= await MakeStandbyAsync(token).ConfigureAwait(false);
                    if (useHardwareEncoder)
                    {
                        // Probe locally, also after an RTMP failure. An offline
                        // destination alone never forces the encoder back to CPU.
                        useHardwareEncoder = await ProbeHardwareEncoderAsync(standby, token).ConfigureAwait(false);
                        Volatile.Write(ref encoderStatus, useHardwareEncoder
                            ? $"{acceleration!.Name} ({acceleration.Encoder})" : EncoderFallbackStatus);
                    }

                    await PublishAsync(standby, token).ConfigureAwait(false);
                }
                catch (Exception e) when (IsMediaError(e))
                {
                    // Command lines and FFmpeg diagnostics may contain the stream key.
                    Volatile.Write(ref status, "Reconnecting");
                    await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            pausedStill = null;
            chat?.Dispose();
            Volatile.Write(ref status, "Stopped");
        }
    }

    private static bool IsMediaError(Exception e) =>
        e is IOException or InvalidDataException or TimeoutException or Win32Exception or InvalidOperationException;

    private Process Launch(IEnumerable<string> args, bool needsInput = false, Action<string>? onError = null)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = needsInput,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error" })
        {
            info.ArgumentList.Add(arg);
        }

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        var process = Process.Start(info) ?? throw new IOException("Could not start the media process.");
        if (onError is not null)
        {
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onError(e.Data); };
        }

        process.BeginErrorReadLine(); // Drain without forwarding potentially sensitive diagnostics.
        return process;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // Exited between HasExited and Kill.
        }
    }

    private async Task<Standby> MakeStandbyAsync(CancellationToken token)
    {
        using var process = Launch([
            "-f", "lavfi", "-i", $"smptehdbars=s={width}x{height}:r=30",
            "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo",
            "-map", "0:v:0", "-map", "1:a:0", "-frames:v", "1", "-frames:a", "1",
            "-c:v", "rawvideo", "-threads:v", "2", "-pix_fmt", "yuv420p",
            "-af", "asetnsamples=n=1600", "-c:a", "pcm_s16le", "-f", "avi", "pipe:1"]);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Timeout);
        using var kill = deadline.Token.Register(() => Kill(process));
        try
        {
            var reader = new AviFrames(process.StandardOutput.BaseStream, width, height);
            var header = await reader.ReadHeaderAsync(deadline.Token).ConfigureAwait(false);
            var frame = reader.CreateFrame();
            if (!await reader.ReadFrameAsync(frame, deadline.Token).ConfigureAwait(false))
            {
                throw new InvalidDataException("Could not generate standby.");
            }

            return new Standby(header, frame);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Could not generate standby.");
        }
        finally
        {
            Kill(process);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private List<string> PublisherArgs(bool hardware)
    {
        var args = hardware ? acceleration!.DeviceArgs() : [];
        args.AddRange(["-f", "avi", "-probesize", "32",
            "-analyzeduration", "1", "-fpsprobesize", "0", "-i", "pipe:0",
            "-map", "0:v:0", "-map", "0:a:0"]);
        if (hardware)
        {
            args.AddRange(acceleration!.EncoderArgs());
        }
        else
        {
            args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency"]);
        }

        args.AddRange(["-g", "60", "-bf", "0", "-b:v", $"{videoBitrateKbps}k", "-maxrate", $"{videoBitrateKbps}k",
            "-bufsize", $"{2L * videoBitrateKbps}k", "-c:a", "aac", "-b:a", "128k"]);
        return args;
    }

    private async Task<bool> ProbeHardwareEncoderAsync(Standby standby, CancellationToken token)
    {
        if (!HardwareAcceleration.IsSupportedPlatform(acceleration!.Backend)) return false;
        var args = PublisherArgs(hardware: true);
        args.AddRange(["-abort_on", "empty_output_stream", "-f", "null", "-"]);
        using var probe = Launch(args, needsInput: true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Timeout);
        using var kill = deadline.Token.Register(() => Kill(probe));
        try
        {
            var input = probe.StandardInput.BaseStream;
            await input.WriteAsync(standby.Header, deadline.Token).ConfigureAwait(false);
            for (int i = 0; i < 3; i++)
                await input.WriteAsync(standby.Frame, deadline.Token).ConfigureAwait(false);
            input.Close();
            await probe.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            return probe.ExitCode == 0;
        }
        catch (Exception e) when (!token.IsCancellationRequested && (IsMediaError(e) || e is OperationCanceledException))
        {
            return false;
        }
        finally
        {
            Kill(probe);
            await probe.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private async Task PublishAsync(Standby standby, CancellationToken token)
    {
        var args = PublisherArgs(useHardwareEncoder);
        args.AddRange(["-progress", "pipe:1", "-stats_period", "0.5", "-rw_timeout", "10000000", "-flvflags", "no_duration_filesize"]);
        // An RTMP address with only a channel name uses Restreamer's root app.
        if (new Uri(destination).AbsolutePath.Trim('/').IndexOf('/') < 0)
        {
            args.AddRange(["-rtmp_app", ""]);
        }

        args.AddRange(["-f", "flv", destination]);
        bool codecFailed = false;
        using var publisher = Launch(args, needsInput: true, onError: useHardwareEncoder ? line =>
        {
            // Codec/device errors, as distinct from network or muxer errors.
            if (IsEncoderError(line, acceleration!.Encoder))
                Volatile.Write(ref codecFailed, true);
        } : null);
        using var kill = token.Register(() => Kill(publisher));
        var pipe = publisher.StandardInput.BaseStream;
        long started = Stopwatch.GetTimestamp();
        long lastProgress = 0;
        var monitor = MonitorAsync(publisher.StandardOutput, () => Interlocked.Exchange(ref lastProgress, Stopwatch.GetTimestamp()));
        Playback? selected = null;
        Decoder? decoder = null;
        byte[]? lastMovieFrame = null;
        double decoderStartSeconds = 0, lastFrameSeconds = 0;
        long retrySourceAt = 0;
        bool atEnd = false;
        bool subtitleFailed = false;
        bool tryingSoftware = false;
        string activeDecoder = "Software";
        try
        {
            await pipe.WriteAsync(standby.Header, token).ConfigureAwait(false);
            long nextTick = Stopwatch.GetTimestamp();
            while (!token.IsCancellationRequested)
            {
                long progress = Interlocked.Read(ref lastProgress);
                if (monitor.IsCompleted || Stopwatch.GetElapsedTime(progress == 0 ? started : progress) > Timeout)
                {
                    throw new IOException("RTMP output stopped advancing.");
                }

                var requested = Volatile.Read(ref playback);
                if (!ReferenceEquals(selected, requested))
                {
                    if (selected?.File != requested?.File)
                    {
                        lastMovieFrame = null;
                    }

                    // A normal pause holds precisely the clean picture just sent.
                    // A seek, track change or materially different client position
                    // needs a still decoded at the new position.
                    if (requested is { Paused: true } && selected is { Paused: false }
                        && lastMovieFrame is not null && selected.Subtitle == requested.Subtitle
                        && requested.Position.TotalSeconds >= lastFrameSeconds
                        && requested.Position.TotalSeconds < lastFrameSeconds + 1d / AviFrames.Fps)
                    {
                        pausedStill = new(requested, lastMovieFrame, subtitleFailed);
                    }
                    else if (!ReferenceEquals(pausedStill?.Request, requested))
                    {
                        pausedStill = null;
                    }

                    if (decoder is not null)
                    {
                        await decoder.DisposeAsync().ConfigureAwait(false);
                        decoder = null;
                    }

                    selected = requested;
                    Volatile.Write(ref decoderStatus, "Idle");
                    retrySourceAt = 0;
                    atEnd = false;
                    subtitleFailed = pausedStill?.WithoutSubtitles ?? false;
                    tryingSoftware = false;
                }

                byte[] frame = standby.Frame;
                bool freshMedia = false;
                string state = "Standby";
                if (pausedStill is not null)
                {
                    lastMovieFrame = frame = pausedStill.Frame;
                    state = subtitleFailed ? "Paused without subtitles" : "Paused";
                }
                else if (selected is not null && !atEnd && Stopwatch.GetTimestamp() >= retrySourceAt)
                {
                    if (selected.Paused) state = "Loading paused frame";
                    try
                    {
                        if (decoder is null)
                        {
                            // On reconnect follow the client timeline, including time spent offline.
                            var position = selected.Paused ? selected.Position
                                : selected.Position + Stopwatch.GetElapsedTime(selected.ChangedAt);
                            decoderStartSeconds = position.TotalSeconds;
                            bool hardware = acceleration?.Decoder is not null && !decodeFallback && !tryingSoftware;
                            activeDecoder = hardware ? acceleration!.DecodeLabel
                                : tryingSoftware || decodeFallback ? $"Software ({acceleration!.DecoderFamily} fallback)" : "Software";
                            Volatile.Write(ref decoderStatus, hardware ? "Starting " + activeDecoder : "Starting software");
                            decoder = new Decoder(OpenMovie(selected.File, position, subtitleFailed ? null : selected.Subtitle, hardware, selected.Paused),
                                width, height, token, selected.Paused);
                        }

                        if (decoder.TryTake(out var next))
                        {
                            lastMovieFrame = frame = next;
                            lastFrameSeconds = decoderStartSeconds + decoder.DisplayedOffsetSeconds;
                            freshMedia = true;
                            if (tryingSoftware)
                            {
                                // Only attribute the failure to hardware after
                                // software decoded the same source and subtitles.
                                decodeFallback = true;
                                tryingSoftware = false;
                            }

                            Volatile.Write(ref decoderStatus, activeDecoder);
                            state = subtitleFailed ? "Playing without subtitles" : "Playing";
                            if (selected.Paused)
                            {
                                pausedStill = new(selected, frame, subtitleFailed);
                                state = subtitleFailed ? "Paused without subtitles" : "Paused";
                                await decoder.DisposeAsync().ConfigureAwait(false);
                                decoder = null;
                                Volatile.Write(ref decoderStatus, "Idle");
                            }
                        }
                        else if (decoder.Finished)
                        {
                            lastMovieFrame = null;
                            state = "Standby";
                            if (await decoder.ExitCodeAsync().ConfigureAwait(false) != 0)
                            {
                                throw new IOException("Decoder exited.");
                            }

                            atEnd = true;
                            Volatile.Write(ref decoderStatus, "Idle");
                            await decoder.DisposeAsync().ConfigureAwait(false);
                            decoder = null;
                        }
                        else if (Stopwatch.GetElapsedTime(decoder.ReadStarted) > Timeout)
                        {
                            throw new TimeoutException("Decoder stalled.");
                        }
                        else if (lastMovieFrame is not null)
                        {
                            frame = lastMovieFrame;
                            state = selected.Paused ? "Loading paused frame" : "Buffering";
                        }
                    }
                    catch (Exception e) when (IsMediaError(e))
                    {
                        lastMovieFrame = null;
                        if (decoder is not null)
                        {
                            await decoder.DisposeAsync().ConfigureAwait(false);
                            decoder = null;
                        }

                        if (acceleration?.Decoder is not null && !decodeFallback && !tryingSoftware)
                        {
                            tryingSoftware = true;
                            Volatile.Write(ref decoderStatus, "Retrying in software");
                            state = "Retrying source with software decoding";
                        }
                        else if (selected.Subtitle is not null && !subtitleFailed)
                        {
                            // An unreadable cue file must not take the movie off air.
                            tryingSoftware = false;
                            subtitleFailed = true;
                            state = "Subtitles unavailable";
                        }
                        else
                        {
                            tryingSoftware = false;
                            Volatile.Write(ref decoderStatus, "Idle");
                            retrySourceAt = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
                            state = "Source unavailable";
                        }
                    }
                }
                else if (retrySourceAt > Stopwatch.GetTimestamp())
                {
                    state = "Source unavailable";
                }

                byte[] outgoing = chat?.Apply(frame, freshMedia) ?? frame;
                if ((selected?.Paused == true || !freshMedia) && ReferenceEquals(frame, lastMovieFrame))
                {
                    AviFrames.Silence(outgoing);
                }

                await pipe.WriteAsync(outgoing, token).AsTask().WaitAsync(Timeout, token).ConfigureAwait(false);
                Volatile.Write(ref status, progress == 0 ? "Connecting" : state);

                nextTick = Math.Max(nextTick + Stopwatch.Frequency / AviFrames.Fps, Stopwatch.GetTimestamp());
                var delay = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), nextTick);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            Kill(publisher);
            if (decoder is not null)
            {
                await decoder.DisposeAsync().ConfigureAwait(false);
            }

            await publisher.WaitForExitAsync().ConfigureAwait(false);
            await monitor.ConfigureAwait(false);
            Volatile.Write(ref decoderStatus, "Idle");
            if (Volatile.Read(ref codecFailed))
            {
                useHardwareEncoder = false;
                Volatile.Write(ref encoderStatus, EncoderFallbackStatus);
            }
        }
    }

    private string EncoderFallbackStatus => $"Software (libx264, {acceleration!.Name} fallback)";

    private static bool IsEncoderError(string line, string codec) =>
        line.StartsWith($"[{codec} @", StringComparison.Ordinal)
        || line.StartsWith("[AVHWDeviceContext @", StringComparison.Ordinal)
        || (line.StartsWith("[vost#", StringComparison.Ordinal) && line.Contains($"/{codec} @", StringComparison.Ordinal)
            && (line.Contains("Error while opening encoder", StringComparison.Ordinal)
                || line.Contains("Error submitting video frame to the encoder", StringComparison.Ordinal)
                || line.Contains("Error encoding a frame", StringComparison.Ordinal)));

    private static async Task MonitorAsync(StreamReader progress, Action advanced)
    {
        long previous = 0;
        while (await progress.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out long value) && value > previous)
            {
                previous = value;
                advanced();
            }
        }
    }

    private Process OpenMovie(string file, TimeSpan position, BroadcastSubtitle? subtitle, bool hardware, bool still)
    {
        string fps = still ? "fps=30:start_time=0:round=up" : "fps=30:start_time=0";
        string fit = $"{fps},scale=w='max(2,2*trunc(min({width},{height}*dar)/2))':h='max(2,2*trunc(min({height},{width}/dar)/2))',setsar=1";
        string pad = $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2";
        string seek = Math.Max(0, position.TotalSeconds).ToString(still ? "F6" : "F3", CultureInfo.InvariantCulture);
        var args = hardware ? acceleration!.DecodeArgs() : [];
        args.AddRange(["-xerror", "-filter_threads", "2", "-protocol_whitelist", "file"]);
        // For a still between two source frames, display the earlier one.
        // Keep the seek preroll: fps rounds the next frame up past time zero.
        if (still) args.Add("-noaccurate_seek");
        args.AddRange(["-ss", seek, "-i", file]);
        if (subtitle?.ExternalPath is { } path && !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("Subtitle path must be local.");
        }

        if (subtitle is { IsBitmap: true })
        {
            // Start the subtitle demuxer before the seek, so a cue already on
            // screen is available. Standalone SUP/IDX times are movie-relative,
            // including the delay before their first cue. The movie demuxer
            // already subtracts its own container start time.
            args.AddRange(["-protocol_whitelist", "file", "-itsoffset", "-" + seek]);
            if (subtitle.ExternalPath is not null)
            {
                args.AddRange(["-seek_timestamp", "1"]);
            }

            string graph = hardware ? $"[0:V:0]{acceleration!.Download}[main];" : string.Empty;
            string source = hardware ? "[main]" : "[0:V:0]";
            args.AddRange(["-i", subtitle.ExternalPath ?? file, "-filter_complex_threads", "2", "-filter_complex",
                $"{graph}[1:{subtitle.StreamIndex}]{source}scale2ref=w=iw:h=ih[sub][base];[base][sub]overlay=eof_action=pass,{fit},{pad}[picture]",
                "-map", "[picture]"]);
        }
        else
        {
            string filter = hardware ? acceleration!.Download + "," + fit : fit;
            if (subtitle is not null)
            {
                if (subtitle.ExternalPath is null)
                {
                    throw new InvalidDataException("Jellyfin did not supply the subtitle file.");
                }

                // libass cues use the movie clock, while -ss restarts the input
                // timeline. Restore that clock for rendering, then reset for AVI.
                filter += $",settb=AVTB,setpts=PTS+{seek}/TB,subtitles=filename={FilterValue("file:" + subtitle.ExternalPath)}";
                if (!string.IsNullOrWhiteSpace(subtitle.CharacterEncoding))
                {
                    filter += ":charenc=" + FilterValue(subtitle.CharacterEncoding);
                }

                filter += ",setpts=PTS-STARTPTS";
            }

            args.AddRange(["-map", "0:V:0", "-vf", filter + "," + pad]);
        }

        if (still)
            args.AddRange(["-frames:v", "1", "-an"]);
        else
            args.AddRange(["-map", "0:a:0?", "-af", "aresample=48000:async=1:first_pts=0,apad,asetnsamples=n=1600",
                "-c:a", "pcm_s16le", "-ar", "48000", "-ac", "2"]);
        args.AddRange(["-c:v", "rawvideo", "-threads:v", "2", "-pix_fmt", "yuv420p", "-map_metadata", "-1",
            "-shortest", "-max_interleave_delta", "0", "-flush_packets", "1", "-f", "avi", "pipe:1"]);
        return Launch(args);
    }

    // ArgumentList bypasses the shell. FFmpeg still parses two layers inside
    // -vf: first the filtergraph, then the filter's colon-separated options.
    private static string FilterValue(string value)
    {
        static string Escape(string text, string special)
        {
            var result = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (special.Contains(c) || char.IsWhiteSpace(c))
                {
                    result.Append('\\');
                }

                result.Append(c);
            }

            return result.ToString();
        }

        return Escape(Escape(value, "\\':"), "\\',[];");
    }

    private sealed class Decoder : IAsyncDisposable
    {
        private readonly Process process;
        private readonly CancellationTokenSource stop;
        private readonly CancellationTokenRegistration kill;
        private readonly Channel<byte[]> free = Channel.CreateBounded<byte[]>(3);
        private readonly Channel<byte[]> ready = Channel.CreateBounded<byte[]>(2);
        private readonly long openedAt;
        private readonly Task producer;
        private byte[]? displayed;
        private long readStarted;
        private long consumed;

        internal Decoder(Process process, int width, int height, CancellationToken token, bool still)
        {
            this.process = process;
            stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            kill = stop.Token.Register(() => Kill(process));
            var reader = new AviFrames(process.StandardOutput.BaseStream, width, height);
            // One packet may be held by the publisher. The others can fill
            // while it writes, without ever overwriting that complete picture.
            for (int i = 0; i < (still ? 1 : 3); i++) free.Writer.TryWrite(reader.CreateFrame());
            readStarted = openedAt = Stopwatch.GetTimestamp();
            producer = PrefetchAsync(reader, stop.Token);
        }

        internal long ReadStarted => Interlocked.Read(ref readStarted);
        internal bool Finished => ready.Reader.Completion.IsCompleted;
        internal double DisplayedOffsetSeconds => (consumed - 1) / (double)AviFrames.Fps;

        private async Task PrefetchAsync(AviFrames reader, CancellationToken token)
        {
            try
            {
                await reader.ReadHeaderAsync(token).ConfigureAwait(false);
                while (true)
                {
                    var frame = await free.Reader.ReadAsync(token).ConfigureAwait(false);
                    Interlocked.Exchange(ref readStarted, Stopwatch.GetTimestamp());
                    if (!await reader.ReadFrameAsync(frame, token).ConfigureAwait(false)) break;
                    await ready.Writer.WriteAsync(frame, token).ConfigureAwait(false);
                }
            }
            finally
            {
                ready.Writer.TryComplete();
            }
        }

        internal bool TryTake(out byte[] frame)
        {
            if (!ready.Reader.TryRead(out frame!)) return false;
            if (displayed is not null) free.Writer.TryWrite(displayed);
            consumed++;

            // Catch up only with already-decoded pairs. Never wait for a
            // second read or discard a useful final frame on a short batch.
            long expected = (long)(Stopwatch.GetElapsedTime(openedAt).TotalSeconds * AviFrames.Fps);
            for (int i = 0; i < 2 && consumed + 2 < expected && ready.Reader.TryRead(out var newer); i++)
            {
                free.Writer.TryWrite(frame);
                frame = newer;
                consumed++;
            }

            displayed = frame;
            return true;
        }

        internal async Task<int> ExitCodeAsync()
        {
            await producer.ConfigureAwait(false);
            await process.WaitForExitAsync(stop.Token).WaitAsync(Timeout, stop.Token).ConfigureAwait(false);
            return process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (Exception e) when (IsMediaError(e) || e is OperationCanceledException)
            {
                // Discard the cancelled decoder's incomplete frame.
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
            kill.Dispose();
            stop.Dispose();
            process.Dispose();
        }
    }
}
