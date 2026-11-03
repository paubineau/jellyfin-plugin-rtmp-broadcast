using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.RtmpBroadcast;

// Jellyfin 12.4 has no server extension point for a video OSD button. Extend
// the final server-hosted Web page so other plugins' markup is preserved.
internal sealed class PlayerUiStartupFilter(IServerConfigurationManager config) : IStartupFilter
{
    private const string Script = """<script defer src="../TwitchBroadcast/Player.js"></script>""";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        var web = new PathString(config.GetNetworkConfiguration().BaseUrl.TrimEnd('/') + "/web/");
        var index = new PathString(web.Value + "index.html");
        app.UseWhen(context =>
            (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            && (context.Request.Path == web || context.Request.Path == index),
            branch => branch.Use(ExtendWebPage));
        next(app);
    };

    private static async Task ExtendWebPage(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        var response = context.Response;
        var method = request.Method;
        var original = response.Body;
        string[] entityHeaders = [HeaderNames.AcceptEncoding, HeaderNames.Range, HeaderNames.IfRange,
            HeaderNames.IfMatch, HeaderNames.IfNoneMatch, HeaderNames.IfModifiedSince, HeaderNames.IfUnmodifiedSince];
        var saved = entityHeaders.Where(request.Headers.ContainsKey).ToDictionary(k => k, k => request.Headers[k]);

        // Negotiate against the transformed entity, even for a cached GET or
        // HEAD. First let Jellyfin serve its entire uncompressed index.
        using var buffer = new MemoryStream();
        try
        {
            foreach (var key in saved.Keys) request.Headers.Remove(key);
            request.Method = HttpMethods.Get;
            response.Body = buffer;
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            response.Body = original;
            request.Method = method;
            foreach (var (key, value) in saved) request.Headers[key] = value;
        }

        if (response.StatusCode != StatusCodes.Status200OK
            || response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) != true
            || response.Headers.ContainsKey(HeaderNames.ContentEncoding))
        {
            buffer.Position = 0;
            if (!HttpMethods.IsHead(method)) await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        var end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (end >= 0 && !html.Contains(Script, StringComparison.Ordinal))
        {
            html = html.Insert(end, Script);
        }

        var body = Encoding.UTF8.GetBytes(html);
        var tag = new EntityTagHeaderValue("\"" + Convert.ToHexString(SHA256.HashData(body)) + "\"");
        response.Headers.Remove(HeaderNames.LastModified);
        response.ContentLength = null;
        await Results.File(body, response.ContentType, entityTag: tag, enableRangeProcessing: true)
            .ExecuteAsync(context).ConfigureAwait(false);
    }
}
