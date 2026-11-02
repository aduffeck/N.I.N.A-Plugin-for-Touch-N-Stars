using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Enum;
using NINA.Core.Utility;
using System;
using System.Globalization;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TouchNStars.Utility;

namespace TouchNStars.Server.Controllers;

public class UtilityController : WebApiController
{
    private static readonly List<string> excluded_members = new List<string>() { "GetEquipment", "RequestAll", "LoadPlugin" };

    [Route(HttpVerbs.Get, "/logs")]
    public List<Hashtable> GetRecentLogs([QueryField(true)] int count, [QueryField] string level)
    {
        List<Hashtable> logs = new List<Hashtable>();

        if (string.IsNullOrEmpty(level))
        {
            level = string.Empty;
        }

        if (level.Equals("ERROR") || level.Equals("WARNING") || level.Equals("INFO") || level.Equals("DEBUG") || string.IsNullOrEmpty(level))
        {
            string currentLogFile = Directory.GetFiles(CoreUtility.LogPath).OrderByDescending(File.GetCreationTime).First();

            // the app asks for the last 50 lines every second: reading the whole log for them (megabytes by the end of a
            // night) kept the Pi's CPU and GC busy all the time. Only the end of the log is read now
            List<string> lines = LastShownLines(currentLogFile, count, line => IsShown(line, level));
            foreach (string line in lines)
            {
                string[] parts = line.Split('|');
                if (parts.Length >= 6)
                {
                    logs.Add(new Hashtable() {
                        { "timestamp", parts[0] },
                        { "level", parts[1] },
                        { "source", parts[2] },
                        { "member", parts[3] },
                        { "line", parts[4] },
                        { "message", string.Join('|', parts.Skip(5)).Trim() }
                    });
                }
            }
        }
        logs.Reverse();
        return logs;
    }

    private static bool IsShown(string line, string level)
    {
        if (!string.IsNullOrEmpty(level) && !line.Contains('|' + level + '|'))
        {
            return false;
        }

        if (line.Contains("DATE|LEVEL|SOURCE|MEMBER|LINE|MESSAGE"))
        {
            return false;
        }

        return !excluded_members.Any(line.Contains);
    }

    private const int LogBlockSize = 64 * 1024;

    /// <summary>
    /// The last <paramref name="count"/> lines of the file that are shown, oldest first: the lines split on '\n' as the
    /// whole file split would give them (the empty one after the last newline included). The file is read backwards in
    /// blocks until enough lines are found. Lines are split on the '\n' byte, which is never part of a UTF-8 character,
    /// and decoded one by one.
    /// </summary>
    private static List<string> LastShownLines(string path, int count, Func<string, bool> shown)
    {
        var found = new List<string>();
        if (count <= 0)
        {
            return found;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long position = stream.Length;
        byte[] rest = [];
        while (true)
        {
            int size = (int)Math.Min(LogBlockSize, position);
            position -= size;
            var block = new byte[size + rest.Length];
            stream.Position = position;
            stream.ReadExactly(block, 0, size);
            rest.CopyTo(block, size);

            int lineEnd = block.Length;
            for (int i = block.Length - 1; i >= 0; i--)
            {
                if (block[i] == (byte)'\n')
                {
                    if (TakeLine(block, i + 1, lineEnd, shown, found, count))
                    {
                        found.Reverse();
                        return found;
                    }

                    lineEnd = i;
                }
            }

            if (position == 0)
            {
                // the first line, without the BOM StreamReader left out
                int start = lineEnd >= 3 && block[0] == 0xEF && block[1] == 0xBB && block[2] == 0xBF ? 3 : 0;
                TakeLine(block, start, lineEnd, shown, found, count);
                found.Reverse();
                return found;
            }

            rest = block[..lineEnd];
        }
    }

    // adds the line if it is shown; true once enough lines are found
    private static bool TakeLine(byte[] bytes, int start, int end, Func<string, bool> shown, List<string> found, int count)
    {
        string line = Encoding.UTF8.GetString(bytes, start, end - start);
        if (shown(line))
        {
            found.Add(line);
        }

        return found.Count >= count;
    }

    [Route(HttpVerbs.Get, "/loglevel")]
    public object GetLogLevel()
    {
        try
        {
            var level = TouchNStars.Mediators.Profile.ActiveProfile.ApplicationSettings.LogLevel;
            return new Dictionary<string, object>
            {
                { "success", true },
                { "logLevel", level.ToString() },
                { "availableLevels", System.Enum.GetNames(typeof(LogLevelEnum)) }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", ex.Message }
            };
        }
    }

    [Route(HttpVerbs.Put, "/loglevel")]
    public async Task<object> SetLogLevel()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, string>>();
            if (body == null || !body.TryGetValue("logLevel", out var levelStr))
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object> { { "success", false }, { "error", "Missing 'logLevel' in request body" } };
            }

            if (!System.Enum.TryParse<LogLevelEnum>(levelStr.ToUpperInvariant(), out var newLevel))
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", $"Invalid log level '{levelStr}'. Valid values: {string.Join(", ", System.Enum.GetNames(typeof(LogLevelEnum)))}" }
                };
            }

            TouchNStars.Mediators.Profile.ActiveProfile.ApplicationSettings.LogLevel = newLevel;
            Logger.SetLogLevel(newLevel);
            Logger.Info($"Log level changed to {newLevel} via Touch-N-Stars API");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "logLevel", newLevel.ToString() }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", ex.Message }
            };
        }
    }

    private static readonly string[] AvailableLanguages =
    [
        "en-GB", "en-US", "de-DE", "it-IT", "es-ES", "gl-ES",
        "zh-CN", "zh-HK", "zh-TW", "fr-FR", "ru-RU", "pl-PL",
        "nl-NL", "ja-JP", "tr-TR", "pt-PT", "el-GR", "cs-CZ",
        "ca-ES", "nb-NO", "ko-KR"
    ];

    [Route(HttpVerbs.Get, "/language")]
    public object GetLanguage()
    {
        try
        {
            var culture = TouchNStars.Mediators.Profile.ActiveProfile.ApplicationSettings.Culture;
            return new Dictionary<string, object>
            {
                { "success", true },
                { "language", culture },
                { "availableLanguages", AvailableLanguages }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", ex.Message }
            };
        }
    }

    [Route(HttpVerbs.Put, "/language")]
    public async Task<object> SetLanguage()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, string>>();
            if (body == null || !body.TryGetValue("language", out var cultureStr))
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object> { { "success", false }, { "error", "Missing 'language' in request body" } };
            }

            if (!AvailableLanguages.Contains(cultureStr))
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", $"Invalid language '{cultureStr}'. Valid values: {string.Join(", ", AvailableLanguages)}" }
                };
            }

            var cultureInfo = new CultureInfo(cultureStr);
            TouchNStars.Mediators.Profile.ChangeLocale(cultureInfo);
            Logger.Info($"Language changed to {cultureStr} via Touch-N-Stars API");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "language", cultureStr }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", ex.Message }
            };
        }
    }

    [Route(HttpVerbs.Get, "/polling-interval")]
    public object GetPollingInterval()
    {
        try
        {
            var interval = TouchNStars.Mediators.Profile.ActiveProfile.ApplicationSettings.DevicePollingInterval;
            return new Dictionary<string, object>
            {
                { "success", true },
                { "pollingInterval", interval }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", ex.Message }
            };
        }
    }

    [Route(HttpVerbs.Put, "/polling-interval")]
    public async Task<object> SetPollingInterval()
    {
        try
        {
            var body = await HttpContext.GetRequestDataAsync<Dictionary<string, string>>();
            if (body == null || !body.TryGetValue("pollingInterval", out var intervalStr))
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object> { { "success", false }, { "error", "Missing 'pollingInterval' in request body" } };
            }

            if (!double.TryParse(intervalStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var newInterval) || newInterval <= 0)
            {
                HttpContext.Response.StatusCode = 400;
                return new Dictionary<string, object>
                {
                    { "success", false },
                    { "error", "Invalid 'pollingInterval': must be a positive number (seconds)" }
                };
            }

            TouchNStars.Mediators.Profile.ActiveProfile.ApplicationSettings.DevicePollingInterval = newInterval;
            Logger.Info($"Device polling interval changed to {newInterval}s via Touch-N-Stars API");

            return new Dictionary<string, object>
            {
                { "success", true },
                { "pollingInterval", newInterval },
                { "note", "Change takes effect when devices are reconnected" }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", ex.Message }
            };
        }
    }

    [Route(HttpVerbs.Get, "/get-api-port")]
    public async Task<int> GetApiPort()
    {
        return await TouchNStars.Communicator.GetPort(true);
    }

    [Route(HttpVerbs.Get, "/version")]
    public object GetAssemblyVersion()
    {
        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString();

            return new Dictionary<string, object>
            {
                { "success", true },
                { "version", version }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", ex.Message }
            };
        }
    }
}
