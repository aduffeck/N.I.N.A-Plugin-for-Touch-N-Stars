using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.SDK.TelescopeSDKs.OnStepXSDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using TouchNStars.Server.Models;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Settings of pins' native OnStepX mount driver that live in the controller itself: altitude and meridian limits, the
/// preferred pier side, set home, and the guide rate and firmware for display; for a Proxisky UMi also the vendor
/// settings under /onstepx/proxisky. Every route answers 404 unless NINA's mount is the native OnStepX driver and
/// connected (the Proxisky routes: and a UMi); a value the controller refuses answers 400 with its reason.
/// </summary>
public class OnStepXController : WebApiController
{
    /// <summary>GET /api/onstepx/settings - The controller's settings (OnStepXMountSettings).</summary>
    [Route(HttpVerbs.Get, "/onstepx/settings")]
    public async Task<ApiResponse> GetSettings()
    {
        if (INDIController.NativeOnStepXMount() is not { } mount)
        {
            return NotConnected();
        }
        return await Run(() => Ok(mount.GetMountSettings(), "OnStepXSettings"), "reading the OnStepX settings");
    }

    /// <summary>
    /// POST /api/onstepx/altitude-limits - Body { "min": -10, "max": 85 }: the lowest (-30 to 30) and highest (60 to
    /// 90) altitude in whole degrees, stored by the controller.
    /// </summary>
    [Route(HttpVerbs.Post, "/onstepx/altitude-limits")]
    public async Task<ApiResponse> SetAltitudeLimits()
    {
        if (INDIController.NativeOnStepXMount() is not { } mount)
        {
            return NotConnected();
        }
        var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
        if (!TryGetInt(body, "min", out int min) || !TryGetInt(body, "max", out int max))
        {
            return Error("Body must contain integer 'min' and 'max' (degrees)", 400);
        }
        return await Run(() =>
        {
            mount.SetAltitudeLimits(min, max);
            return Ok(new { min, max }, "OnStepXAltitudeLimits");
        }, "setting the OnStepX altitude limits");
    }

    /// <summary>
    /// POST /api/onstepx/meridian-limits - Body { "east": 15, "west": 10 }: degrees past the meridian the mount may
    /// track on the east and on the west side of the pier (-360 to 360, rounded to 0.25°), stored by the controller.
    /// </summary>
    [Route(HttpVerbs.Post, "/onstepx/meridian-limits")]
    public async Task<ApiResponse> SetMeridianLimits()
    {
        if (INDIController.NativeOnStepXMount() is not { } mount)
        {
            return NotConnected();
        }
        var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
        if (!TryGetDouble(body, "east", out double east) || !TryGetDouble(body, "west", out double west))
        {
            return Error("Body must contain numeric 'east' and 'west' (degrees)", 400);
        }
        return await Run(() =>
        {
            mount.SetMeridianLimits(east, west);
            return Ok(new { east, west }, "OnStepXMeridianLimits");
        }, "setting the OnStepX meridian limits");
    }

    /// <summary>
    /// POST /api/onstepx/preferred-pier-side - Body { "side": "East" | "West" | "Best" | "" }: the profile setting
    /// TelescopeSettings.PreferredPierSide, which the driver sets on the mount at once and at every connect; "" leaves
    /// the mount's own.
    /// </summary>
    [Route(HttpVerbs.Post, "/onstepx/preferred-pier-side")]
    public async Task<ApiResponse> SetPreferredPierSide()
    {
        if (INDIController.NativeOnStepXMount() is null)
        {
            return NotConnected();
        }
        var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
        string side = body != null && body.TryGetValue("side", out var s) ? s?.ToString()?.Trim() ?? string.Empty : null;
        if (side == null || (side.Length > 0 && side != "East" && side != "West" && side != "Best"))
        {
            return Error("Body must contain 'side': \"East\", \"West\", \"Best\" or \"\"", 400);
        }
        var settings = TouchNStars.Mediators?.Profile?.ActiveProfile?.TelescopeSettings;
        if (settings == null)
        {
            return Error("No active profile", 500);
        }
        return await Run(() =>
        {
            settings.PreferredPierSide = side;
            return Ok(new { side }, "OnStepXPreferredPierSide");
        }, "setting the preferred pier side");
    }

    /// <summary>
    /// POST /api/onstepx/set-home - Makes the current position the home position (:hF#). The mount must point at the
    /// pole (a GEM with the counterweights down) and stand still; the controller stops tracking and clears the park
    /// state, the alignment and custom tracking rates.
    /// </summary>
    [Route(HttpVerbs.Post, "/onstepx/set-home")]
    public async Task<ApiResponse> SetHome()
    {
        if (INDIController.NativeOnStepXMount() is not { } mount)
        {
            return NotConnected();
        }
        return await Run(() =>
        {
            mount.SetHome();
            return Ok("Home set", "OnStepXSetHome");
        }, "setting home");
    }

    /// <summary>
    /// GET /api/onstepx/proxisky - The vendor settings of a Proxisky UMi (ProxiskySettings), read from the mount on every
    /// call (about 20 commands, half a second). A null value is a feature this mount does not have. CanSetDecLimits,
    /// CanSetDecSecondHome and CanSetPid tell what the model allows changing; RestartPending names the settings written
    /// that take effect at the next power cycle (StatusLed, AutoTracking, RaPid, DecPid).
    /// </summary>
    [Route(HttpVerbs.Get, "/onstepx/proxisky")]
    public async Task<ApiResponse> GetProxiskySettings()
    {
        if (ProxiskyMount() is not { } mount)
        {
            return NoProxisky();
        }
        return await Run(() => Ok(mount.GetProxiskySettings(), "ProxiskySettings"), "reading the Proxisky settings");
    }

    /// <summary>
    /// POST /api/onstepx/proxisky/switch - Body { "setting": "StatusLed", "enabled": true }: one of StatusLed,
    /// PowerLossMemory, AsiairHoming, SupervisedHome, DecSecondHome, AutoTracking, RaLimits, DecLimits, AntiCollision,
    /// SupervisedGoto. A setting the model does not take answers 400.
    /// </summary>
    [Route(HttpVerbs.Post, "/onstepx/proxisky/switch")]
    public async Task<ApiResponse> SetProxiskySwitch()
    {
        if (ProxiskyMount() is not { } mount)
        {
            return NoProxisky();
        }
        var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
        string name = body != null && body.TryGetValue("setting", out var s) ? s?.ToString() : null;
        if (!TryParseName(name, out ProxiskySwitch setting) || !TryGetBool(body, "enabled", out bool enabled))
        {
            return Error($"Body must contain 'setting' ({string.Join(", ", Enum.GetNames<ProxiskySwitch>())}) and boolean 'enabled'", 400);
        }
        return await Run(() =>
        {
            mount.SetProxiskySwitch(setting, enabled);
            return Ok(new { setting = setting.ToString(), enabled }, "ProxiskySwitch");
        }, $"switching {setting}");
    }

    /// <summary>
    /// POST /api/onstepx/proxisky/ra-limits - Body { "left": 95, "right": 95 }: the RA travel limits in whole degrees on
    /// the vendor's "left" and "right" side, 1 to 180.
    /// </summary>
    [Route(HttpVerbs.Post, "/onstepx/proxisky/ra-limits")]
    public async Task<ApiResponse> SetProxiskyRaLimits()
    {
        if (ProxiskyMount() is not { } mount)
        {
            return NoProxisky();
        }
        var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
        if (!TryGetInt(body, "left", out int left) || !TryGetInt(body, "right", out int right))
        {
            return Error("Body must contain integer 'left' and 'right' (degrees)", 400);
        }
        return await Run(() =>
        {
            mount.SetProxiskyRaLimits(left, right);
            return Ok(new { left, right }, "ProxiskyRaLimits");
        }, "setting the Proxisky RA limits");
    }

    /// <summary>
    /// POST /api/onstepx/proxisky/dec-limits - Body { "ccw": 120, "cw": 120 }: the Dec travel limits in whole degrees
    /// counter-clockwise and clockwise, 90 to 200. UMi17X only (CanSetDecLimits).
    /// </summary>
    [Route(HttpVerbs.Post, "/onstepx/proxisky/dec-limits")]
    public async Task<ApiResponse> SetProxiskyDecLimits()
    {
        if (ProxiskyMount() is not { } mount)
        {
            return NoProxisky();
        }
        var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
        if (!TryGetInt(body, "ccw", out int ccw) || !TryGetInt(body, "cw", out int cw))
        {
            return Error("Body must contain integer 'ccw' and 'cw' (degrees)", 400);
        }
        return await Run(() =>
        {
            mount.SetProxiskyDecLimits(ccw, cw);
            return Ok(new { ccw, cw }, "ProxiskyDecLimits");
        }, "setting the Proxisky Dec limits");
    }

    /// <summary>
    /// POST /api/onstepx/proxisky/acs-thresholds - Body { "ra": 1000, "dec": 1000 }: the anti-collision thresholds (the
    /// vendor's "sensitivity"), RA 10 and Dec 100 to 59999.
    /// </summary>
    [Route(HttpVerbs.Post, "/onstepx/proxisky/acs-thresholds")]
    public async Task<ApiResponse> SetProxiskyAcsThresholds()
    {
        if (ProxiskyMount() is not { } mount)
        {
            return NoProxisky();
        }
        var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
        if (!TryGetInt(body, "ra", out int ra) || !TryGetInt(body, "dec", out int dec))
        {
            return Error("Body must contain integer 'ra' and 'dec'", 400);
        }
        return await Run(() =>
        {
            mount.SetProxiskyAcsThresholds(ra, dec);
            return Ok(new { ra, dec }, "ProxiskyAcsThresholds");
        }, "setting the Proxisky collision thresholds");
    }

    /// <summary>POST /api/onstepx/proxisky/acs-clear - Sets both collision counters to 0.</summary>
    [Route(HttpVerbs.Post, "/onstepx/proxisky/acs-clear")]
    public async Task<ApiResponse> ClearProxiskyCollisionCounters()
    {
        if (ProxiskyMount() is not { } mount)
        {
            return NoProxisky();
        }
        return await Run(() =>
        {
            mount.ClearProxiskyCollisionCounters();
            return Ok("Collision counters cleared", "ProxiskyAcsClear");
        }, "clearing the Proxisky collision counters");
    }

    /// <summary>POST /api/onstepx/proxisky/supervised-goto-tolerance - Body { "degrees": 10 }: 5 to 30.</summary>
    [Route(HttpVerbs.Post, "/onstepx/proxisky/supervised-goto-tolerance")]
    public async Task<ApiResponse> SetProxiskySupervisedGotoTolerance()
    {
        if (ProxiskyMount() is not { } mount)
        {
            return NoProxisky();
        }
        var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
        if (!TryGetInt(body, "degrees", out int degrees))
        {
            return Error("Body must contain integer 'degrees'", 400);
        }
        return await Run(() =>
        {
            mount.SetProxiskySupervisedGotoTolerance(degrees);
            return Ok(new { degrees }, "ProxiskySupervisedGotoTolerance");
        }, "setting the Proxisky supervised goto tolerance");
    }

    /// <summary>
    /// POST /api/onstepx/proxisky/pid - Body { "axis": "Ra" | "Dec", "angleKp": 300, "angleKi": 200, "speedKp": 200,
    /// "speedKi": 50 }: the servo gains of one axis, 11 to 1999, speed Ki 11 to 80. UMi20S and UMi17X only (CanSetPid);
    /// refused while the mount moves or tracks, since the commit restarts the axis. Takes effect at the next power cycle.
    /// </summary>
    [Route(HttpVerbs.Post, "/onstepx/proxisky/pid")]
    public async Task<ApiResponse> SetProxiskyPid()
    {
        if (ProxiskyMount() is not { } mount)
        {
            return NoProxisky();
        }
        var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
        string axisName = body != null && body.TryGetValue("axis", out var a) ? a?.ToString() : null;
        if (!TryParseName(axisName, out ProxiskyAxis axis)
            || !TryGetInt(body, "angleKp", out int angleKp) || !TryGetInt(body, "angleKi", out int angleKi)
            || !TryGetInt(body, "speedKp", out int speedKp) || !TryGetInt(body, "speedKi", out int speedKi))
        {
            return Error("Body must contain 'axis' (\"Ra\" or \"Dec\") and integer 'angleKp', 'angleKi', 'speedKp', 'speedKi'", 400);
        }
        var gains = new ProxiskyPidGains(angleKp, angleKi, speedKp, speedKi);
        return await Run(() =>
        {
            mount.SetProxiskyPidGains(axis, gains);
            return Ok(new { axis = axis.ToString(), gains }, "ProxiskyPid");
        }, $"setting the Proxisky {axis} PID gains");
    }

    private static OnStepXTelescope ProxiskyMount()
        => INDIController.NativeOnStepXMount() is { IsProxisky: true } mount ? mount : null;

    private ApiResponse NoProxisky() => Error("No Proxisky UMi is connected with the native OnStepX driver", 404);

    private async Task<ApiResponse> Run(Func<ApiResponse> action, string what)
    {
        try
        {
            // serial commands block for a few hundred ms
            return await Task.Run(action);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Error(ex.Message, 400);
        }
        catch (Exception ex)
        {
            Logger.Error($"Error {what}: {ex}");
            return Error($"An unexpected error occurred while {what}");
        }
    }

    private static bool TryGetInt(Dictionary<string, object> body, string key, out int value)
    {
        value = 0;
        return body != null && body.TryGetValue(key, out var raw)
            && int.TryParse(raw?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A name of <typeparamref name="T"/>, any case; unlike Enum.TryParse no numbers or comma lists.</summary>
    private static bool TryParseName<T>(string name, out T value) where T : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(candidate.ToString(), name, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool TryGetBool(Dictionary<string, object> body, string key, out bool value)
    {
        value = false;
        return body != null && body.TryGetValue(key, out var raw) && bool.TryParse(raw?.ToString(), out value);
    }

    private static bool TryGetDouble(Dictionary<string, object> body, string key, out double value)
    {
        value = 0;
        return body != null && body.TryGetValue(key, out var raw)
            && double.TryParse(raw?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private ApiResponse Ok(object response, string type)
    {
        HttpContext.Response.StatusCode = 200;
        return new ApiResponse { Success = true, Response = response, StatusCode = 200, Type = type };
    }

    private ApiResponse NotConnected() => Error("No native OnStepX mount is connected", 404);

    private ApiResponse Error(string error, int statusCode = 500)
    {
        HttpContext.Response.StatusCode = statusCode;
        return new ApiResponse { Success = false, Error = error, StatusCode = statusCode, Type = "Error" };
    }
}
