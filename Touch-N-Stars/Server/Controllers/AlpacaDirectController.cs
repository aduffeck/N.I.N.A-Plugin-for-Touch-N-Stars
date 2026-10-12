using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using NINA.Profile;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TouchNStars.Server.Models;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for configuring AlpacaDirect (static-IP Alpaca) device settings.
/// AlpacaDirect devices bypass UDP discovery and connect directly to a known host,
/// which is required when the Alpaca device is on a different subnet.
///
/// Settings are stored via NINA's PluginOptionsAccessor, keyed by the per-device GUID
/// that each AlpacaDirect* class uses as its plugin options namespace.
///
/// </summary>
public class AlpacaDirectController : WebApiController
{
    // Maps device type names to the GUID each AlpacaDirect* class uses
    // as its PluginOptionsAccessor namespace.
    private static readonly Dictionary<string, Guid> DeviceGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        ["camera"] = Guid.Parse("01E42001-1A8B-44AA-AD7E-CE8F5250F1F4"),
        // PINS' guide camera slot keeps its own static IP (NINA.Equipment AlpacaInteraction.GuideAlpacaDirectCameraId).
        ["guidecamera"] = Guid.Parse("6C9035BF-9F7E-4FA2-8743-8D29026BCC5D"),
        ["telescope"] = Guid.Parse("F98BB2D7-A53B-456C-A3B0-CC1CC0F3A40E"),
        ["focuser"] = Guid.Parse("75ABC27F-85F6-4993-B42C-C66E1F5726E3"),
        ["filterwheel"] = Guid.Parse("2F95FB1C-46ED-4F2C-8072-273F07567DD7"),
        ["dome"] = Guid.Parse("C09F1563-4B7A-4300-B460-058ACD7952ED"),
        ["rotator"] = Guid.Parse("7F937C44-9ECE-49A7-B56E-8090FF8267A8"),
        ["safetymonitor"] = Guid.Parse("C4DB7AFB-8092-45F9-9CB1-AB7992977234"),
        ["switch"] = Guid.Parse("F889ED92-DC36-4A8F-A24E-788E3AD3E780"),
        ["flatdevice"] = Guid.Parse("DE702D3C-8D66-462A-A4AB-19FA5A0C7E84"),
        ["weather"] = Guid.Parse("6BD8BCE9-C199-401A-AAF8-47EA8EE5AE32"),
    };

    /// <summary>
    /// GET /api/alpaca-direct/{deviceType}/settings
    /// Returns the current AlpacaDirect settings for the specified device type.
    /// deviceType: camera | guidecamera | telescope | focuser | filterwheel | dome | rotator | safetymonitor | switch | flatdevice | weather
    /// </summary>
    [Route(HttpVerbs.Get, "/alpaca-direct/{deviceType}/settings")]
    public ApiResponse GetSettings(string deviceType)
    {
        try
        {
            if (!DeviceGuids.TryGetValue(deviceType, out Guid guid))
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Unknown device type '{deviceType}'. Valid types: {string.Join(", ", DeviceGuids.Keys)}",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var accessor = new PluginOptionsAccessor(TouchNStars.Mediators.Profile, guid);

            return new ApiResponse
            {
                Success = true,
                StatusCode = 200,
                Type = "AlpacaDirectSettings",
                Response = new
                {
                    DeviceType = deviceType.ToLowerInvariant(),
                    IpAddress = accessor.GetValueString("IpAddress", "127.0.0.1"),
                    Port = accessor.GetValueInt32("Port", 5000),
                    DeviceNumber = accessor.GetValueInt32("DeviceNumber", 0),
                    ServiceType = accessor.GetValueString("ServiceType", "Http"),
                }
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
    /// PUT /api/alpaca-direct/{deviceType}/settings
    /// Updates the AlpacaDirect settings for the specified device type.
    /// Body (JSON, all fields optional):
    /// {
    ///   "IpAddress": "192.168.2.100",
    ///   "Port": 11111,
    ///   "DeviceNumber": 0,
    ///   "ServiceType": "Http"   // "Http" or "Https"
    /// }
    /// </summary>
    [Route(HttpVerbs.Put, "/alpaca-direct/{deviceType}/settings")]
    public async Task<ApiResponse> UpdateSettings(string deviceType)
    {
        try
        {
            if (!DeviceGuids.TryGetValue(deviceType, out Guid guid))
            {
                HttpContext.Response.StatusCode = 404;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Unknown device type '{deviceType}'. Valid types: {string.Join(", ", DeviceGuids.Keys)}",
                    StatusCode = 404,
                    Type = "Error"
                };
            }

            var body = await HttpContext.GetRequestDataAsync<AlpacaDirectSettingsRequest>();

            if (body == null)
            {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse
                {
                    Success = false,
                    Error = "Request body is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            var accessor = new PluginOptionsAccessor(TouchNStars.Mediators.Profile, guid);

            if (body.IpAddress != null)
            {
                accessor.SetValueString("IpAddress", body.IpAddress.Trim());
            }

            if (body.Port.HasValue)
            {
                if (body.Port.Value < 1 || body.Port.Value > 65535)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Port must be between 1 and 65535",
                        StatusCode = 400,
                        Type = "Error"
                    };
                }
                accessor.SetValueInt32("Port", body.Port.Value);
            }

            if (body.DeviceNumber.HasValue)
            {
                if (body.DeviceNumber.Value < 0)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "DeviceNumber must be >= 0",
                        StatusCode = 400,
                        Type = "Error"
                    };
                }
                accessor.SetValueInt32("DeviceNumber", body.DeviceNumber.Value);
            }

            if (body.ServiceType != null)
            {
                if (!string.Equals(body.ServiceType, "Http", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(body.ServiceType, "Https", StringComparison.OrdinalIgnoreCase))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "ServiceType must be 'Http' or 'Https'",
                        StatusCode = 400,
                        Type = "Error"
                    };
                }
                // Normalise to the exact casing AlpacaDirectSettings.ServiceType expects
                accessor.SetValueString("ServiceType", body.ServiceType.Substring(0, 1).ToUpper() + body.ServiceType.Substring(1).ToLower());
            }

            // Return current values after update
            return new ApiResponse
            {
                Success = true,
                StatusCode = 200,
                Type = "AlpacaDirectSettings",
                Response = new
                {
                    DeviceType = deviceType.ToLowerInvariant(),
                    IpAddress = accessor.GetValueString("IpAddress", "127.0.0.1"),
                    Port = accessor.GetValueInt32("Port", 5000),
                    DeviceNumber = accessor.GetValueInt32("DeviceNumber", 0),
                    ServiceType = accessor.GetValueString("ServiceType", "Http"),
                }
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

public class AlpacaDirectSettingsRequest
{
    public string IpAddress { get; set; }
    public int? Port { get; set; }
    public int? DeviceNumber { get; set; }
    public string ServiceType { get; set; }
}
