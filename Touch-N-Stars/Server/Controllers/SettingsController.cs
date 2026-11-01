using TouchNStars.Server.Models;
using TouchNStars.Utility;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for TNS settings management
/// </summary>
public class SettingsController : WebApiController
{
    private static readonly string SettingsFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NINA", "TnsCache", "settings.json"
    );

    private static readonly JsonSerializerOptions ReadOptions = new() { AllowTrailingCommas = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Guards every read-modify-write of the file and the cache below.
    private static readonly object _fileLock = new();

    // Cache for the static read path (see TryGetRawSetting). Guarded by _fileLock.
    private static Dictionary<string, string> _fileCache;
    private static DateTime _fileCacheStampUtc;
    private static long _fileCacheLength;

    /// <summary>
    /// Reads a raw setting value without going through the HTTP layer, for code that
    /// runs outside a request (e.g. FlatTargetNameService on the image save pipeline).
    /// The parsed file is cached and re-read only when its timestamp or length changes.
    /// That check cannot see an out-of-process edit that leaves the length untouched
    /// within the filesystem's timestamp granularity; the in-process writers below
    /// invalidate the cache explicitly, so this only affects external editing.
    /// </summary>
    public static bool TryGetRawSetting(string key, out string value)
    {
        value = null;
        if (string.IsNullOrEmpty(key)) return false;

        lock (_fileLock)
        {
            try
            {
                var (stamp, length) = FileStamp();
                if (_fileCache == null || stamp != _fileCacheStampUtc || length != _fileCacheLength)
                {
                    // Load moves an unreadable file aside and may restore the backup, so the
                    // stamp is taken afterwards: a file that stays unreadable or missing is
                    // then not read (and warned about) again for every saved image.
                    _fileCache = LoadLocked();
                    (_fileCacheStampUtc, _fileCacheLength) = FileStamp();
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to read the settings file: {ex.Message}");
                _fileCache = null;
                return false;
            }

            return _fileCache.TryGetValue(key, out value);
        }
    }

    private static (DateTime Stamp, long Length) FileStamp()
    {
        var info = new FileInfo(SettingsFilePath);
        return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, -1L);
    }

    // Callers hold _fileLock.
    private static Dictionary<string, string> LoadLocked()
    {
        return JsonFileStore.Read(
            SettingsFilePath,
            json => JsonSerializer.Deserialize<Dictionary<string, string>>(json, ReadOptions) ?? new Dictionary<string, string>(),
            () => new Dictionary<string, string>(),
            "settings");
    }

    // Callers hold _fileLock.
    private static void SaveLocked(Dictionary<string, string> settings)
    {
        JsonFileStore.Write(SettingsFilePath, JsonSerializer.Serialize(settings, WriteOptions));
        _fileCache = null;
    }

    /// <summary>
    /// POST /api/settings - Save or create a setting
    /// </summary>
    [Route(HttpVerbs.Post, "/settings")]
    public async Task<ApiResponse> SaveSetting()
    {
        try
        {
            var setting = await HttpContext.GetRequestDataAsync<Setting>();

            if (string.IsNullOrEmpty(setting.Key))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Key ist erforderlich",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            lock (_fileLock)
            {
                var currentSettings = LoadLocked();
                currentSettings[setting.Key] = setting.Value;
                SaveLocked(currentSettings);
            }

            return new ApiResponse
            {
                Success = true,
                Response = setting,
                StatusCode = 200,
                Type = "Setting"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/settings - Get all settings
    /// </summary>
    [Route(HttpVerbs.Get, "/settings")]
    public Task<Dictionary<string, string>> GetAllSettings()
    {
        try
        {
            lock (_fileLock)
            {
                return Task.FromResult(LoadLocked());
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return Task.FromResult(new Dictionary<string, string>());
        }
    }

    /// <summary>
    /// GET /api/settings/{key} - Get a specific setting by key
    /// </summary>
    [Route(HttpVerbs.Get, "/settings/{key}")]
    public Task<ApiResponse> GetSetting(string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key))
            {
                HttpContext.Response.StatusCode = 400;
                return Task.FromResult(new ApiResponse
                {
                    Success = false,
                    Error = "Key ist erforderlich",
                    StatusCode = 400,
                    Type = "Error"
                });
            }

            Dictionary<string, string> settings;
            lock (_fileLock)
            {
                settings = LoadLocked();
            }

            if (!settings.TryGetValue(key, out var value))
            {
                return Task.FromResult(new ApiResponse
                {
                    Success = false,
                    Error = "Einstellung nicht gefunden",
                    StatusCode = 404,
                    Type = "NotFound"
                });
            }

            return Task.FromResult(new ApiResponse
            {
                Success = true,
                Response = new Setting { Key = key, Value = value },
                StatusCode = 200,
                Type = "Setting"
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return Task.FromResult(new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            });
        }
    }

    /// <summary>
    /// DELETE /api/settings/{key} - Delete a setting
    /// </summary>
    [Route(HttpVerbs.Delete, "/settings/{key}")]
    public Task<ApiResponse> DeleteSetting(string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key))
            {
                HttpContext.Response.StatusCode = 400;
                return Task.FromResult(new ApiResponse
                {
                    Success = false,
                    Error = "Key ist erforderlich",
                    StatusCode = 400,
                    Type = "Error"
                });
            }

            lock (_fileLock)
            {
                var settings = LoadLocked();

                if (!File.Exists(SettingsFilePath))
                {
                    return Task.FromResult(new ApiResponse
                    {
                        Success = false,
                        Error = "Keine Einstellungen vorhanden",
                        StatusCode = 404,
                        Type = "NotFound"
                    });
                }

                if (!settings.Remove(key))
                {
                    return Task.FromResult(new ApiResponse
                    {
                        Success = false,
                        Error = "Einstellung nicht gefunden",
                        StatusCode = 404,
                        Type = "NotFound"
                    });
                }

                SaveLocked(settings);
            }

            return Task.FromResult(new ApiResponse
            {
                Success = true,
                Response = $"Einstellung '{key}' gelöscht",
                StatusCode = 200,
                Type = "Setting"
            });
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return Task.FromResult(new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            });
        }
    }

    /// <summary>
    /// PUT /api/settings/{key} - Update an existing setting
    /// </summary>
    [Route(HttpVerbs.Put, "/settings/{key}")]
    public async Task<ApiResponse> UpdateSetting(string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key))
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Key ist erforderlich",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var updatedSetting = await HttpContext.GetRequestDataAsync<Setting>();

            lock (_fileLock)
            {
                var settings = LoadLocked();

                if (!File.Exists(SettingsFilePath))
                {
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Keine Einstellungen vorhanden",
                        StatusCode = 404,
                        Type = "NotFound"
                    };
                }

                if (!settings.ContainsKey(key))
                {
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Einstellung nicht gefunden",
                        StatusCode = 404,
                        Type = "NotFound"
                    };
                }

                settings[key] = updatedSetting.Value;
                SaveLocked(settings);
            }

            return new ApiResponse
            {
                Success = true,
                Response = new Setting { Key = key, Value = updatedSetting.Value },
                StatusCode = 200,
                Type = "Setting"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }
}
