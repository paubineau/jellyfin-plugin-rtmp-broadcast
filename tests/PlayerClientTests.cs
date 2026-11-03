using Jellyfin.Plugin.RtmpBroadcast;
using Xunit;

namespace RtmpBroadcast.Tests;

public class PlayerClientTests
{
    [Theory]
    [InlineData("Jellyfin Web", true)]
    [InlineData("Jellyfin for Android", true)]
    [InlineData("Jellyfin Android", true)]
    [InlineData("Jellyfin Android TV", false)]
    [InlineData(null, false)]
    public void RecognizesWebAndAndroidClientNames(string? client, bool supported)
    {
        Assert.Equal(supported, BroadcastService.SupportsPlayerClient(client));
    }
}
