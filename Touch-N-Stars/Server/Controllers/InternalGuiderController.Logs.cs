using System;
using System.IO;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

public partial class InternalGuiderController
{
    private const int GuideLogBufferSize = 64 * 1024;

    /// <summary>GET /api/internal-guider/logs - the guide logs (one per night), newest first.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/logs")]
    public Task GetGuideLogs()
    {
        return WithGuideLogsAsync("logs", archive => SendOk(archive.GetGuideLogs() ?? Array.Empty<AdvancedGuideLogFile>()));
    }

    /// <summary>GET /api/internal-guider/logs/{name} - one guide log as text/plain (gzip when the client accepts it).</summary>
    [Route(HttpVerbs.Get, "/internal-guider/logs/{name}")]
    public Task GetGuideLog(string name)
    {
        return WithGuideLogsAsync("logs/get", async archive =>
        {
            if (!InternalGuiderRequest.IsValidGuideLogName(name))
            {
                await SendError(400, "InvalidRequest", "The guide log name may only contain letters, digits, '_', '-' and '.' and must end in .txt.");
                return;
            }

            using Stream log = archive.OpenGuideLog(name);
            if (log == null)
            {
                await SendError(404, "NotFound", $"Guide log {name} not found.");
                return;
            }

            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = "text/plain; charset=utf-8";
            HttpContext.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");

            // Once the headers are out, a failure (the phone left the WLAN, the app was closed, a read error on the card)
            // can no longer become an error response: log it and end the request.
            bool started = false;
            try
            {
                await using Stream output = HttpContext.OpenResponseStream(buffered: false, preferCompression: true);
                started = true;
                await log.CopyToAsync(output, GuideLogBufferSize, HttpContext.CancellationToken);
            }
            catch (Exception ex) when (started)
            {
                Logger.Warning($"InternalGuider: download of guide log {name} broke off: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// The guide log routes: the internal guider, connected or not (logs are reviewed after the night, also while another
    /// guider is connected); 501 when it has no <see cref="IGuideLogArchive"/>.
    /// </summary>
    private Task WithGuideLogsAsync(string what, Func<IGuideLogArchive, Task> handler)
    {
        return WithGuiderAsync(what, false, guider => guider is IGuideLogArchive archive
            ? handler(archive)
            : SendNotSupported("guide logs"), forIncidents: true);
    }
}
