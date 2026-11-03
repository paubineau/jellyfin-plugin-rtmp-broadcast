using System.ComponentModel.DataAnnotations;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RtmpBroadcast;

[ApiController]
[Route("TwitchBroadcast")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class BroadcastController(BroadcastService service) : ControllerBase
{
    // Jellyfin's authenticated principal identifies the operator, irrespective
    // of what session or device the request body asks to follow.
    private Guid OperatorId => Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;

    // Jellyfin Web loads the player shell before login. Only the static code
    // is public; every endpoint that reads or changes broadcast state is gated.
    [AllowAnonymous]
    [HttpGet("Player.js")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult PlayerScript() => File(
        typeof(Plugin).Assembly.GetManifestResourceStream("Jellyfin.Plugin.RtmpBroadcast.Web.player.js")!,
        "application/javascript; charset=utf-8");

    [HttpGet("Sessions")]
    public IActionResult Sessions() => Ok(service.GetSessions(OperatorId).Select(s => new
    {
        id = s.SessionId, deviceId = s.DeviceId, deviceName = s.DeviceName, itemId = s.ItemId, itemName = s.ItemName
    }));

    [HttpGet("Status")]
    public IActionResult Status()
    {
        var status = service.GetStatus(OperatorId);
        var settings = Plugin.Instance!.Settings;
        return Ok(new
        {
            active = status.Active, state = status.State.ToLowerInvariant(), sessionId = status.SessionId,
            itemName = status.ItemName, configured = !string.IsNullOrEmpty(Plugin.Instance?.Destination),
            outputHeight = settings.OutputHeight, videoBitrateKbps = settings.VideoBitrateKbps,
            chatEnabled = settings.ChatEnabled, chatChannel = settings.ChatChannel, chatStatus = status.ChatStatus,
            accelerationStatus = status.AccelerationStatus
        });
    }

    public sealed record Selection([Required] string SessionId, Guid? ItemId);

    [HttpPost("Start")]
    public IActionResult Start(Selection selection)
    {
        try
        {
            service.StartBroadcast(OperatorId, selection.SessionId, selection.ItemId);
            return Status();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpPost("Stop")]
    public async Task<IActionResult> Stop()
    {
        try
        {
            await service.StopBroadcastAsync(OperatorId).ConfigureAwait(false);
            return Status();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    public sealed record ReceiveAddress([Required, StringLength(4096)] string Url);

    public sealed record QualityRequest(int OutputHeight, int VideoBitrateKbps);

    [HttpPost("Quality")]
    public IActionResult Quality(QualityRequest request)
    {
        if (!StreamQuality.IsValidQuality(request.OutputHeight, request.VideoBitrateKbps))
            return BadRequest(new { message = StreamQuality.ValidationMessage });

        Plugin.Instance!.EditSettings(settings =>
        {
            settings.OutputHeight = request.OutputHeight;
            settings.VideoBitrateKbps = request.VideoBitrateKbps;
        });
        return NoContent();
    }

    public sealed record ChatRequest(bool Enabled, string? Channel);

    [HttpPost("Chat")]
    public IActionResult Chat(ChatRequest request)
    {
        var channel = (request.Channel ?? string.Empty).Trim();
        if (channel.StartsWith('#')) channel = channel[1..];
        if (!StreamQuality.IsValidChat(request.Enabled, channel))
            return BadRequest(new { message = StreamQuality.ChatValidationMessage });

        Plugin.Instance!.EditSettings(settings =>
        {
            settings.ChatChannel = channel;
            settings.ChatEnabled = request.Enabled;
        });
        return NoContent();
    }

    [HttpPost("Destination")]
    public IActionResult Destination(ReceiveAddress address)
    {
        var value = address.Url.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("rtmp" or "rtmps")
            || uri.Host.Length == 0 || uri.AbsolutePath.Trim('/').Length == 0 || uri.Fragment.Length > 0
            || value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return BadRequest(new { message = "Enter the full RTMP or RTMPS publish URL, including the stream path." });
        }

        Plugin.Instance!.SaveDestination(value);
        return NoContent();
    }
}
