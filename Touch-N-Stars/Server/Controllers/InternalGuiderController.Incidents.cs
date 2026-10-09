using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using TouchNStars.Server.Infrastructure;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

public partial class InternalGuiderController
{
    private const int ArchiveBufferSize = 64 * 1024;

    /// <summary>GET /api/internal-guider/incidents - the flight recorder's incident summaries (newest first) and budget.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/incidents")]
    public Task GetIncidents()
    {
        return WithRecorderAsync("incidents", false, recorder => SendOk(recorder.GetIncidents() ?? new AdvancedIncidentList()));
    }

    /// <summary>GET /api/internal-guider/incidents/{id} - AdvancedIncident with the telemetry of all its frames, markers and diagnosis.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/incidents/{id}")]
    public Task GetIncident(string id)
    {
        return WithIncidentAsync("incidents/get", id, recorder =>
        {
            AdvancedIncident incident = recorder.GetIncident(id);
            return incident == null ? SendIncidentNotFound(id) : SendOk(incident);
        });
    }

    /// <summary>GET /api/internal-guider/incidents/{id}/image?frame=N&amp;kind=context - a stored image of an incident frame as JPEG.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/incidents/{id}/image")]
    public Task GetIncidentImage(string id)
    {
        return WithIncidentAsync("incidents/image", id, async recorder =>
        {
            string rawKind = Query("kind");
            string kind = string.IsNullOrWhiteSpace(rawKind) ? InternalGuiderStates.IncidentImageContext : rawKind.Trim().ToLowerInvariant();
            if (!InternalGuiderStates.IncidentImageKinds.Contains(kind))
            {
                await SendError(400, "InvalidRequest", $"'kind' must be one of: {string.Join(", ", InternalGuiderStates.IncidentImageKinds)}.");
                return;
            }
            if (!TryQueryFrame(out long frame))
            {
                await SendError(400, "InvalidRequest", "'frame' must be a frame number of the incident.");
                return;
            }

            AdvancedIncidentImage image = recorder.GetIncidentImage(id, kind, frame);
            if (image?.Pixels == null || image.Width <= 0 || image.Height <= 0)
            {
                await (IncidentExists(recorder, id)
                    ? SendError(404, "NoImage", $"Frame {frame} of incident {id} has no {kind} image.")
                    : SendIncidentNotFound(id));
                return;
            }

            (byte[] jpeg, int binning, int width, int height) = InternalGuiderService.RenderIncidentImage(image, RenderOptionsFromQuery());

            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = "image/jpeg";
            HttpContext.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            HttpContext.Response.Headers.Add("X-Frame-Number", frame.ToString(CultureInfo.InvariantCulture));
            HttpContext.Response.Headers.Add("X-Image-Binning", binning.ToString(CultureInfo.InvariantCulture));
            HttpContext.Response.Headers.Add("X-Image-X0", image.X0.ToString(CultureInfo.InvariantCulture));
            HttpContext.Response.Headers.Add("X-Image-Y0", image.Y0.ToString(CultureInfo.InvariantCulture));
            HttpContext.Response.Headers.Add("X-Frame-Width", width.ToString(CultureInfo.InvariantCulture));
            HttpContext.Response.Headers.Add("X-Frame-Height", height.ToString(CultureInfo.InvariantCulture));
            HttpContext.Response.Headers.Add("Access-Control-Expose-Headers",
                "X-Frame-Number, X-Image-Binning, X-Image-X0, X-Image-Y0, X-Frame-Width, X-Frame-Height");
            using Stream stream = HttpContext.OpenResponseStream();
            await stream.WriteAsync(jpeg, 0, jpeg.Length);
        });
    }

    /// <summary>GET /api/internal-guider/incidents/{id}/crops?frame=N - the raw 16-bit star crops of an incident frame.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/incidents/{id}/crops")]
    public Task GetIncidentCrops(string id)
    {
        return WithIncidentAsync("incidents/crops", id, recorder =>
        {
            if (!TryQueryFrame(out long frame))
            {
                return SendError(400, "InvalidRequest", "'frame' must be a frame number of the incident.");
            }
            IReadOnlyList<AdvancedIncidentImage> crops = recorder.GetIncidentCrops(id, frame);
            if ((crops == null || crops.Count == 0) && !IncidentExists(recorder, id))
            {
                return SendIncidentNotFound(id);
            }
            return SendOk(InternalGuiderService.BuildIncidentCrops(frame, crops));
        });
    }

    /// <summary>POST /api/internal-guider/incidents/{id}/keep - body { kept }: keep the incident out of the budget rotation, or release it.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/incidents/{id}/keep")]
    public Task KeepIncident(string id)
    {
        return WithIncidentAsync("incidents/keep", id, async recorder =>
        {
            string body = await HttpContext.GetRequestBodyAsStringAsync();
            if (!InternalGuiderRequest.TryParseKeepBody(body, out bool kept, out string parseError))
            {
                await SendError(400, "InvalidRequest", parseError);
                return;
            }
            if (!recorder.SetIncidentKept(id, kept))
            {
                await SendIncidentNotFound(id);
                return;
            }

            AdvancedIncidentSummary summary = recorder.GetIncidents()?.Incidents?
                .FirstOrDefault(s => string.Equals(s?.Id, id, StringComparison.Ordinal));
            await (summary == null ? SendIncidentNotFound(id) : SendOk(summary));
        });
    }

    /// <summary>DELETE /api/internal-guider/incidents/{id} - delete one incident (kept or not).</summary>
    [Route(HttpVerbs.Delete, "/internal-guider/incidents/{id}")]
    public Task DeleteIncident(string id)
    {
        return WithIncidentAsync("incidents/delete", id, recorder => recorder.DeleteIncident(id)
            ? SendOk(new { deleted = true })
            : SendIncidentNotFound(id));
    }

    /// <summary>DELETE /api/internal-guider/incidents - delete all incidents that are not kept.</summary>
    [Route(HttpVerbs.Delete, "/internal-guider/incidents")]
    public Task DeleteAllIncidents()
    {
        return WithRecorderAsync("incidents/delete-all", false, recorder => SendOk(new { deleted = recorder.DeleteAllIncidents() }));
    }

    /// <summary>POST /api/internal-guider/incidents/mark - body { note }: record the last 2 minutes and the next 30 seconds as an incident.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/incidents/mark")]
    public Task MarkIncident()
    {
        return WithRecorderAsync("incidents/mark", true, async recorder =>
        {
            string body = await HttpContext.GetRequestBodyAsStringAsync();
            if (!InternalGuiderRequest.TryParseMarkBody(body, out string note, out string parseError))
            {
                await SendError(400, "InvalidRequest", parseError);
                return;
            }

            string id = recorder.MarkIncident(note, out string error);
            if (string.IsNullOrEmpty(id))
            {
                await SendError(409, "Rejected", string.IsNullOrWhiteSpace(error)
                    ? $"The guider did not mark an incident (state {recorder.State ?? "unknown"})."
                    : error);
                return;
            }
            await SendOk(new { id });
        });
    }

    /// <summary>GET /api/internal-guider/incidents/{id}/download - the incident as a zip, streamed while the guider writes it.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/incidents/{id}/download")]
    public Task DownloadIncident(string id)
    {
        return WithIncidentAsync("incidents/download", id, async recorder =>
        {
            var output = new DeferredResponseStream(() =>
            {
                HttpContext.Response.StatusCode = 200;
                HttpContext.Response.ContentType = "application/zip";
                HttpContext.Response.SendChunked = true;
                HttpContext.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
                HttpContext.Response.Headers.Add("Content-Disposition", $"attachment; filename=\"pins-incident-{id}.zip\"");
                HttpContext.Response.Headers.Add("Access-Control-Expose-Headers", "Content-Disposition");
                return HttpContext.Response.OutputStream;
            }, ArchiveBufferSize);

            bool written;
            try
            {
                written = await recorder.WriteIncidentArchive(id, output, HttpContext.CancellationToken);
                if (written) await output.CompleteAsync(HttpContext.CancellationToken);
            }
            catch (Exception ex) when (output.Started)
            {
                Logger.Warning($"InternalGuider: download of incident {id} broke off: {ex.Message}");
                return;
            }

            if (output.Started)
            {
                if (!written) Logger.Warning($"InternalGuider: the guider reported the archive of incident {id} as failed after it was sent.");
                return;
            }
            await (written
                ? SendError(500, "Error", $"The guider wrote an empty archive for incident {id}.")
                : SendIncidentNotFound(id));
        });
    }

    /// <summary>The incident routes: the internal guider (see <see cref="WithGuiderAsync"/>), 501 when it has no <see cref="IGuideIncidentRecorder"/>.</summary>
    private Task WithRecorderAsync(string what, bool requireConnected, Func<IGuideIncidentRecorder, Task> handler)
    {
        return WithGuiderAsync(what, requireConnected, guider => guider is IGuideIncidentRecorder recorder
            ? handler(recorder)
            : SendNotSupported("the flight recorder"));
    }

    /// <summary>
    /// The routes of one incident: the internal guider, connected or not (incidents are reviewed after the night), and
    /// a valid id (400 otherwise, before anything reaches the guider).
    /// </summary>
    private Task WithIncidentAsync(string what, string id, Func<IGuideIncidentRecorder, Task> handler)
    {
        return WithRecorderAsync(what, false, recorder => InternalGuiderRequest.IsValidIncidentId(id)
            ? handler(recorder)
            : SendError(400, "InvalidRequest", "The incident id may only contain letters, digits and '-'."));
    }

    /// <summary>Whether the incident is stored or being recorded; tells an unknown id from a frame without images.</summary>
    private static bool IncidentExists(IGuideIncidentRecorder recorder, string id)
    {
        AdvancedIncidentList list = recorder.GetIncidents();
        return list != null
            && (string.Equals(list.RecordingId, id, StringComparison.Ordinal)
                || (list.Incidents?.Any(s => string.Equals(s?.Id, id, StringComparison.Ordinal)) ?? false));
    }

    private Task SendIncidentNotFound(string id) => SendError(404, "NotFound", $"Unknown incident '{id}'.");

    private bool TryQueryFrame(out long frame)
    {
        frame = QueryLong("frame") ?? -1;
        return frame >= 0;
    }
}
