using System;
using System.IO;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
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
            using Stream output = HttpContext.OpenResponseStream(buffered: false, preferCompression: true);
            await log.CopyToAsync(output, GuideLogBufferSize, HttpContext.CancellationToken);
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
