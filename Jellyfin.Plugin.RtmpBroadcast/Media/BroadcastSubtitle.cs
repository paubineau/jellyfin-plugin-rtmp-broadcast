namespace Jellyfin.Plugin.RtmpBroadcast;

// Text tracks are standalone files resolved by Jellyfin. Bitmap streams
// can also live inside the movie or a multi-track external subtitle file.
internal sealed record BroadcastSubtitle(bool IsBitmap, string? ExternalPath, int StreamIndex, string? CharacterEncoding = null);
