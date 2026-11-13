using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;
using NINA.Core.Utility;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// End of the night and the checks around it:
/// GET  /api/secure-observatory         the running or last shutdown, with its steps
/// POST /api/secure-observatory         start it; body { park, closeDome, closeCover, warmCamera, disconnect, warmMinutes }
/// POST /api/secure-observatory/cancel  stop the remaining steps (a closing dome keeps closing)
/// GET  /api/preflight/image-disk       free space where the frames are saved
/// Answers use camelCase.
/// </summary>
public class SecureObservatoryController : WebApiController
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly SecureObservatoryService service;

    public SecureObservatoryController() : this(SecureObservatoryService.Instance) { }

    internal SecureObservatoryController(SecureObservatoryService service)
    {
        this.service = service;
    }

    [Route(HttpVerbs.Get, "/secure-observatory")]
    public Task GetStatus() => Send(service.GetStatus());

    [Route(HttpVerbs.Post, "/secure-observatory")]
    public async Task Start()
    {
        SecureObservatoryOptions options;
        try
        {
            string body = await HttpContext.GetRequestBodyAsStringAsync();
            options = string.IsNullOrWhiteSpace(body)
                ? new SecureObservatoryOptions()
                : JsonConvert.DeserializeObject<SecureObservatoryOptions>(body) ?? new SecureObservatoryOptions();
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await Send(new { error = $"Invalid body: {ex.Message}" });
            return;
        }

        var remote = HttpContext?.Request?.RemoteEndPoint?.ToString() ?? "unknown";
        Logger.Info($"TNS: secure observatory requested from {remote}");
        await Send(service.Start(options));
    }

    [Route(HttpVerbs.Post, "/secure-observatory/cancel")]
    public Task Cancel() => Send(service.Cancel());

    [Route(HttpVerbs.Get, "/preflight/image-disk")]
    public Task GetImageDisk()
    {
        string path = null;
        try
        {
            path = TouchNStars.Mediators?.Profile?.ActiveProfile?.ImageFileSettings?.FilePath;
        }
        catch (Exception ex)
        {
            Logger.Warning($"TNS: could not read the image folder: {ex.Message}");
        }

        return Send(ImageDisk.Read(path));
    }

    private Task Send(object data) =>
        HttpContext.SendStringAsync(InternalGuiderJson.Serialize(data), "application/json", Utf8NoBom);
}

/// <summary>Free space on the disk the frames are saved to.</summary>
internal static class ImageDisk
{
    public static object Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new { path, exists = false, freeBytes = (long?)null, totalBytes = (long?)null };
        }

        bool exists = Directory.Exists(path);
        long? free = null;
        long? total = null;

        try
        {
            // On Linux DriveInfo takes any path and reports the file system it lives on.
            var drive = new DriveInfo(exists ? path : Path.GetPathRoot(Path.GetFullPath(path)));
            if (drive.IsReady)
            {
                free = drive.AvailableFreeSpace;
                total = drive.TotalSize;
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"TNS: could not read the free space of {path}: {ex.Message}");
        }

        return new { path, exists, freeBytes = free, totalBytes = total };
    }
}
