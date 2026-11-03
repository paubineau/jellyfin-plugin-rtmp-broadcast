using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RtmpBroadcast;

public sealed class StreamQuality : BasePluginConfiguration
{
    public int OutputHeight { get; set; } = 1080;
    public int VideoBitrateKbps { get; set; } = 4500;
    public bool ChatEnabled { get; set; }
    public string ChatChannel { get; set; } = string.Empty;

    internal const string ValidationMessage = "Choose 480p, 720p or 1080p and a bitrate between 500 and 12000 kb/s.";
    internal const string ChatValidationMessage = "Enter a Twitch channel name using up to 25 letters, digits or underscores.";
    internal StreamQuality Copy() => (StreamQuality)MemberwiseClone();
    internal static bool IsValidQuality(int height, int bitrate) =>
        height is 480 or 720 or 1080 && bitrate is >= 500 and <= 12000;

    internal static bool IsValidChat(bool enabled, string? channel) =>
        string.IsNullOrEmpty(channel) ? !enabled :
        channel.Length <= 25 && channel.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
