using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Jellyfin.Plugin.RtmpBroadcast;
using Xunit;

namespace RtmpBroadcast.Tests;

public class PausedBroadcastTests
{
    [FfmpegFact]
    public async Task StartingPausedPublishesTheSelectedFrameAndSilence()
    {
        var ffmpeg = Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG")!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var directory = Directory.CreateTempSubdirectory("rtmp-broadcast-test-");
        var movie = Path.Combine(directory.FullName, "movie.mkv");
        var capture = Path.Combine(directory.FullName, "capture.flv");
        try
        {
            // Only the selected half-second is blue. Playing on, choosing the first
            // frame or switching to standby will all produce a different picture.
            await Ffmpeg(ffmpeg, token, "-f", "lavfi", "-i",
                "color=red:s=160x90:r=30:d=3,drawbox=c=blue:t=fill:enable='between(t,0.5,1)'",
                "-f", "lavfi", "-i", "sine=frequency=733:duration=3", "-c:v", "libx264", "-c:a", "aac", movie);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var recording = ReceiveFlv(listener, capture, token);
            try
            {
                await using var pipeline = new BroadcastPipeline(ffmpeg, $"tcp://127.0.0.1:{port}/test/still", 480, 1000);
                // At 30 fps the blue frame at 1.000 is still visible. The next frame is red.
                pipeline.SetPlayback(movie, TimeSpan.FromSeconds(1.02), paused: true);
                while (pipeline.Status != "Paused") await Task.Delay(30, token);
                // Observe the hold for longer than the remaining blue portion of the movie.
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            }
            finally
            {
                await recording;
            }

            var video = await Ffmpeg(ffmpeg, token, "-i", capture, "-an", "-vf", "scale=8:4", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1");
            const int lastHalfSecond = 15 * 8 * 4 * 3;
            Assert.True(video.Length >= lastHalfSecond);
            for (int i = video.Length - lastHalfSecond; i < video.Length; i += 3)
                Assert.True(video[i] < 45 && video[i + 1] < 45 && video[i + 2] > 195, "Expected the held blue movie frame.");
            var audio = await Ffmpeg(ffmpeg, token, "-i", capture, "-vn", "-ac", "1", "-ar", "8000", "-f", "s16le", "pipe:1");
            Assert.True(audio.Length >= 8000); // At least half a second of mono PCM.
            Assert.All(audio, sample => Assert.Equal((byte)0, sample));
        }
        finally
        {
            File.Delete(capture);
            File.Delete(movie);
            directory.Delete();
        }
    }

    private static async Task ReceiveFlv(TcpListener listener, string path, CancellationToken token)
    {
        using var client = await listener.AcceptTcpClientAsync(token);
        await using var output = File.Create(path);
        try { await client.GetStream().CopyToAsync(output, token); }
        // Stopping a live publisher can reset the connection after its final complete packets.
        catch (IOException e) when (e.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset }) { }
    }

    private static async Task<byte[]> Ffmpeg(string executable, CancellationToken token, params string[] args)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-nostdin", "-loglevel", "error" }.Concat(args)) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        using var stop = token.Register(() => { try { process.Kill(); } catch (InvalidOperationException) { } });
        var errors = process.StandardError.ReadToEndAsync();
        using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes, token);
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, await errors);
        return bytes.ToArray();
    }

    private sealed class FfmpegFactAttribute : FactAttribute
    {
        public FfmpegFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG")))
                Skip = "Set JELLYFIN_FFMPEG to the Jellyfin FFmpeg executable to run the media integration test.";
        }
    }
}
