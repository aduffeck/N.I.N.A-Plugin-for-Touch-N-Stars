using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using Newtonsoft.Json.Linq;

namespace TouchNStars.Server.Services;

/// <summary>
/// Parsers for the request bodies and ids of the native guider API. They check the JSON shape and types only; value
/// ranges are the guider's business, it rejects with a message code.
/// </summary>
internal static class NativeGuiderRequest
{
    public const int MaxActionIds = 50;
    public const int MaxIncidentIdLength = 100;
    public const int MaxIncidentNoteLength = 500;

    /// <summary>Parses { "name": "...", "value": ... } into the contract's invariant string form.</summary>
    public static bool TryParseSettingBody(string body, out string name, out string value, out string error)
    {
        name = null;
        value = null;
        error = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            error = "Body must be JSON: { \"name\": \"...\", \"value\": ... }";
            return false;
        }

        JObject json;
        try
        {
            json = JObject.Parse(body);
        }
        catch (Exception)
        {
            error = "Body is not valid JSON.";
            return false;
        }

        JToken nameToken = json.GetValue("name", StringComparison.OrdinalIgnoreCase);
        JToken valueToken = json.GetValue("value", StringComparison.OrdinalIgnoreCase);
        name = nameToken?.Type == JTokenType.String ? nameToken.Value<string>() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "'name' is required.";
            return false;
        }
        if (valueToken == null)
        {
            error = "'value' is required.";
            return false;
        }

        value = valueToken.Type switch
        {
            JTokenType.Null => string.Empty,
            JTokenType.Boolean => valueToken.Value<bool>() ? "true" : "false",
            JTokenType.Integer => valueToken.Value<long>().ToString(CultureInfo.InvariantCulture),
            JTokenType.Float => valueToken.Value<double>().ToString("R", CultureInfo.InvariantCulture),
            JTokenType.String => valueToken.Value<string>(),
            _ => null
        };
        if (value == null)
        {
            error = "'value' must be a string, number or boolean.";
            return false;
        }
        return true;
    }

    /// <summary>Parses the optional { minExposure, maxExposure, frames } body of darks/build.</summary>
    public static bool TryParseDarksBody(string body, out double minExposure, out double maxExposure, out int frames, out string error)
    {
        minExposure = 0.5;
        maxExposure = 4.0;
        frames = 5;
        error = null;
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                JObject json = JObject.Parse(body);
                JToken min = json.GetValue("minExposure", StringComparison.OrdinalIgnoreCase);
                JToken max = json.GetValue("maxExposure", StringComparison.OrdinalIgnoreCase);
                JToken count = json.GetValue("frames", StringComparison.OrdinalIgnoreCase);
                if (min != null && min.Type != JTokenType.Null) minExposure = min.Value<double>();
                if (max != null && max.Type != JTokenType.Null) maxExposure = max.Value<double>();
                if (count != null && count.Type != JTokenType.Null) frames = count.Value<int>();
            }
            catch (Exception)
            {
                error = "Body must be JSON: { \"minExposure\": 0.5, \"maxExposure\": 4, \"frames\": 5 }";
                return false;
            }
        }

        if (!double.IsFinite(minExposure) || !double.IsFinite(maxExposure) || minExposure <= 0 || maxExposure < minExposure || maxExposure > 60)
        {
            error = "Exposures must satisfy 0 < minExposure <= maxExposure <= 60 s.";
            return false;
        }
        if (frames < 1 || frames > 50)
        {
            error = "'frames' must be between 1 and 50.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Parses the coach/start body into AdvancedCoachOptions. Steps must be known step names (returned canonical, in
    /// session order); the numbers are passed on as given. An empty body starts all steps with the guider's defaults.
    /// </summary>
    public static bool TryParseCoachOptions(string body, out AdvancedCoachOptions options, out string error)
    {
        options = new AdvancedCoachOptions();
        const string example = "{ \"steps\": [\"CameraCheck\", \"Drift\"], \"exposureSeconds\": [1, 2], \"gains\": [100], \"driftSeconds\": 180 }";
        if (!TryParseJsonObject(body, example, out JObject json, out error))
        {
            return false;
        }

        if (!TryReadArray(json, "steps", out JArray steps, out error)) return false;
        var stepList = new List<string>();
        foreach (JToken token in steps ?? new JArray())
        {
            string name = token.Type == JTokenType.String ? token.Value<string>()?.Trim() : null;
            string canonical = NativeGuiderStates.CoachSteps.FirstOrDefault(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));
            if (canonical == null)
            {
                error = $"Unknown coach step '{token}'. Valid steps: {string.Join(", ", NativeGuiderStates.CoachSteps)}.";
                return false;
            }
            if (!stepList.Contains(canonical)) stepList.Add(canonical);
        }
        options.Steps = NativeGuiderStates.CoachSteps.Where(stepList.Contains).ToList();

        if (!TryReadArray(json, "exposureSeconds", out JArray exposures, out error)) return false;
        var exposureList = new List<double>();
        foreach (JToken token in exposures ?? new JArray())
        {
            if (!TryGetNumber(token, out double value))
            {
                error = "'exposureSeconds' must be an array of numbers.";
                return false;
            }
            exposureList.Add(value);
        }

        if (!TryReadArray(json, "gains", out JArray gains, out error)) return false;
        var gainList = new List<int>();
        foreach (JToken token in gains ?? new JArray())
        {
            if (!TryGetInt(token, out int gain))
            {
                error = "'gains' must be an array of whole numbers.";
                return false;
            }
            gainList.Add(gain);
        }
        options.ExposureSeconds = exposureList;
        options.Gains = gainList;

        if (!TryReadInt(json, "framesPerCombination", options.FramesPerCombination, out int frames, out error)) return false;
        if (!TryReadNumber(json, "driftSeconds", options.DriftSeconds, out double drift, out error)) return false;
        if (!TryReadNumber(json, "trialSeconds", options.TrialSeconds, out double trial, out error)) return false;
        if (!TryReadBool(json, "repeatBaseline", options.RepeatBaseline, out bool repeatBaseline, out error)) return false;
        if (!TryReadBool(json, "allowCalibration", options.AllowCalibration, out bool allowCalibration, out error)) return false;
        options.FramesPerCombination = frames;
        options.DriftSeconds = drift;
        options.TrialSeconds = trial;
        options.RepeatBaseline = repeatBaseline;
        options.AllowCalibration = allowCalibration;
        error = null;
        return true;
    }

    /// <summary>Parses { ids: ["drift.minMove", "trial:B"] } (non-empty, distinct, at most 50).</summary>
    public static bool TryParseIdsBody(string body, out List<string> ids, out string error)
    {
        ids = new List<string>();
        const string example = "{ \"ids\": [\"drift.minMove\", \"trial:B\"] }";
        if (string.IsNullOrWhiteSpace(body))
        {
            error = $"Body must be JSON: {example}";
            return false;
        }
        if (!TryParseJsonObject(body, example, out JObject json, out error))
        {
            return false;
        }

        if (json.GetValue("ids", StringComparison.OrdinalIgnoreCase) is not JArray array || array.Count == 0)
        {
            error = "'ids' must be a non-empty array of finding or trial ids.";
            return false;
        }
        foreach (JToken token in array)
        {
            string id = token.Type == JTokenType.String ? token.Value<string>()?.Trim() : null;
            if (string.IsNullOrEmpty(id))
            {
                error = "'ids' must only contain non-empty strings.";
                return false;
            }
            if (!ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id);
        }
        if (ids.Count > MaxActionIds)
        {
            error = $"At most {MaxActionIds} ids can be applied at once.";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>Parses { id: "hint.raOscillation" }.</summary>
    public static bool TryParseIdBody(string body, out string id, out string error)
    {
        id = null;
        const string example = "{ \"id\": \"hint.raOscillation\" }";
        if (string.IsNullOrWhiteSpace(body))
        {
            error = $"Body must be JSON: {example}";
            return false;
        }
        if (!TryParseJsonObject(body, example, out JObject json, out error))
        {
            return false;
        }
        JToken token = json.GetValue("id", StringComparison.OrdinalIgnoreCase);
        id = token?.Type == JTokenType.String ? token.Value<string>()?.Trim() : null;
        if (string.IsNullOrEmpty(id))
        {
            error = "'id' is required.";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>Incident ids (yyyyMMdd-HHmmss-Kind[-n]) consist of ASCII letters, digits and '-'; they name folders of the guider.</summary>
    public static bool IsValidIncidentId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIncidentIdLength) return false;
        foreach (char c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-') return false;
        }
        return true;
    }

    /// <summary>Parses { kept: true|false }.</summary>
    public static bool TryParseKeepBody(string body, out bool kept, out string error)
    {
        kept = false;
        const string example = "{ \"kept\": true }";
        if (string.IsNullOrWhiteSpace(body))
        {
            error = $"Body must be JSON: {example}";
            return false;
        }
        if (!TryParseJsonObject(body, example, out JObject json, out error))
        {
            return false;
        }
        JToken token = json.GetValue("kept", StringComparison.OrdinalIgnoreCase);
        if (token?.Type != JTokenType.Boolean)
        {
            error = "'kept' must be true or false.";
            return false;
        }
        kept = token.Value<bool>();
        return true;
    }

    /// <summary>
    /// Parses the optional { note } of incidents/mark into one trimmed line (line breaks become spaces) of at most
    /// 500 characters; an empty body or note means no note (null).
    /// </summary>
    public static bool TryParseMarkBody(string body, out string note, out string error)
    {
        note = null;
        if (!TryParseJsonObject(body, "{ \"note\": \"gust of wind\" }", out JObject json, out error))
        {
            return false;
        }
        JToken token = json.GetValue("note", StringComparison.OrdinalIgnoreCase);
        if (token == null || token.Type == JTokenType.Null) return true;
        if (token.Type != JTokenType.String)
        {
            error = "'note' must be a string.";
            return false;
        }

        string text = string.Join(" ", token.Value<string>().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (text.Length > MaxIncidentNoteLength)
        {
            error = $"'note' must not be longer than {MaxIncidentNoteLength} characters.";
            return false;
        }
        note = text.Length > 0 ? text : null;
        return true;
    }

    /// <summary>Parses a JSON object body; an empty body is an empty object.</summary>
    private static bool TryParseJsonObject(string body, string example, out JObject json, out string error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            json = new JObject();
            return true;
        }
        try
        {
            json = JObject.Parse(body);
            return true;
        }
        catch (Exception)
        {
            json = null;
            error = $"Body must be JSON: {example}";
            return false;
        }
    }

    /// <summary>Reads an optional array property (null when absent or null).</summary>
    private static bool TryReadArray(JObject json, string name, out JArray array, out string error)
    {
        array = null;
        error = null;
        JToken token = json.GetValue(name, StringComparison.OrdinalIgnoreCase);
        if (token == null || token.Type == JTokenType.Null) return true;
        if (token is not JArray list)
        {
            error = $"'{name}' must be an array.";
            return false;
        }
        array = list;
        return true;
    }

    private static bool TryReadInt(JObject json, string name, int fallback, out int value, out string error)
    {
        value = fallback;
        error = null;
        JToken token = json.GetValue(name, StringComparison.OrdinalIgnoreCase);
        if (token == null || token.Type == JTokenType.Null) return true;
        if (!TryGetInt(token, out value))
        {
            error = $"'{name}' must be a whole number.";
            return false;
        }
        return true;
    }

    private static bool TryReadNumber(JObject json, string name, double fallback, out double value, out string error)
    {
        value = fallback;
        error = null;
        JToken token = json.GetValue(name, StringComparison.OrdinalIgnoreCase);
        if (token == null || token.Type == JTokenType.Null) return true;
        if (!TryGetNumber(token, out value))
        {
            error = $"'{name}' must be a number.";
            return false;
        }
        return true;
    }

    private static bool TryReadBool(JObject json, string name, bool fallback, out bool value, out string error)
    {
        value = fallback;
        error = null;
        JToken token = json.GetValue(name, StringComparison.OrdinalIgnoreCase);
        if (token == null || token.Type == JTokenType.Null) return true;
        if (token.Type != JTokenType.Boolean)
        {
            error = $"'{name}' must be true or false.";
            return false;
        }
        value = token.Value<bool>();
        return true;
    }

    /// <summary>A finite JSON number.</summary>
    private static bool TryGetNumber(JToken token, out double value)
    {
        value = token.Type == JTokenType.Integer || token.Type == JTokenType.Float ? (double)token : double.NaN;
        return double.IsFinite(value);
    }

    /// <summary>A JSON number without a fraction that fits an int (2 and 2.0 alike).</summary>
    private static bool TryGetInt(JToken token, out int value)
    {
        value = 0;
        if (!TryGetNumber(token, out double number) || number != Math.Floor(number) || number < int.MinValue || number > int.MaxValue)
        {
            return false;
        }
        value = (int)number;
        return true;
    }
}
