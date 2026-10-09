using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using TouchNStars.Server.Services;
using TouchNStars.Server.Models;

namespace TouchNStars.Server.Controllers;

/// <summary>REST API of the internal guider (<see cref="IAdvancedGuider"/>) under /api/internal-guider; see INTERNAL_GUIDER_API.md.</summary>
public partial class InternalGuiderController : WebApiController
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private const int DefaultSteps = 400;
    private const int MaxSteps = 5000;
    private const int DefaultAlerts = 100;
    private const int MaxAlerts = 1000;
    private const int DefaultCropSize = 31;
    // Multi-star guiding tracks up to 9 stars
    private const int MaxSecondaryCrops = 8;

    public InternalGuiderController() : this(InternalGuiderService.Instance)
    {
    }

    /// <summary>For tests: a controller bound to its own service (and thereby its own mediator).</summary>
    public InternalGuiderController(InternalGuiderService service)
    {
        Service = service;
    }

    private InternalGuiderService Service { get; }

    #region Data

    /// <summary>GET /api/internal-guider/status - device summary and guider status; always 200 so it can be polled.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/status")]
    public Task GetStatus()
    {
        try
        {
            Service.EnsureWatching();
            IAdvancedGuider guider = Service.GetConnectedGuider(out string reason);
            InternalGuiderDeviceSummary device = Service.GetDeviceSummary();
            return SendOk(new
            {
                device.Available,
                device.Connected,
                device.DeviceId,
                device.DeviceName,
                device.IsNative,
                reason,
                status = guider?.GetStatus()
            });
        }
        catch (Exception ex)
        {
            return SendException("status", ex);
        }
    }

    /// <summary>GET /api/internal-guider/steps?max=400 - recent guide steps, oldest first.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/steps")]
    public Task GetSteps()
    {
        return WithConnectedGuiderAsync("steps", guider =>
        {
            int max = QueryInt("max", DefaultSteps, 1, MaxSteps);
            return SendOk(guider.GetRecentSteps(max) ?? Array.Empty<AdvancedGuideStep>());
        });
    }

    /// <summary>GET /api/internal-guider/alerts?max=100 - recent alerts/events, oldest first.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/alerts")]
    public Task GetAlerts()
    {
        return WithConnectedGuiderAsync("alerts", guider =>
        {
            int max = QueryInt("max", DefaultAlerts, 1, MaxAlerts);
            return SendOk(guider.GetRecentAlerts(max) ?? Array.Empty<AdvancedGuiderAlert>());
        });
    }

    /// <summary>GET /api/internal-guider/calibration - current calibration or null.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/calibration")]
    public Task GetCalibration()
    {
        return WithConnectedGuiderAsync("calibration", guider => SendOk(guider.GetCalibration()));
    }

    /// <summary>GET /api/internal-guider/settings - all settings with metadata, also before the first connect.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/settings")]
    public Task GetSettings()
    {
        try
        {
            IAdvancedGuider guider = Service.GetConfigurableGuider(out bool connected, out string reason);
            if (guider == null) return SendNotAvailable(reason);
            return SendOk(new
            {
                connected,
                settings = guider.GetSettings() ?? Array.Empty<AdvancedGuiderSetting>()
            });
        }
        catch (Exception ex)
        {
            return SendException("settings", ex);
        }
    }

    /// <summary>POST /api/internal-guider/settings - body { name, value }: change one setting.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/settings")]
    public async Task PostSetting()
    {
        try
        {
            IAdvancedGuider guider = Service.GetConfigurableGuider(out _, out string reason);
            if (guider == null)
            {
                await SendNotAvailable(reason);
                return;
            }

            string body = await HttpContext.GetRequestBodyAsStringAsync();
            if (!InternalGuiderRequest.TryParseSettingBody(body, out string name, out string value, out string parseError))
            {
                await SendError(400, "InvalidRequest", parseError);
                return;
            }

            if (!guider.TrySetSetting(name, value, out string error))
            {
                await SendError(400, "InvalidValue", string.IsNullOrWhiteSpace(error) ? $"The value for '{name}' was rejected." : error);
                return;
            }

            AdvancedGuiderSetting updated = guider.GetSettings()?.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            await SendOk(updated);
        }
        catch (Exception ex)
        {
            await SendException("settings", ex);
        }
    }

    /// <summary>GET /api/internal-guider/image - auto-stretched JPEG of the latest frame (or of a recent frame N).</summary>
    [Route(HttpVerbs.Get, "/internal-guider/image")]
    public async Task GetImage()
    {
        try
        {
            IAdvancedGuider guider = Service.GetConnectedGuider(out string reason);
            if (guider == null)
            {
                await SendNotAvailable(reason);
                return;
            }

            AdvancedGuiderFrame frame = Service.GetFrame(guider, QueryLong("frame"));
            if (frame?.Pixels == null || frame.Width <= 0 || frame.Height <= 0)
            {
                await SendError(404, "NoFrame", "No guide frame yet. Start looping first.");
                return;
            }

            byte[] jpeg = Service.RenderFrameJpeg(frame, RenderOptionsFromQuery());

            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.ContentType = "image/jpeg";
            HttpContext.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            HttpContext.Response.Headers.Add("X-Frame-Number", frame.FrameNumber.ToString(CultureInfo.InvariantCulture));
            HttpContext.Response.Headers.Add("X-Frame-Width", frame.Width.ToString(CultureInfo.InvariantCulture));
            HttpContext.Response.Headers.Add("X-Frame-Height", frame.Height.ToString(CultureInfo.InvariantCulture));
            HttpContext.Response.Headers.Add("Access-Control-Expose-Headers", "X-Frame-Number, X-Frame-Width, X-Frame-Height");
            using Stream stream = HttpContext.OpenResponseStream();
            await stream.WriteAsync(jpeg, 0, jpeg.Length);
        }
        catch (Exception ex)
        {
            await SendException("image", ex);
        }
    }

    /// <summary>GET /api/internal-guider/frame-info - overlay data and star crops of the latest (or a recent) frame.</summary>
    [Route(HttpVerbs.Get, "/internal-guider/frame-info")]
    public Task GetFrameInfo()
    {
        return WithConnectedGuiderAsync("frame-info", guider =>
        {
            AdvancedGuiderFrame frame = Service.GetFrame(guider, QueryLong("frame"));
            if (frame == null)
            {
                return SendError(404, "NoFrame", "No guide frame yet. Start looping first.");
            }
            return SendOk(Service.BuildFrameInfo(frame, QueryInt("cropSize", DefaultCropSize, 0, 128),
                QueryInt("secondaries", 0, 0, MaxSecondaryCrops)));
        });
    }

    #endregion

    #region Actions

    /// <summary>POST /api/internal-guider/loop - start looping exposures.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/loop")]
    public Task Loop() => RunAction("loop", (guider, ct) => guider.StartLooping(ct));

    /// <summary>POST /api/internal-guider/stop - stop guiding (through NINA) and stop all exposures.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/stop")]
    public Task Stop() => RunAction("stop", async (guider, ct) =>
    {
        Service.CancelGuidingStart();
        if (InternalGuiderStates.IsGuiding(guider.State) && Service.Mediator != null)
        {
            await Service.Mediator.StopGuiding(ct);
        }
        return await guider.StopLooping(ct);
    });

    /// <summary>POST /api/internal-guider/start-guiding?calibrate=false - start guiding through NINA; 202, outcome as 'action' message.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/start-guiding")]
    public Task StartGuiding()
    {
        return WithConnectedGuiderAsync("start-guiding", guider =>
        {
            bool calibrate = QueryBool("calibrate", false);
            Service.StartGuidingInBackground(calibrate);
            return SendJson(202, new { success = true, response = new { action = calibrate ? "calibrate" : "start-guiding", accepted = true } });
        });
    }

    /// <summary>POST /api/internal-guider/stop-guiding - stop guiding through NINA's guider mediator.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/stop-guiding")]
    public Task StopGuiding() => RunAction("stop-guiding", async (guider, ct) =>
    {
        Service.CancelGuidingStart();
        return Service.Mediator != null
            ? await Service.Mediator.StopGuiding(ct)
            : await guider.StopGuiding(ct);
    });

    /// <summary>POST /api/internal-guider/pause - pause guiding (exposures continue).</summary>
    [Route(HttpVerbs.Post, "/internal-guider/pause")]
    public Task Pause() => RunAction("pause", (guider, ct) => guider.SetPaused(true, ct));

    /// <summary>POST /api/internal-guider/resume - resume guiding.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/resume")]
    public Task Resume() => RunAction("resume", (guider, ct) => guider.SetPaused(false, ct));

    /// <summary>POST /api/internal-guider/dither?pixels=3&amp;raOnly=false - dither and settle; 202, outcome as 'action' message.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/dither")]
    public Task Dither()
    {
        return WithConnectedGuiderAsync("dither", guider =>
        {
            double? pixels = QueryNullableDouble("pixels");
            if (pixels.HasValue && (pixels.Value <= 0 || pixels.Value > 100))
            {
                return SendError(400, "InvalidRequest", "'pixels' must be between 0 and 100.");
            }
            bool raOnly = QueryBool("raOnly", false);
            if (!Service.TryDitherInBackground(guider, pixels, raOnly))
            {
                return SendError(409, "Rejected", "A dither is already in progress.");
            }
            return SendJson(202, new { success = true, response = new { action = "dither", accepted = true, pixels, raOnly } });
        });
    }

    /// <summary>POST /api/internal-guider/darks/build - body { minExposure, maxExposure, frames }; 202, progress as 'darks' messages.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/darks/build")]
    public Task BuildDarks()
    {
        return WithConnectedGuiderAsync("darks/build", async guider =>
        {
            string body = await HttpContext.GetRequestBodyAsStringAsync();
            if (!InternalGuiderRequest.TryParseDarksBody(body, out double minExposure, out double maxExposure, out int frames, out string parseError))
            {
                await SendError(400, "InvalidRequest", parseError);
                return;
            }
            if (!InternalGuiderStates.IsStopped(guider.State))
            {
                await SendError(409, "Rejected", "Stop looping and guiding before building the dark library.");
                return;
            }
            if (!Service.TryBuildDarksInBackground(guider, minExposure, maxExposure, frames))
            {
                await SendError(409, "Rejected", "A dark library build is already running.");
                return;
            }
            await SendJson(202, new { success = true, response = new { action = "darks", accepted = true, minExposure, maxExposure, frames } });
        });
    }

    /// <summary>POST /api/internal-guider/darks/cancel - cancel a running dark library build.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/darks/cancel")]
    public Task CancelDarks()
    {
        return WithConnectedGuiderAsync("darks/cancel", _ => Service.CancelDarks()
            ? SendOk(new { action = "darks-cancel" })
            : SendError(409, "Rejected", "No dark library build is running."));
    }

    /// <summary>POST /api/internal-guider/clear-calibration - forget the current calibration.</summary>
    [Route(HttpVerbs.Post, "/internal-guider/clear-calibration")]
    public Task ClearCalibration() => RunAction("clear-calibration", async (guider, ct) =>
    {
        if (Service.Mediator != null)
        {
            return await Service.Mediator.ClearCalibration(ct);
        }
        return await guider.ClearCalibration(ct);
    });

    /// <summary>
    /// Runs a short guider action: 200 { action, state } when it succeeded in time, 202 { action, accepted, pending }
    /// when it still runs (its outcome follows as an 'action' message), 409 when the guider rejected it.
    /// </summary>
    private Task RunAction(string name, Func<IAdvancedGuider, CancellationToken, Task<bool>> action)
    {
        return WithConnectedGuiderAsync(name, async guider =>
        {
            string Rejected() => $"The guider rejected '{name}' in state {guider.State ?? "unknown"}.";
            GuiderCallOutcome outcome = await Service.RunGuiderCallAsync(name, ct => action(guider, ct), Rejected);
            if (outcome.State == GuiderCallState.Pending)
            {
                await SendJson(202, new { success = true, response = new { action = name, accepted = true, pending = true } });
                return;
            }
            if (!outcome.Success)
            {
                await SendFailure(outcome, Rejected);
                return;
            }
            await SendOk(new { action = name, state = guider.State });
        });
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Answers a <see cref="GuiderCallOutcome"/>: 200 { action, accepted, pending: false, status, [extra] },
    /// 202 with pending: true while the call still runs, 409 Rejected/Cancelled, 500.
    /// </summary>
    private Task SendCallOutcome(string action, GuiderCallOutcome outcome, Func<object> status, string busyMessage,
        Func<string> failureMessage, string extraName = null, object extraValue = null)
    {
        switch (outcome.State)
        {
            case GuiderCallState.Busy:
                return SendError(409, "Rejected", busyMessage ?? $"'{action}' is already running.");
            case GuiderCallState.Pending:
                return SendJson(202, new { success = true, response = CallResponse(action, true, status(), extraName, extraValue) });
        }
        if (!outcome.Success) return SendFailure(outcome, failureMessage);
        return SendOk(CallResponse(action, false, status(), extraName, extraValue));
    }

    /// <summary>Answers a completed call that did not succeed: 500, 409 Cancelled or 409 Rejected.</summary>
    private Task SendFailure(GuiderCallOutcome outcome, Func<string> rejectedMessage)
    {
        if (outcome.Faulted) return SendError(500, "Error", outcome.Error);
        if (outcome.Cancelled) return SendError(409, "Cancelled", outcome.Error);
        return SendError(409, "Rejected", rejectedMessage());
    }

    private static Dictionary<string, object> CallResponse(string action, bool pending, object status, string extraName, object extraValue)
    {
        var response = new Dictionary<string, object>
        {
            ["action"] = action,
            ["accepted"] = true,
            ["pending"] = pending,
            ["status"] = status
        };
        if (extraName != null) response[extraName] = extraValue;
        return response;
    }

    private Task WithConnectedGuiderAsync(string what, Func<IAdvancedGuider, Task> handler)
    {
        return WithGuiderAsync(what, true, handler);
    }

    /// <summary>
    /// Runs <paramref name="handler"/> with the connected internal guider or, unless <paramref name="requireConnected"/>,
    /// the guider chooser's internal guider while it is not connected. 409 NotAvailable without one, 500 on errors.
    /// </summary>
    private async Task WithGuiderAsync(string what, bool requireConnected, Func<IAdvancedGuider, Task> handler)
    {
        try
        {
            string reason;
            IAdvancedGuider guider = requireConnected
                ? Service.GetConnectedGuider(out reason)
                : Service.GetConfigurableGuider(out _, out reason);
            if (guider == null)
            {
                await SendNotAvailable(reason);
                return;
            }
            Service.EnsureWatching();
            await handler(guider);
        }
        catch (Exception ex)
        {
            await SendException(what, ex);
        }
    }

    private Task SendOk(object response) => SendJson(200, new { success = true, response });

    private Task SendNotAvailable(string reason)
        => SendError(409, "NotAvailable", reason ?? "The Internal Guider is not connected.");

    /// <summary>501 for an optional part of the contract (<see cref="IGuidingCoach"/>, <see cref="IGuideIncidentRecorder"/>) the guider lacks.</summary>
    private Task SendNotSupported(string function)
        => SendError(501, "NotSupported", $"The connected guider does not support {function}.");

    private Task SendError(int status, string code, string error)
        => SendJson(status, new { success = false, error, code });

    /// <summary>An error with the guider's stable message code and its parameters for localisation (e.g. coach.busy).</summary>
    private Task SendError(int status, string code, string error, string messageCode, Dictionary<string, object> messageParameters)
        => messageCode == null
            ? SendError(status, code, error)
            : SendJson(status, new { success = false, error, code, messageCode, messageParameters = messageParameters ?? new Dictionary<string, object>() });

    private Task SendException(string what, Exception ex)
    {
        Logger.Error($"InternalGuider: {what} failed: {ex}");
        return SendError(500, "Error", ex.Message);
    }

    private Task SendJson(int statusCode, object data)
    {
        HttpContext.Response.StatusCode = statusCode;
        HttpContext.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
        return HttpContext.SendStringAsync(InternalGuiderJson.Serialize(data), "application/json", Utf8NoBom);
    }

    /// <summary>The JPEG options of /image and /incidents/{id}/image: maxWidth, stretch, gamma and quality.</summary>
    private GuideFrameRenderer.RenderOptions RenderOptionsFromQuery()
    {
        return new GuideFrameRenderer.RenderOptions
        {
            MaxWidth = QueryInt("maxWidth", GuideFrameRenderer.DefaultMaxWidth, 0, 8192),
            TargetBackground = QueryDouble("stretch", GuideFrameRenderer.DefaultTargetBackground, 0.01, 0.9),
            Gamma = QueryDouble("gamma", 1.0, 0.1, 5.0),
            Quality = QueryInt("quality", GuideFrameRenderer.DefaultQuality, 10, 100)
        };
    }

    private string Query(string name) => HttpContext.Request.QueryString[name];

    private int QueryInt(string name, int fallback, int min, int max)
    {
        return int.TryParse(Query(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    private long? QueryLong(string name)
    {
        return long.TryParse(Query(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;
    }

    private double QueryDouble(string name, double fallback, double min, double max)
    {
        double? value = QueryNullableDouble(name);
        return value.HasValue ? Math.Clamp(value.Value, min, max) : fallback;
    }

    private double? QueryNullableDouble(string name)
    {
        return double.TryParse(Query(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value
            : null;
    }

    private bool QueryBool(string name, bool fallback)
    {
        string raw = Query(name);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    #endregion
}
