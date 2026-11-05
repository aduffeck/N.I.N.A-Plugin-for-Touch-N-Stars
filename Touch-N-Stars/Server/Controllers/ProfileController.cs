using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Model;
using NINA.Core.Utility;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TouchNStars.Utility;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for NINA profile management.
/// </summary>
public class ProfileController : WebApiController
{
    // one upload at a time: JsonFileStore.Write uses a fixed temporary file next to the horizon file
    private static readonly object HorizonLock = new();

    /// <summary>
    /// POST /api/profile/horizon
    /// Accepts the raw text content of a .hrz horizon file in the request body.
    /// Parses it, writes it to the active profile's horizon file and loads it through the profile service.
    /// Returns 400 if the body is empty or the horizon data is malformed (fewer than 2 points).
    /// </summary>
    [Route(HttpVerbs.Post, "/profile/horizon")]
    public async Task<object> SetHorizonFromBody()
    {
        string body;
        using (var reader = new StreamReader(HttpContext.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync();
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            HttpContext.Response.StatusCode = 400;
            return new { success = false, error = "Request body is empty" };
        }

        CustomHorizon horizon;
        try
        {
            using var sr = new StringReader(body);
            horizon = CustomHorizon.FromReader_Standard(sr);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 400;
            return new { success = false, error = "Horizon data is malformed: " + ex.Message };
        }

        var profileService = TouchNStars.Mediators.Profile;

        // One file per profile: all profiles shared tnshorizon.hrz, so an upload in one profile changed the horizon of
        // every other profile that had one uploaded.
        string filePath = Path.Combine(CoreUtil.APPLICATIONTEMPPATH, $"tnshorizon-{profileService.ActiveProfile.Id}.hrz");
        lock (HorizonLock)
        {
            try
            {
                // a power cut leaves the old or the new file, not a cut-off one that is dropped at the next start
                JsonFileStore.Write(filePath, body);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                HttpContext.Response.StatusCode = 500;
                return new { success = false, error = "The horizon could not be saved: " + ex.Message };
            }

            try
            {
                // Setting the profile values directly raised no HorizonChanged: loaded targets ("loop until altitude"),
                // the framing assistant and the sky atlas kept the old horizon until NINA was restarted.
                profileService.ChangeHorizon(filePath);
            }
            catch (Exception ex)
            {
                // a HorizonChanged handler failed; the horizon itself is set
                Logger.Error(ex);
            }
        }

        // ChangeHorizon reports a file it cannot load only as a notification and clears the horizon
        if (profileService.ActiveProfile.AstrometrySettings.Horizon == null)
        {
            HttpContext.Response.StatusCode = 500;
            return new { success = false, error = "The horizon file could not be loaded (see the NINA log)" };
        }

        return "Horizon updated";
    }
}
