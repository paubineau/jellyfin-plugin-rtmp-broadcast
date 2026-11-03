using System;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.RtmpBroadcast;

// This is the wire format produced by our own non-seekable FFmpeg muxer.
// One YUV420 picture and 1/30 second of stereo PCM have identical duration.
internal sealed class AviFrames
{
    internal const int Fps = 30;
    private const int AudioBytes = 48000 / Fps * 2 * 2;

    private readonly Stream input;
    private readonly int videoBytes;
    private readonly int audioHeader;
    private readonly byte[] tag = new byte[8];
    private bool hasAudio;

    internal AviFrames(Stream input, int width, int height)
    {
        this.input = input;
        videoBytes = width * height * 3 / 2;
        audioHeader = 8 + videoBytes;
    }

    internal byte[] CreateFrame()
    {
        var frame = new byte[audioHeader + 8 + AudioBytes];
        "00dc"u8.CopyTo(frame);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), videoBytes);
        "01wb"u8.CopyTo(frame.AsSpan(audioHeader));
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(audioHeader + 4), AudioBytes);
        return frame;
    }

    internal static void Silence(byte[] frame) => Array.Clear(frame, frame.Length - AudioBytes, AudioBytes);

    internal async Task<byte[]> ReadHeaderAsync(CancellationToken token)
    {
        using var header = new MemoryStream();
        var riff = new byte[12];
        await input.ReadExactlyAsync(riff, token).ConfigureAwait(false);
        if (!riff.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !riff.AsSpan(8, 4).SequenceEqual("AVI "u8))
        {
            throw new InvalidDataException("Invalid decoder header.");
        }

        // FFmpeg's demuxer treats a zero-length RIFF + movi on a pipe as
        // unbounded. Otherwise a long-lived raw stream can end at 4 GiB.
        Array.Clear(riff, 4, 4);
        header.Write(riff);
        while (true)
        {
            await input.ReadExactlyAsync(tag, token).ConfigureAwait(false);
            int size = BinaryPrimitives.ReadInt32LittleEndian(tag.AsSpan(4));
            var kind = Array.Empty<byte>();
            if (tag.AsSpan(0, 4).SequenceEqual("LIST"u8))
            {
                kind = new byte[4];
                await input.ReadExactlyAsync(kind, token).ConfigureAwait(false);
                if (kind.AsSpan().SequenceEqual("movi"u8))
                {
                    Array.Clear(tag, 4, 4);
                    header.Write(tag);
                    header.Write(kind);
                    return header.ToArray();
                }

                size -= 4;
            }

            if (size < 0 || header.Length + size > 256 * 1024)
            {
                throw new InvalidDataException("Invalid decoder header length.");
            }

            var data = new byte[size + (size & 1)];
            await input.ReadExactlyAsync(data, token).ConfigureAwait(false);
            if (kind.AsSpan().SequenceEqual("hdrl"u8))
            {
                // The FFmpeg AVI hdrl begins with avih, whose dwStreams is
                // DWORD #7. The decoder maps exactly one video and optionally audio.
                if (data.Length < 36 || !data.AsSpan(0, 4).SequenceEqual("avih"u8))
                {
                    throw new InvalidDataException("Invalid decoder stream header.");
                }

                hasAudio = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(32)) == 2;
            }

            header.Write(tag);
            header.Write(kind);
            header.Write(data);
        }
    }

    internal async Task<bool> ReadFrameAsync(byte[] frame, CancellationToken token)
    {
        bool haveVideo = false;
        while (true)
        {
            int count = await input.ReadAtLeastAsync(tag, 8, throwOnEndOfStream: false, token).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            if (count != 8)
            {
                throw new EndOfStreamException();
            }

            int size = BinaryPrimitives.ReadInt32LittleEndian(tag.AsSpan(4));
            if (tag.AsSpan(0, 4).SequenceEqual("00dc"u8) && size == videoBytes && !haveVideo)
            {
                await input.ReadExactlyAsync(frame.AsMemory(8, videoBytes), token).ConfigureAwait(false);
                if (!hasAudio)
                {
                    return true; // PCM in this decoder's buffer is initialized to silence.
                }

                haveVideo = true;
            }
            else if (tag.AsSpan(0, 4).SequenceEqual("01wb"u8) && size == AudioBytes)
            {
                await input.ReadExactlyAsync(frame.AsMemory(audioHeader + 8), token).ConfigureAwait(false);
                if (haveVideo)
                {
                    return true;
                }

                // -shortest can flush surplus padded PCM after the last video frame.
            }
            else
            {
                throw new InvalidDataException("Unexpected decoder packet.");
            }
        }

        if (haveVideo)
        {
            Silence(frame);
        }

        return haveVideo;
    }
}
