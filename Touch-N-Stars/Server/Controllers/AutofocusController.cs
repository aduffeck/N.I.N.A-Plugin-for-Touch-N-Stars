using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using TouchNStars.Server.Infrastructure;
using TouchNStars.Server.Models;
using TouchNStars.Utility;

namespace TouchNStars.Server.Controllers;

public class AutofocusController : WebApiController
{
    [Route(HttpVerbs.Get, "/autofocus/{action}")]
    public async Task<object> ControlAutofocus(string action)
    {
        string targetUrl = $"{await CoreUtility.GetApiUrl()}/equipment/focuser/auto-focus";
        bool info = action.Equals("info");
        bool start = action.Equals("start");
        bool stop = action.Equals("stopp");

        if (info)
        {
            return new Dictionary<string, object>() {
                { "Success", true },
                { "autofocus_running", DataContainer.afRun },
                { "newAfGraph", DataContainer.newAfGraph },
                { "afError", DataContainer.afError },
                { "afErrorText", DataContainer.afErrorText },
            };
        }
        if (start)
        {
            bool wasRunning = DataContainer.afRun;
            DataContainer.afRun = true;
            DataContainer.newAfGraph = false;
            DataContainer.afError = false;
            DataContainer.afErrorText = string.Empty;

            try
            {
                HttpClient client = new HttpClient();
                HttpResponseMessage response = await client.GetAsync(targetUrl);

                if (response.IsSuccessStatusCode)
                {
                    ApiResponse apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse>();
                    if (apiResponse.Success)
                    {
                        return new Dictionary<string, object>() { { "message", "Autofokus gestartet" } };
                    }
                    else
                    {
                        AutofocusNotStarted(wasRunning);
                        return new Dictionary<string, object>() { { "message", $"Fehler beim Starten des Autofokus: {apiResponse.Error}" } };
                    }
                }
                else
                {
                    AutofocusNotStarted(wasRunning);
                    return new Dictionary<string, object>() { { "message", $"Fehler beim Starten des Autofokus: {response.StatusCode}" } };
                }
            }
            catch (Exception ex)
            {
                AutofocusNotStarted(wasRunning);
                Logger.Error(ex);
                HttpContext.Response.StatusCode = 500;
                return new Dictionary<string, object>() { { "error", "Interner Fehler beim Starten des Autofokus" } };
            }
        }

        if (stop)
        {
            DataContainer.afRun = false;
            DataContainer.newAfGraph = false;

            try
            {
                HttpClient client = new HttpClient();
                HttpResponseMessage response = await client.GetAsync(targetUrl + "?cancel=true");

                if (response.IsSuccessStatusCode)
                {
                    ApiResponse apiResponse = await response.Content.ReadFromJsonAsync<ApiResponse>();
                    if (apiResponse.Success)
                    {
                        return new Dictionary<string, object>() { { "message", "Autofokus gestoppt" } };
                    }
                    else
                    {
                        return new Dictionary<string, object>() { { "message", $"Fehler beim Stoppen des Autofokus: {apiResponse.Error}" } };
                    }
                }
                else
                {
                    return new Dictionary<string, object>() { { "message", $"Fehler beim Stoppen des Autofokus: {response.StatusCode}" } };
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                HttpContext.Response.StatusCode = 500;
                return new Dictionary<string, object>() { { "error", "Interner Fehler beim Stopoen des Autofokus" } };
            }
        }

        return new Dictionary<string, object>() { { "error", "Ungültige Anfrage" } };
    }

    // afRun is set before the start: a refused start (no focuser, ninaAPI not reachable) left it set, and the app showed
    // an autofocus that wasn't running. It goes back to what it was, as ninaAPI also refuses a start while its autofocus
    // still runs
    private static void AutofocusNotStarted(bool wasRunning)
    {
        lock (DataContainer.lockObj)
        {
            DataContainer.afRun = wasRunning;
        }
    }
}
