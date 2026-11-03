using System.Diagnostics;
using Jellyfin.Plugin.RtmpBroadcast.Chat;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Jellyfin.Plugin.RtmpBroadcast;

internal sealed class ChatOverlay(int width, int height) : IDisposable
{
    private readonly object gate = new();
    private IReadOnlyList<ChatLine> previous = [];
    private ChatPixels? current;
    private bool disposed;
    private bool unavailable;
    private bool missingDependency;
    private long retryAt;

    // Only the publisher touches this composite. Keep the clean source so a
    // repeated picture can show updated or cleared chat without ghosting.
    private ChatPixels? cachedPixels;
    private byte[]? cachedSource;
    private byte[]? composite;

    internal bool Unavailable => Volatile.Read(ref unavailable);

    internal void SetLines(IReadOnlyList<ChatLine> lines)
    {
        lock (gate)
        {
            if (disposed || missingDependency) return;
            bool unchanged = previous.SequenceEqual(lines);
            if (unchanged && (!unavailable || Stopwatch.GetTimestamp() < retryAt)) return;
            if (!unchanged) previous = lines.ToArray();
            try
            {
                var next = previous.Count == 0 ? null : Render(previous);
                Volatile.Write(ref current, next);
                Volatile.Write(ref unavailable, false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // An optional font/native renderer failure must never retain
                // moderated text or interrupt the RTMP publisher.
                Volatile.Write(ref current, null);
                Volatile.Write(ref unavailable, true);
                missingDependency = e.GetBaseException() is DllNotFoundException or EntryPointNotFoundException
                    or BadImageFormatException or TypeLoadException or FileNotFoundException;
                if (missingDependency) previous = [];
                retryAt = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
            }
        }
    }

    internal byte[] Apply(byte[] frame, bool freshMedia)
    {
        var pixels = Volatile.Read(ref current);
        if (pixels is null) return frame;

        if (freshMedia || !ReferenceEquals(pixels, cachedPixels) || !ReferenceEquals(frame, cachedSource))
        {
            composite ??= new byte[frame.Length];
            frame.CopyTo(composite, 0);
            pixels.Blend(composite, width, height);
            cachedPixels = pixels;
            cachedSource = frame;
        }

        return composite!;
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            previous = [];
            Volatile.Write(ref current, null);
            cachedPixels = null;
            cachedSource = composite = null;
        }
    }

    private ChatPixels Render(IReadOnlyList<ChatLine> lines)
    {
        float size = Math.Max(13, height / 41f);
        string family = OperatingSystem.IsWindows() ? "Segoe UI" : "sans-serif";
        using var regular = SKTypeface.FromFamilyName(family, SKFontStyle.Normal);
        using var bold = SKTypeface.FromFamilyName(family, SKFontStyle.Bold);
        using var bodyFont = new SKFont(regular, size);
        using var nameFont = new SKFont(bold, size);
        using var bodyShaper = new SKShaper(regular);
        using var nameShaper = new SKShaper(bold);
        using var paint = new SKPaint { IsAntialias = true };

        float padX = size * 0.59f, padY = size * 0.26f, gap = size * 0.23f;
        var metrics = bodyFont.Metrics;
        float lineHeight = (float)Math.Ceiling(metrics.Descent - metrics.Ascent + size * 0.08f);
        float limit = width * 0.4f - 2 * padX;
        var rows = lines.Select(line =>
        {
            string name = line.Sender is null ? "" : line.Sender + (line.Text.Length == 0 ? ":" : ": ");
            float nameWidth = name.Length == 0 ? 0 : nameShaper.Shape(name, nameFont).Width;
            float bodyWidth = line.Text.Length == 0 ? 0 : bodyShaper.Shape(line.Text, bodyFont).Width;
            return (Line: line, Name: name, NameWidth: nameWidth, Width: nameWidth + bodyWidth);
        }).ToArray();

        int messages = rows.Count(row => row.Line.Sender is not null);
        int tileWidth = EvenCeiling(2 * padX + Math.Min(limit, rows.Max(row => row.Width)));
        float contentHeight = rows.Length * lineHeight + messages * (2 * padY + gap) - gap;
        int tileHeight = EvenCeiling(contentHeight);
        using var bitmap = new SKBitmap(tileWidth, tileHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        float y = tileHeight - contentHeight;
        for (int first = 0; first < rows.Length;)
        {
            int end = first + 1;
            float longest = rows[first].Width;
            while (end < rows.Length && rows[end].Line.Sender is null)
                longest = Math.Max(longest, rows[end++].Width);

            float boxHeight = 2 * padY + (end - first) * lineHeight;
            float boxWidth = 2 * padX + Math.Min(limit, longest);
            float left = tileWidth - boxWidth;
            paint.Color = new SKColor(12, 17, 25, 196);
            float radius = size * 0.42f;
            canvas.DrawRoundRect(left, y, boxWidth, boxHeight, radius, radius, paint);
            for (int i = first; i < end; i++)
            {
                var row = rows[i];
                canvas.Save();
                canvas.Translate(left + padX, y + padY - metrics.Ascent + (i - first) * lineHeight);
                if (row.Width > limit) canvas.Scale(limit / row.Width, 1);
                if (row.Line.Sender is { } sender)
                {
                    paint.Color = NameColor(sender, row.Line.Color);
                    canvas.DrawShapedText(nameShaper, row.Name, 0, 0, SKTextAlign.Left, nameFont, paint);
                }

                if (row.Line.Text.Length > 0)
                {
                    paint.Color = new SKColor(248, 249, 252);
                    canvas.DrawShapedText(bodyShaper, row.Line.Text, row.NameWidth, 0, SKTextAlign.Left, bodyFont, paint);
                }

                canvas.Restore();
            }

            y += boxHeight + gap;
            first = end;
        }

        return new ChatPixels(bitmap);
    }

    private static int EvenCeiling(float n) => ((int)Math.Ceiling(n) + 1) & ~1;

    private static SKColor NameColor(string sender, string? supplied)
    {
        if (supplied is not null && SKColor.TryParse(supplied, out var color))
        {
            int low = Math.Min(color.Red, Math.Min(color.Green, color.Blue));
            int range = Math.Max(color.Red, Math.Max(color.Green, color.Blue)) - low;
            if (range >= 12)
            {
                // Keep the Twitch hue but bring very dark/saturated names into
                // a consistent contrast range against a bright movie scene.
                byte Tint(byte c) => (byte)(175 + (c - low) * 74 / range);
                return new SKColor(Tint(color.Red), Tint(color.Green), Tint(color.Blue));
            }
        }

        // Stable across restarts, including senders without a Twitch color.
        uint hash = 2166136261;
        foreach (char c in sender) hash = unchecked((hash ^ char.ToUpperInvariant(c)) * 16777619);
        return SKColor.FromHsv(hash % 360, 29, 98);
    }
}
