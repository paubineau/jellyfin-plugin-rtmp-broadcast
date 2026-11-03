using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RtmpBroadcast;

public sealed class Plugin : BasePlugin<StreamQuality>, IHasWebPages
{
    private readonly string _destinationFile;
    private readonly object _settingsLock = new();
    private readonly object _qualityFileLock = new();
    private string? _destination;

    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer)
    {
        _destinationFile = Path.Combine(paths.PluginConfigurationsPath, "TwitchBroadcast.destination");
        _destination = File.Exists(_destinationFile) ? File.ReadAllText(_destinationFile).Trim() : null;
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }
    public override Guid Id => new("d5d3a401-8609-4f19-9b37-2428e3ef6ab3");
    public override string Name => "RTMP Broadcast";
    public override string Description => "Send the video you're watching in Jellyfin Web to an RTMP or RTMPS server.";
    internal string? Destination => Volatile.Read(ref _destination);
    internal StreamQuality Settings
    {
        get
        {
            lock (_settingsLock)
            {
                var saved = Configuration;
                return StreamQuality.IsValidQuality(saved.OutputHeight, saved.VideoBitrateKbps)
                    && StreamQuality.IsValidChat(saved.ChatEnabled, saved.ChatChannel) ? saved : new StreamQuality();
            }
        }
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is not StreamQuality quality || !StreamQuality.IsValidQuality(quality.OutputHeight, quality.VideoBitrateKbps))
            throw new ArgumentException(StreamQuality.ValidationMessage, nameof(configuration));
        if (!StreamQuality.IsValidChat(quality.ChatEnabled, quality.ChatChannel))
            throw new ArgumentException(StreamQuality.ChatValidationMessage, nameof(configuration));

        lock (_settingsLock)
        {
            quality.ChatChannel = quality.ChatChannel?.ToLowerInvariant() ?? string.Empty;
            var previous = Configuration;
            try { base.UpdateConfiguration(quality); }
            catch { Configuration = previous; throw; }
        }
    }

    internal void EditSettings(Action<StreamQuality> edit)
    {
        lock (_settingsLock)
        {
            var updated = Settings.Copy();
            edit(updated);
            UpdateConfiguration(updated);
        }
    }

    public override void SaveConfiguration(StreamQuality config)
    {
        // Jellyfin also calls this on the first read to create default settings.
        lock (_qualityFileLock)
        {
            Directory.CreateDirectory(ApplicationPaths.PluginConfigurationsPath);
            var temporary = ConfigurationFilePath + ".tmp";
            XmlSerializer.SerializeToFile(config, temporary);
            File.Move(temporary, ConfigurationFilePath, overwrite: true);
        }
    }

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "TwitchBroadcast",
            DisplayName = Name,
            EmbeddedResourcePath = "Jellyfin.Plugin.RtmpBroadcast.Web.config.html",
            EnableInMainMenu = true
        }
    ];

    internal void SaveDestination(string value)
    {
        // A receive URL may contain a publish token. Keep it out of the standard
        // plugin configuration API, which returns the full configuration object.
        lock (_settingsLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_destinationFile)!);
            var temporary = _destinationFile + ".tmp";
            var options = new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.Create };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var writer = new StreamWriter(new FileStream(temporary, options)))
            {
                writer.Write(value);
            }

            File.Move(temporary, _destinationFile, overwrite: true);
            Volatile.Write(ref _destination, value);
        }
    }
}
