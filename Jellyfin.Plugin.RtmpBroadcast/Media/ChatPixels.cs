using SkiaSharp;

namespace Jellyfin.Plugin.RtmpBroadcast;

// Convert the changed overlay once, then alpha-blend only its small rectangle
// into the normalized YUV420p picture. Transparent corners and gaps draw nothing.
internal sealed class ChatPixels
{
    private readonly int width, height;
    private readonly byte[] luma, chromaU, chromaV, lumaAlpha, chromaAlpha;

    internal ChatPixels(SKBitmap bitmap)
    {
        width = bitmap.Width;
        height = bitmap.Height;
        luma = new byte[width * height];
        lumaAlpha = new byte[luma.Length];
        chromaU = new byte[luma.Length / 4];
        chromaV = new byte[chromaU.Length];
        chromaAlpha = new byte[chromaU.Length];
        var colors = bitmap.Pixels;

        // One chroma sample covers four luma pixels. Average their color in
        // proportion to their coverage, so antialiased edges keep their hue.
        for (int y = 0; y < height; y += 2)
        for (int x = 0; x < width; x += 2)
        {
            int alpha = 0, red = 0, green = 0, blue = 0;
            for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                int offset = (y + dy) * width + x + dx;
                var c = colors[offset];
                lumaAlpha[offset] = c.Alpha;
                luma[offset] = (byte)(16 + ((66 * c.Red + 129 * c.Green + 25 * c.Blue + 128) >> 8));
                alpha += c.Alpha;
                red += c.Red * c.Alpha;
                green += c.Green * c.Alpha;
                blue += c.Blue * c.Alpha;
            }

            if (alpha == 0) continue;
            int uv = (y / 2) * (width / 2) + x / 2;
            int r = red / alpha, g = green / alpha, b = blue / alpha;
            chromaU[uv] = (byte)(128 + ((-38 * r - 74 * g + 112 * b + 128) >> 8));
            chromaV[uv] = (byte)(128 + ((112 * r - 94 * g - 18 * b + 128) >> 8));
            chromaAlpha[uv] = (byte)((alpha + 2) / 4);
        }
    }

    internal void Blend(byte[] frame, int videoWidth, int videoHeight)
    {
        // Keep the newest card just below mid-height as older messages grow upward.
        // Align the origin to a 2x2 chroma block and leave a 5% right margin.
        int left = (videoWidth - videoWidth / 20 - width) & ~1;
        int top = (videoHeight * 3 / 5 - height) & ~1;
        int picture = videoWidth * videoHeight;
        const int videoHeader = 8;
        int y = videoHeader + top * videoWidth + left;
        int u = videoHeader + picture + (top / 2) * (videoWidth / 2) + left / 2;
        BlendPlane(frame, y, videoWidth, luma, lumaAlpha, width);
        BlendPlane(frame, u, videoWidth / 2, chromaU, chromaAlpha, width / 2);
        BlendPlane(frame, u + picture / 4, videoWidth / 2, chromaV, chromaAlpha, width / 2);
    }

    private static void BlendPlane(byte[] frame, int start, int stride, byte[] color, byte[] alpha, int rowWidth)
    {
        for (int row = 0; row < color.Length; row += rowWidth, start += stride)
        {
            for (int x = 0; x < rowWidth; x++)
            {
                int a = alpha[row + x];
                frame[start + x] = (byte)((a * color[row + x] + (255 - a) * frame[start + x] + 127) / 255);
            }
        }
    }
}
