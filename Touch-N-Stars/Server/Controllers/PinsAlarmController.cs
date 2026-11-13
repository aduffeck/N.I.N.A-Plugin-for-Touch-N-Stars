#nullable enable
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using TouchNStars.Server.Models;
using TouchNStars.Server.Services.Alarm;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// The server-side alarm (beep on critical events): settings, test, silence, status and history.
/// Errors are logged as warnings: this controller's lines never raise alarms anyway (TNS controllers are excluded).
/// </summary>
public class PinsAlarmController : WebApiController {
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true, AllowTrailingCommas = true };

    public PinsAlarmController() : this(PinsAlarmService.Instance) {
    }

    /// <summary>For tests: a controller bound to its own service.</summary>
    public PinsAlarmController(PinsAlarmService service) {
        Service = service;
    }

    private PinsAlarmService Service { get; }

    [Route(HttpVerbs.Get, "/pins/alarm")]
    public ApiResponse GetSettings() {
        try {
            Service.Settings.Refresh();
            return Ok(new { Settings = Service.Settings.Current, Stored = Service.Settings.Stored, Defaults = AlarmSettings.Defaults() }, "PinsAlarmSettings");
        } catch (Exception ex) {
            return Fail(500, $"Alarm settings could not be read: {ex.Message}");
        }
    }

    [Route(HttpVerbs.Put, "/pins/alarm")]
    public async Task<ApiResponse> PutSettings() {
        JsonElement patch;
        try {
            patch = JsonDocument.Parse(await HttpContext.GetRequestBodyAsStringAsync()).RootElement.Clone();
        } catch (JsonException ex) {
            return Fail(400, $"Invalid JSON: {ex.Message}");
        }
        try {
            if (!Service.Settings.TryUpdate(patch, out var settings, out var error)) {
                return Fail(400, error);
            }
            return Ok(new { Settings = settings, Stored = true }, "PinsAlarmSettings");
        } catch (Exception ex) {
            Logger.Warning($"Alarm settings could not be saved: {ex.Message}");
            return Fail(500, $"Alarm settings could not be saved: {ex.Message}");
        }
    }

    /// <summary>A test burst with the current settings or the values in the body (not saved).</summary>
    [Route(HttpVerbs.Post, "/pins/alarm/test")]
    public async Task<ApiResponse> Test() {
        if (Service.Engine.ShuttingDown) {
            return Fail(503, "Shutting down");
        }
        var settings = Service.Settings.Current;
        string body = await HttpContext.GetRequestBodyAsStringAsync();
        if (!string.IsNullOrWhiteSpace(body)) {
            JsonElement overrides;
            try {
                overrides = JsonDocument.Parse(body).RootElement.Clone();
            } catch (JsonException ex) {
                return Fail(400, $"Invalid JSON: {ex.Message}");
            }
            if (overrides.ValueKind == JsonValueKind.Object) {
                // Only the sound fields; the rest stays as set
                var sound = JsonSerializer.SerializeToElement(overrides.EnumerateObject()
                    .Where(p => new[] { "volume", "tonems", "pausems", "burstseconds" }.Contains(p.Name.ToLowerInvariant()))
                    .GroupBy(p => p.Name.ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.Last().Value));
                if (!AlarmSettings.TryMerge(settings, sound, out settings, out var error)) {
                    return Fail(400, error);
                }
            }
        }

        var (result, request) = await Service.TestAsync(settings);
        return result switch {
            BeepResult.Ok => Ok(new { Result = result.ToString(), request.Tones, request.Volume }, "PinsAlarmTest"),
            BeepResult.NoPowerBox => Fail(404, "PowerBox device not connected"),
            BeepResult.NotSupported => Fail(400, "Beep method not available on PowerBox device"),
            _ => Fail(500, "PowerBox did not start the tone (no buzzer or communication error, see log)"),
        };
    }

    /// <summary>Stops a running burst and mutes alarms for Minutes (default 30; 0 ends a mute).</summary>
    [Route(HttpVerbs.Post, "/pins/alarm/silence")]
    public async Task<ApiResponse> Silence() {
        int minutes = 30;
        string body = await HttpContext.GetRequestBodyAsStringAsync();
        if (!string.IsNullOrWhiteSpace(body)) {
            try {
                var root = JsonDocument.Parse(body).RootElement;
                var prop = root.ValueKind == JsonValueKind.Object
                    ? root.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, "minutes", StringComparison.OrdinalIgnoreCase))
                    : default;
                if (prop.Value.ValueKind != JsonValueKind.Undefined &&
                    (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out minutes))) {
                    return Fail(400, "Minutes must be a number");
                }
            } catch (JsonException ex) {
                return Fail(400, $"Invalid JSON: {ex.Message}");
            }
        }
        if (minutes < 0 || minutes > 720) {
            return Fail(400, "Minutes must be 0..720");
        }
        var (stopped, mutedUntil) = Service.Silence(minutes);
        return Ok(new { StoppedBurst = stopped, MutedUntilUtc = mutedUntil }, "PinsAlarmSilence");
    }

    [Route(HttpVerbs.Get, "/pins/alarm/status")]
    public ApiResponse Status() {
        try {
            return Ok(Service.GetStatus(), "PinsAlarmStatus");
        } catch (Exception ex) {
            return Fail(500, $"Alarm status could not be read: {ex.Message}");
        }
    }

    [Route(HttpVerbs.Get, "/pins/alarm/history")]
    public ApiResponse History([QueryField] int limit = AlarmEngine.HistorySize) {
        if (limit < 1 || limit > AlarmEngine.HistorySize) {
            return Fail(400, $"limit must be 1..{AlarmEngine.HistorySize}");
        }
        var entries = Service.Engine.History(limit).Select(r => new {
            r.Id,
            r.TimeUtc,
            r.LastTimeUtc,
            r.Count,
            Category = r.Category.ToString(),
            r.Key,
            r.Message,
            Origin = r.Origin.ToString(),
            Outcome = r.Outcome.ToString(),
            r.Reason,
        }).ToList();
        return Ok(new { Entries = entries }, "PinsAlarmHistory");
    }

    private ApiResponse Ok(object response, string type) {
        HttpContext.Response.StatusCode = 200;
        return new ApiResponse { Success = true, Response = response, StatusCode = 200, Type = type };
    }

    private ApiResponse Fail(int status, string error) {
        HttpContext.Response.StatusCode = status;
        return new ApiResponse { Success = false, Error = error, StatusCode = status, Type = "Error" };
    }
}
