# RTMP Broadcast for Jellyfin

RTMP Broadcast lets you share what you’re watching in Jellyfin with a live audience. You control playback in the Jellyfin web player while your server streams the video to an RTMP or RTMPS destination such as Twitch.

**AI disclosure.** I used OpenAI Codex to write and test this plugin and to draft its documentation.

## Installation

Requires Jellyfin Server 12.4.0 with Jellyfin FFmpeg and an administrator account.

1. Open **Dashboard → Plugins → Manage repositories → New repository**.
2. Name the repository **RTMP Broadcast** and enter this URL.

   ```text
   https://raw.githubusercontent.com/paubineau/jellyfin-plugin-rtmp-broadcast/main/manifest.json
   ```

3. Under **Plugins → Available**, install **RTMP Broadcast**. Restart Jellyfin and reload Jellyfin Web.

<details>
<summary>Manual installation</summary>

Extract the release ZIP into `plugins/RTMP Broadcast_<version>/` and restart Jellyfin. [Release downloads](https://github.com/paubineau/jellyfin-plugin-rtmp-broadcast/releases)

</details>

## Start a broadcast

1. Open **Dashboard → Plugins → RTMP Broadcast**. Enter the full **Publish URL** with the stream key if required, then click **Save destination**.
2. Play a video from your local library in Jellyfin’s built-in web player.
3. Click the antenna icon next to the settings gear to start broadcasting. Click it again to stop the broadcast and keep watching locally.

### Stream quality

- **Default output** — 1080p30 H.264 at 4,500 kb/s with stereo AAC.
- **Player settings** — Open **Settings → Broadcast settings** to change the resolution or bitrate for your next stream.

### Hardware acceleration

The plugin uses Jellyfin’s settings under **Dashboard → Playback → Transcoding**. During a broadcast, the plugin dashboard shows the active encoder and decoder.

### Twitch example

Use this **Publish URL**, replacing `{stream_key}` with your Twitch stream key.

```text
rtmps://ingest.global-contribute.live-video.net/app/{stream_key}
```

**Optional chat overlay** — In **Broadcast settings**, enable **Show Twitch chat on the broadcast** and enter your channel name. Chat appears on the right.

## Build from source

Run these commands from the repository root with the .NET 10 SDK. The ZIP is written to `dist/`.

```sh
dotnet test tests/RtmpBroadcast.Tests.csproj -c Release
dotnet build Jellyfin.Plugin.RtmpBroadcast/Jellyfin.Plugin.RtmpBroadcast.csproj -c Release -t:PackagePlugin
```

For the media integration test, set `JELLYFIN_FFMPEG` to the FFmpeg executable before running the tests.

## License

RTMP Broadcast is licensed under the [GNU General Public License, version 3 only](LICENSE).
