using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Image.ImageData;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TouchNStars.Server.Models;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Controller for browsing and managing the local filesystem.
/// Routes:
///   GET    /api/filesystem/browse?path=...[&amp;offset=..&amp;limit=..] — list directories, then files (optionally a page)
///   POST   /api/filesystem/directory               — create a directory (body: { "path": "..." })
///   DELETE /api/filesystem/directory?path=...      — delete a directory (recursive)
///   GET    /api/filesystem/file?path=...&amp;download=1 — stream a file (binary)
///   GET    /api/filesystem/imageinfo?path=...              — image/FITS metadata for preview
///   GET    /api/filesystem/preview?path=...&amp;maxWidth=...   — server-rendered JPEG/PNG preview
///   PUT    /api/filesystem/rename                  — rename/move (body: { "sourcePath", "targetPath" })
///   DELETE /api/filesystem/file?path=...           — delete a file
/// </summary>
public class FilesystemController : WebApiController
{
    private const int StreamBufferSize = 81920;
    private const int ExdevErrno = 18;

    private readonly ImagePreviewService previewService = new();

    // Deliberately not BaseImageData.FileIsSupported's regex — it is missing .fz/.rw2/.dng.
    // Mirrors the extension switch in NINA.Image.ImageData.BaseImageData.FromFile instead.
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".gif", ".tif", ".tiff", ".jpg", ".jpeg", ".png",
        ".xisf", ".fit", ".fits", ".fts", ".fz",
        ".cr2", ".cr3", ".nef", ".raf", ".raw", ".pef", ".dng", ".arw", ".orf", ".rw2"
    };

    private static readonly HashSet<string> RawImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cr2", ".cr3", ".nef", ".raf", ".raw", ".pef", ".dng", ".arw", ".orf", ".rw2"
    };

    private static bool IsSupportedImageExtension(string path) =>
        SupportedImageExtensions.Contains(Path.GetExtension(path) ?? string.Empty);

    // FITS bit depth is self-describing in the header, so 16 is just the historical default used
    // for the load-call argument there. Raw files have no such header, so use the camera profile's
    // configured bit depth instead of hardcoding 16.
    private static int ResolveBitDepth(string path)
    {
        string ext = Path.GetExtension(path) ?? string.Empty;
        if (RawImageExtensions.Contains(ext))
        {
            double profileBitDepth = TouchNStars.Mediators.Profile.ActiveProfile.CameraSettings.BitDepth;
            return profileBitDepth > 0 ? (int)profileBitDepth : 16;
        }
        return 16;
    }

    private int ParseIntParam(string name, int defaultVal, int? max = null)
    {
        string raw = HttpContext.Request.QueryString[name];
        if (string.IsNullOrWhiteSpace(raw) || !int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out int value))
        {
            value = defaultVal;
        }
        if (max.HasValue) value = Math.Min(value, max.Value);
        return value;
    }

    private double ParseDoubleParam(string name, double defaultVal)
    {
        string raw = HttpContext.Request.QueryString[name];
        if (string.IsNullOrWhiteSpace(raw) || !double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out double value))
        {
            return defaultVal;
        }
        return value;
    }

    private bool ParseBoolParam(string name, bool defaultVal)
    {
        string raw = HttpContext.Request.QueryString[name];
        if (string.IsNullOrWhiteSpace(raw)) return defaultVal;
        if (raw == "1") return true;
        if (raw == "0") return false;
        return bool.TryParse(raw, out bool value) ? value : defaultVal;
    }

    private static string HeaderValueToString(IGenericMetaDataHeader header) => header switch
    {
        IGenericMetaDataHeader<string> s => s.Value,
        IGenericMetaDataHeader<double> d => d.Value.ToString(CultureInfo.InvariantCulture),
        IGenericMetaDataHeader<int> i => i.Value.ToString(CultureInfo.InvariantCulture),
        IGenericMetaDataHeader<uint> ui => ui.Value.ToString(CultureInfo.InvariantCulture),
        IGenericMetaDataHeader<bool> b => b.Value ? "T" : "F",
        IGenericMetaDataHeader<DateTime> dt => dt.Value.ToString("o"),
        _ => header?.ToString()
    };

    private static ImageInfo BuildImageInfo(NINA.Image.Interfaces.IImageData imageData)
    {
        var props = imageData.Properties;
        var meta = imageData.MetaData;
        return new ImageInfo
        {
            Success = true,
            IsSupported = true,
            Width = props.Width,
            Height = props.Height,
            BitDepth = props.BitDepth,
            IsBayered = props.IsBayered,
            // The pattern that would actually be used to render, not the profile's override -
            // meta.Camera.BayerPattern is never populated on a file load and so always read "Auto".
            BayerPattern = props.IsBayered ? ImagePreviewService.ResolveBayerPattern(imageData).ToString() : string.Empty,
            FocalLength = double.IsNaN(meta.Telescope.FocalLength) ? null : meta.Telescope.FocalLength,
            PixelSize = double.IsNaN(meta.Camera.PixelSize) ? null : meta.Camera.PixelSize,
            CameraName = meta.Camera.Name,
            TelescopeName = meta.Telescope.Name,
            ExposureStart = meta.Image.ExposureStart == DateTime.MinValue ? null : meta.Image.ExposureStart.ToString("o"),
            ExposureTime = double.IsNaN(meta.Image.ExposureTime) ? null : meta.Image.ExposureTime,
            FilterName = meta.FilterWheel.Filter,
            Headers = meta.GenericHeaders.Select(h => new ImageHeaderEntry
            {
                Key = h.Key,
                Value = HeaderValueToString(h),
                Comment = h.Comment
            }).ToList()
        };
    }

    private static readonly Dictionary<string, string> ContentTypesByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".tif"] = "image/tiff",
            [".tiff"] = "image/tiff",
            [".fit"] = "application/fits",
            [".fits"] = "application/fits",
            [".fts"] = "application/fits",
            [".txt"] = "text/plain",
            [".log"] = "text/plain",
            [".csv"] = "text/csv",
            [".json"] = "application/json",
            [".xml"] = "application/xml"
        };

    // The anonymous responses in this controller already use camelCase field names; the
    // ImageInfo POCO does not, and the client reads camelCase throughout. Without this
    // resolver /filesystem/imageinfo answers { "Success": ... } and every client check
    // against info.success silently fails.
    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    private Task SendJson(object data, int statusCode = 200)
    {
        HttpContext.Response.StatusCode = statusCode;
        string json = JsonConvert.SerializeObject(data, JsonSettings);
        return HttpContext.SendStringAsync(json, "application/json", Encoding.UTF8);
    }

    private static string GetContentType(string path)
    {
        string extension = Path.GetExtension(path);
        return ContentTypesByExtension.TryGetValue(extension ?? string.Empty, out var contentType)
            ? contentType
            : "application/octet-stream";
    }

    // -------------------------------------------------------------------------
    // GET /api/filesystem/browse
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Get, "/filesystem/browse")]
    public async Task Browse()
    {
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            string path = string.IsNullOrWhiteSpace(pathParam)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : Uri.UnescapeDataString(pathParam);

            string fullPath = Path.GetFullPath(path);

            if (!Directory.Exists(fullPath))
            {
                await SendJson(new { success = false, error = "Path does not exist" }, 404);
                return;
            }

            // Optional paging over the folders, then the files, in the order listed: only the page is read from the
            // disk. Without offset/limit everything is listed, as before.
            if (!TryReadPaging(out int offset, out int? limit))
            {
                await SendJson(new { success = false, error = "offset and limit must be non-negative numbers, limit at least 1" }, 400);
                return;
            }

            var directories = new List<object>();
            var files = new List<object>();
            int totalDirectories;
            int totalFiles;

            try
            {
                var directoryPaths = Directory.GetDirectories(fullPath).OrderBy(d => d).ToList();
                var filePaths = Directory.GetFiles(fullPath).OrderBy(f => f).ToList();
                totalDirectories = directoryPaths.Count;
                totalFiles = filePaths.Count;

                int end = limit is { } count ? (int)Math.Min((long)offset + count, (long)totalDirectories + totalFiles) : totalDirectories + totalFiles;
                var pageDirectories = directoryPaths.Skip(offset).Take(Math.Max(0, Math.Min(end, totalDirectories) - offset));
                int fileStart = Math.Max(0, offset - totalDirectories);
                var pageFiles = filePaths.Skip(fileStart).Take(Math.Max(0, end - totalDirectories - fileStart));

                foreach (var dir in pageDirectories)
                {
                    try
                    {
                        var info = new DirectoryInfo(dir);
                        directories.Add(new
                        {
                            name = info.Name,
                            path = info.FullName,
                            lastModified = info.LastWriteTimeUtc.ToString("o")
                        });
                    }
                    catch { /* skip inaccessible entries */ }
                }

                foreach (var file in pageFiles)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        files.Add(new
                        {
                            name = info.Name,
                            path = info.FullName,
                            size = info.Length,
                            lastModified = info.LastWriteTimeUtc.ToString("o")
                        });
                    }
                    catch { /* skip inaccessible entries */ }
                }
            }
            catch (UnauthorizedAccessException)
            {
                await SendJson(new { success = false, error = "Access denied" }, 403);
                return;
            }

            string parentPath = Directory.GetParent(fullPath)?.FullName;

            await SendJson(new
            {
                success = true,
                currentPath = fullPath,
                parentPath,
                directories,
                files,
                totalDirectories,
                totalFiles
            });
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.Browse] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }

    private bool TryReadPaging(out int offset, out int? limit)
    {
        offset = 0;
        limit = null;
        string offsetParam = HttpContext.Request.QueryString["offset"];
        string limitParam = HttpContext.Request.QueryString["limit"];

        if (!string.IsNullOrEmpty(offsetParam)
            && (!int.TryParse(offsetParam, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(limitParam))
        {
            if (!int.TryParse(limitParam, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed < 1)
            {
                return false;
            }

            limit = parsed;
        }

        return true;
    }

    // -------------------------------------------------------------------------
    // POST /api/filesystem/directory  body: { "path": "C:\\some\\new\\dir" }
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Post, "/filesystem/directory")]
    public async Task CreateDirectory()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, string>>();
            if (body == null || !body.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
            {
                await SendJson(new { success = false, error = "Missing 'path' in request body" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(path);

            if (Directory.Exists(fullPath))
            {
                await SendJson(new { success = false, error = "Directory already exists" }, 409);
                return;
            }

            Directory.CreateDirectory(fullPath);
            Logger.Info($"[FilesystemController] Created directory: {fullPath}");

            await SendJson(new { success = true, path = fullPath }, 201);
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.CreateDirectory] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }

    // -------------------------------------------------------------------------
    // DELETE /api/filesystem/directory?path=...
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Delete, "/filesystem/directory")]
    public async Task DeleteDirectory()
    {
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            if (string.IsNullOrWhiteSpace(pathParam))
            {
                await SendJson(new { success = false, error = "Missing 'path' query parameter" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(Uri.UnescapeDataString(pathParam));

            if (!Directory.Exists(fullPath))
            {
                await SendJson(new { success = false, error = "Directory does not exist" }, 404);
                return;
            }

            Directory.Delete(fullPath, recursive: true);
            Logger.Info($"[FilesystemController] Deleted directory: {fullPath}");

            await SendJson(new { success = true, path = fullPath });
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.DeleteDirectory] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }

    // -------------------------------------------------------------------------
    // GET /api/filesystem/file?path=...[&download=1]  — stream raw file content
    //
    // The response is a byte-for-byte copy of the file. Reading it as UTF-8 text
    // would replace every byte >= 0x80 with U+FFFD, which corrupts images and the
    // pixel data of FITS files.
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Get, "/filesystem/file")]
    public async Task ReadFile()
    {
        bool streaming = false;
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            if (string.IsNullOrWhiteSpace(pathParam))
            {
                await SendJson(new { success = false, error = "Missing 'path' query parameter" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(Uri.UnescapeDataString(pathParam));

            if (!File.Exists(fullPath))
            {
                await SendJson(new { success = false, error = "File does not exist" }, 404);
                return;
            }

            // Opened before any header is set: a file that can't be opened is answered with JSON, which went out
            // under the file's Content-Length before and left the client waiting for the rest.
            using var input = new FileStream(
                fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, StreamBufferSize, useAsync: true);
            long length = input.Length;

            string downloadParam = HttpContext.Request.QueryString["download"];
            bool asAttachment = downloadParam == "1" ||
                string.Equals(downloadParam, "true", StringComparison.OrdinalIgnoreCase);

            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = GetContentType(fullPath);
            // Content-Length lets the client show real download progress.
            HttpContext.Response.ContentLength64 = length;
            HttpContext.Response.Headers["Content-Disposition"] =
                $"{(asAttachment ? "attachment" : "inline")}; filename=\"{Path.GetFileName(fullPath)}\"";

            // The connection ends with this response: if the copy fails after the headers, the client sees a closed
            // connection instead of reading the next response on it as the rest of this body.
            HttpContext.Response.KeepAlive = false;

            streaming = true;
            await CopyAnnouncedLengthAsync(input, HttpContext.Response.OutputStream, length).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException) when (!streaming)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex) when (!streaming)
        {
            Logger.Error($"[FilesystemController.ReadFile] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
        catch (Exception ex)
        {
            // The headers are out, so no JSON may follow them. Rethrown, so the connection is closed and the client
            // sees a short body instead of waiting for bytes that never come.
            if (ex is FileShrankException)
            {
                Logger.Warning($"[FilesystemController.ReadFile] {ex.Message}");
            }
            else if (ex is IOException or OperationCanceledException)
            {
                // usually the client leaving (an aborted download, the app going to the background)
                Logger.Debug($"[FilesystemController.ReadFile] stopped while sending: {ex.Message}");
            }
            else
            {
                Logger.Error($"[FilesystemController.ReadFile] failed while sending: {ex.Message}", ex);
            }

            throw;
        }
    }

    private sealed class FileShrankException(string message) : IOException(message);

    // Exactly the length announced in Content-Length: a file that grows meanwhile (the live log) is sent as it was
    // when the response started; more bytes than announced broke the response and the next one on the connection.
    private static async Task CopyAnnouncedLengthAsync(Stream input, Stream output, long length)
    {
        var buffer = new byte[StreamBufferSize];
        long left = length;
        while (left > 0)
        {
            int read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left))).ConfigureAwait(false);
            if (read == 0)
            {
                throw new FileShrankException($"The file ended {left} bytes before the length that was announced (truncated while it was sent)");
            }

            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            left -= read;
        }
    }

    // -------------------------------------------------------------------------
    // GET /api/filesystem/imageinfo?path=...
    //
    // Reports dimensions/bit depth/bayer pattern/header table for a file, and whether it can be
    // rendered by /filesystem/preview at all. A load failure (e.g. missing libraw on PINS) is
    // reported as isSupported:false, not a 500 — that is what lets the frontend fall back to
    // "download to device" instead of showing a broken preview modal.
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Get, "/filesystem/imageinfo")]
    public async Task GetImageInfo()
    {
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            if (string.IsNullOrWhiteSpace(pathParam))
            {
                await SendJson(new ImageInfo { Success = false, Error = "Missing 'path' query parameter" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(Uri.UnescapeDataString(pathParam));
            if (!File.Exists(fullPath))
            {
                await SendJson(new ImageInfo { Success = false, Error = "File does not exist" }, 404);
                return;
            }

            if (!IsSupportedImageExtension(fullPath))
            {
                await SendJson(new ImageInfo { Success = true, IsSupported = false });
                return;
            }

            int bitDepth = ResolveBitDepth(fullPath);
            // RawConverterEnum.FREEIMAGE is a no-op (see ImagePreviewService) but is the only
            // CreateFromFile overload the referenced NINA package version exposes; the load
            // itself does not observe HttpContext.CancellationToken because of that.
            var imageData = await TouchNStars.Mediators.ImageDataFactory
                .CreateFromFile(fullPath, bitDepth, false, RawConverterEnum.FREEIMAGE);

            if (imageData == null)
            {
                await SendJson(new ImageInfo { Success = true, IsSupported = false, Error = "Failed to load image" });
                return;
            }

            // Same correction the preview path applies, so both endpoints agree on IsBayered -
            // this is what decides whether the client offers the debayer option at all.
            imageData = ImagePreviewService.ApplyBayerDetection(imageData);

            await SendJson(BuildImageInfo(imageData));
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new ImageInfo { Success = false, Error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            // Covers e.g. a raw file when libraw.so isn't loadable on PINS — reported as
            // unsupported so the row falls back to download rather than a hard error.
            Logger.Warning($"[FilesystemController.GetImageInfo] {ex.Message}");
            await SendJson(new ImageInfo { Success = true, IsSupported = false, Error = ex.Message });
        }
    }

    // -------------------------------------------------------------------------
    // GET /api/filesystem/preview?path=...&maxWidth=...&quality=...&stretch=...
    //     &blackClipping=...&unlinked=...&debayer=...
    //
    // Renders the file through ImageDataFactory -> RenderImage -> Stretch -> scaled JPEG (or PNG
    // when quality<0), streamed the same way ReadFile() streams raw bytes.
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Get, "/filesystem/preview")]
    public async Task GetPreview()
    {
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            if (string.IsNullOrWhiteSpace(pathParam))
            {
                await SendJson(new { success = false, error = "Missing 'path' query parameter" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(Uri.UnescapeDataString(pathParam));
            if (!File.Exists(fullPath))
            {
                await SendJson(new { success = false, error = "File does not exist" }, 404);
                return;
            }

            if (!IsSupportedImageExtension(fullPath))
            {
                await SendJson(new { success = false, error = "Unsupported file type" }, 400);
                return;
            }

            var profile = TouchNStars.Mediators.Profile.ActiveProfile;
            int maxWidth = ParseIntParam("maxWidth", defaultVal: 2048, max: 2048);
            int quality = ParseIntParam("quality", defaultVal: 85);
            double stretch = ParseDoubleParam("stretch", profile.ImageSettings.AutoStretchFactor);
            double blackClipping = ParseDoubleParam("blackClipping", profile.ImageSettings.BlackClipping);
            bool unlinked = ParseBoolParam("unlinked", profile.ImageSettings.UnlinkedStretch);
            bool debayer = ParseBoolParam("debayer", true);
            int bitDepth = ResolveBitDepth(fullPath);

            var (bytes, contentType) = await previewService.RenderPreviewAsync(
                fullPath, maxWidth, quality, stretch, blackClipping, unlinked, debayer, bitDepth,
                HttpContext.CancellationToken);

            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = contentType;
            HttpContext.Response.ContentLength64 = bytes.Length;
            await HttpContext.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, HttpContext.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Client closed the modal or moved the slider again - nothing to send back.
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.GetPreview] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }

    // -------------------------------------------------------------------------
    // PUT /api/filesystem/rename  body: { "sourcePath": "...", "targetPath": "..." }
    //
    // Handles both files and directories. The target's parent directory has to
    // exist, so this doubles as a move within the existing folder tree.
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Put, "/filesystem/rename")]
    public async Task Rename()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, string>>();

            if (body == null
                || !body.TryGetValue("sourcePath", out var sourcePath) || string.IsNullOrWhiteSpace(sourcePath)
                || !body.TryGetValue("targetPath", out var targetPath) || string.IsNullOrWhiteSpace(targetPath))
            {
                await SendJson(new { success = false, error = "Missing 'sourcePath' or 'targetPath' in request body" }, 400);
                return;
            }

            string fullSource = Path.GetFullPath(sourcePath);
            string fullTarget = Path.GetFullPath(targetPath);

            if (string.Equals(fullSource, fullTarget, StringComparison.Ordinal))
            {
                await SendJson(new { success = true, path = fullTarget });
                return;
            }

            bool sourceIsDirectory = Directory.Exists(fullSource);
            if (!sourceIsDirectory && !File.Exists(fullSource))
            {
                await SendJson(new { success = false, error = "Source does not exist" }, 404);
                return;
            }

            if (sourceIsDirectory && fullTarget.StartsWith(Path.TrimEndingDirectorySeparator(fullSource) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                await SendJson(new { success = false, error = "A folder can't be moved into itself" }, 400);
                return;
            }

            string targetParent = Path.GetDirectoryName(fullTarget);
            if (string.IsNullOrEmpty(targetParent) || !Directory.Exists(targetParent))
            {
                await SendJson(new { success = false, error = "Target directory does not exist" }, 400);
                return;
            }

            if (File.Exists(fullTarget) || Directory.Exists(fullTarget))
            {
                // On vfat/exFAT (USB sticks, SD cards) a rename that only changes the case finds the source under
                // the target's name; on ext4 "Foo" and "foo" are two entries, and the target then really exists.
                if (!IsCaseOnlyRenameOfTheSameEntry(fullSource, fullTarget))
                {
                    await SendJson(new { success = false, error = "Target already exists" }, 409);
                    return;
                }

                RenameCaseOnly(fullSource, fullTarget, sourceIsDirectory);
                Logger.Info($"[FilesystemController] Renamed (case only): {fullSource} -> {fullTarget}");
                await SendJson(new { success = true, path = fullTarget });
                return;
            }

            try
            {
                if (sourceIsDirectory)
                {
                    Directory.Move(fullSource, fullTarget);
                }
                else
                {
                    // across file systems .NET copies the file and deletes the source
                    File.Move(fullSource, fullTarget);
                }
            }
            catch (IOException ex) when (sourceIsDirectory
                && Directory.Exists(fullSource)
                && !Directory.Exists(fullTarget)
                && IsCrossDevice(ex, fullSource, targetParent))
            {
                // a directory can't be renamed onto another file system (EXDEV); nothing was changed
                await SendJson(new { success = false, error = "Moving folders between drives is not supported" }, 409);
                return;
            }

            Logger.Info($"[FilesystemController] Renamed: {fullSource} -> {fullTarget}");

            await SendJson(new { success = true, path = fullTarget });
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.Rename] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }

    // The parent's listing has the source but no entry spelled exactly like the target: the target "exists" only
    // because the file system ignores case.
    private static bool IsCaseOnlyRenameOfTheSameEntry(string fullSource, string fullTarget)
    {
        string parent = Path.GetDirectoryName(fullSource);
        if (!string.Equals(fullSource, fullTarget, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(parent, Path.GetDirectoryName(fullTarget), StringComparison.Ordinal))
        {
            return false;
        }

        string targetName = Path.GetFileName(fullTarget);
        return !Directory.EnumerateFileSystemEntries(parent)
            .Any(entry => string.Equals(Path.GetFileName(entry), targetName, StringComparison.Ordinal));
    }

    // A direct rename that only changes the case is a no-op on vfat/exFAT, so it goes through a temporary name; if
    // the second step fails, the entry gets its old name back.
    private static void RenameCaseOnly(string fullSource, string fullTarget, bool isDirectory)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(fullSource), $".tns-rename-{Guid.NewGuid():N}");
        Move(fullSource, temporary, isDirectory);

        try
        {
            Move(temporary, fullTarget, isDirectory);
        }
        catch (Exception renameError)
        {
            try
            {
                Move(temporary, fullSource, isDirectory);
            }
            catch (Exception rollbackError)
            {
                // the entry would otherwise seem gone: it is a hidden file now (Rename logs this message)
                throw new IOException($"Renaming {fullSource} failed ({renameError.Message}), and so did giving it its name back "
                    + $"({rollbackError.Message}): it is now {temporary}", renameError);
            }

            throw;
        }
    }

    private static void Move(string from, string to, bool isDirectory)
    {
        if (isDirectory)
        {
            Directory.Move(from, to);
        }
        else
        {
            File.Move(from, to);
        }
    }

    // EXDEV (18 on Linux and macOS; .NET carries the errno in HResult) also covers a folder behind a symlink to another
    // drive and btrfs subvolumes. On Unix a move across mounts always fails with EXDEV, so any other error has another
    // cause; only Windows (HRESULTs) falls back to comparing the drives.
    private static bool IsCrossDevice(IOException ex, string fullSource, string targetParent)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ex.HResult == ExdevErrno;
        }

        return !string.Equals(MountRoot(fullSource), MountRoot(targetParent), StringComparison.Ordinal);
    }

    // The mount point a path lies on: the longest mounted root that contains it.
    private static string MountRoot(string path)
    {
        string full = Path.GetFullPath(path);
        string best = Path.GetPathRoot(full) ?? string.Empty;

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                string root = Path.TrimEndingDirectorySeparator(drive.RootDirectory.FullName);
                bool contains = full == root
                    || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || root == string.Empty;
                if (contains && root.Length > best.Length)
                {
                    best = root;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"[FilesystemController] Reading the mounts failed: {ex.Message}");
        }

        return best;
    }

    // -------------------------------------------------------------------------
    // DELETE /api/filesystem/file?path=...
    // -------------------------------------------------------------------------
    [Route(HttpVerbs.Delete, "/filesystem/file")]
    public async Task DeleteFile()
    {
        try
        {
            string pathParam = HttpContext.Request.QueryString["path"];
            if (string.IsNullOrWhiteSpace(pathParam))
            {
                await SendJson(new { success = false, error = "Missing 'path' query parameter" }, 400);
                return;
            }

            string fullPath = Path.GetFullPath(Uri.UnescapeDataString(pathParam));

            if (!File.Exists(fullPath))
            {
                await SendJson(new { success = false, error = "File does not exist" }, 404);
                return;
            }

            File.Delete(fullPath);
            Logger.Info($"[FilesystemController] Deleted file: {fullPath}");

            await SendJson(new { success = true, path = fullPath });
        }
        catch (UnauthorizedAccessException)
        {
            await SendJson(new { success = false, error = "Access denied" }, 403);
        }
        catch (Exception ex)
        {
            Logger.Error($"[FilesystemController.DeleteFile] {ex.Message}", ex);
            await SendJson(new { success = false, error = ex.Message }, 500);
        }
    }
}
