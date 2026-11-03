using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.RtmpBroadcast;

internal static class SubtitleResolver
{
    internal static async Task<BroadcastSubtitle?> ResolveAsync(
        ISubtitleEncoder encoder, MediaSourceInfo source, int index, CancellationToken token)
    {
        var track = source.MediaStreams.FirstOrDefault(s => s.Index == index && s.Type == MediaStreamType.Subtitle);
        if (track is null || source.Protocol != MediaProtocol.File || source.IsRemote || track.IsExternalUrl == true
            || (track.IsExternal && !IsLocalFile(track.Path))) return null;

        if (!track.IsTextSubtitleStream && (track.IsExternal || !track.IsExtractableSubtitleStream))
        {
            // Jellyfin numbers external tracks together with container streams.
            // FFmpeg numbers streams separately inside each input file.
            var inputIndex = EncodingHelper.GetSubtitleStreamIndexForFfmpeg(source, track);
            return inputIndex < 0 ? null : new BroadcastSubtitle(
                true, track.IsExternal ? track.Path : null, inputIndex);
        }

        // Jellyfin caches embedded text, PGS and VobSub as standalone files.
        // Reopening a decoder after a seek should not rescan the whole movie.
        var file = await encoder.GetSubtitleFilePath(track, source, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!IsLocalFile(file)) return null;

        string? encoding = null;
        if (track.IsTextSubtitleStream && track.IsExternal && string.Equals(file, track.Path, StringComparison.Ordinal))
        {
            encoding = await encoder.GetSubtitleFileCharacterSet(track, track.Language, source, token).ConfigureAwait(false);
        }

        return new BroadcastSubtitle(!track.IsTextSubtitleStream, file, 0, encoding);
    }

    private static bool IsLocalFile(string? path) =>
        !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) && File.Exists(path);
}
