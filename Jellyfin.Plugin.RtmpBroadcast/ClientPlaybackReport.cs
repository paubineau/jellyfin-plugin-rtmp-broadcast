using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.RtmpBroadcast;

// Jellyfin's progress timer can overwrite SessionInfo.PlayState while the
// user's new report is being published. Keep that report available to the
// playback event handler for the duration of the authenticated Web request.
internal sealed class ClientPlaybackReport : IAsyncActionFilter
{
    private static readonly object Key = new();

    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is ControllerActionDescriptor
            { ControllerName: "Playstate", ActionName: "ReportPlaybackStart" or "ReportPlaybackProgress" }
            && context.ActionArguments.Values.OfType<PlaybackProgressInfo>().FirstOrDefault() is { } report)
        {
            return WithReportAsync(context.HttpContext, report, next);
        }

        return next();
    }

    private static async Task WithReportAsync(HttpContext context, PlaybackProgressInfo report, ActionExecutionDelegate next)
    {
        context.Items[Key] = report;
        try { await next().ConfigureAwait(false); }
        finally { context.Items.Remove(Key); }
    }

    internal static PlaybackProgressInfo? Get(HttpContext? context, string sessionId) =>
        context?.Items[Key] is PlaybackProgressInfo report && report.SessionId == sessionId ? report : null;
}
