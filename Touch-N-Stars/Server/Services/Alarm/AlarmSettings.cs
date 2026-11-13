#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TouchNStars.Server.Services.Alarm;

public sealed class AlarmCategories {
    public bool Errors { get; set; } = true;
    public bool Guiding { get; set; } = true;
    public bool Mount { get; set; } = true;
    public bool Safety { get; set; } = true;
    public bool PowerBox { get; set; } = true;
    public bool Disk { get; set; } = true;
    public bool Warnings { get; set; } = false;

    public bool IsOn(AlarmCategory category) => category switch {
        AlarmCategory.Errors => Errors,
        AlarmCategory.Guiding => Guiding,
        AlarmCategory.Mount => Mount,
        AlarmCategory.Safety => Safety,
        AlarmCategory.PowerBox => PowerBox,
        AlarmCategory.Disk => Disk,
        AlarmCategory.Warnings => Warnings,
        _ => true,
    };
}

/// <summary>
/// Alarm settings, stored as JSON in the TNS settings file (key "pins_alarm"). Treated as immutable once published:
/// an update creates a new instance.
/// </summary>
public sealed class AlarmSettings {
    public const int MaxExclusions = 50;

    private static readonly JsonSerializerOptions Options = new() {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
    };

    public int Version { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public AlarmCategories Categories { get; set; } = new();

    /// <summary>1..100; the buzzer duty is min(volume, 50) %, so 50..100 are all the loudest.</summary>
    public int Volume { get; set; } = 50;
    public int ToneMs { get; set; } = 400;
    public int PauseMs { get; set; } = 600;
    public int BurstSeconds { get; set; } = 5;
    public int GraceMinutes { get; set; } = 3;
    public int ThrottleMinutes { get; set; } = 10;

    /// <summary>Case-insensitive parts of "category|key|message|detail"; an alarm containing one is not beeped.</summary>
    public List<string> Exclusions { get; set; } = new();

    [JsonIgnore]
    public int Tones => Math.Max(1, BurstSeconds * 1000 / (ToneMs + PauseMs));

    public static AlarmSettings Defaults() => new();

    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>The stored value, or null if it is missing, unreadable or invalid.</summary>
    public static AlarmSettings? Parse(string? json) {
        if (string.IsNullOrWhiteSpace(json)) {
            return null;
        }
        try {
            var settings = JsonSerializer.Deserialize<AlarmSettings>(json, Options);
            if (settings == null) {
                return null;
            }
            settings.Categories ??= new AlarmCategories();
            settings.Exclusions ??= new List<string>();
            settings.NormalizeExclusions();
            return settings.Validate() == null ? settings : null;
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>
    /// <paramref name="current"/> with the fields of <paramref name="patch"/> applied (unknown fields ignored,
    /// Categories merged per field). False with the first bad field in <paramref name="error"/>.
    /// </summary>
    public static bool TryMerge(AlarmSettings current, JsonElement patch, out AlarmSettings merged, out string error) {
        merged = current;
        if (patch.ValueKind != JsonValueKind.Object) {
            error = "Settings must be a JSON object";
            return false;
        }

        var node = JsonNode.Parse(current.Serialize())!.AsObject();
        JsonObject patchNode;
        try {
            patchNode = JsonNode.Parse(patch.GetRawText())!.AsObject();
            // The properties are read lazily: a duplicate key throws here, not later in the loop
            _ = patchNode.Count;
        } catch (ArgumentException ex) {
            // e.g. a key given twice
            error = $"Invalid settings: {ex.Message}";
            return false;
        }
        foreach (var (name, value) in patchNode) {
            string? canonical = node.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (canonical == null) {
                continue;
            }
            if (canonical == nameof(Categories) && value is JsonObject categories && node[canonical] is JsonObject target) {
                foreach (var (catName, catValue) in categories) {
                    string? catCanonical = target.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, catName, StringComparison.OrdinalIgnoreCase));
                    if (catCanonical != null) {
                        target[catCanonical] = catValue?.DeepClone();
                    }
                }
                continue;
            }
            node[canonical] = value?.DeepClone();
        }

        AlarmSettings? result;
        try {
            result = node.Deserialize<AlarmSettings>(Options);
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) {
            error = $"Invalid value: {ex.Message}";
            return false;
        }
        if (result == null || result.Categories == null || result.Exclusions == null) {
            error = "Invalid value: null";
            return false;
        }
        result.NormalizeExclusions();
        error = result.Validate() ?? "";
        if (error.Length > 0) {
            return false;
        }
        merged = result;
        return true;
    }

    /// <summary>The first field outside its range, or null.</summary>
    public string? Validate() {
        static string? Range(string name, int value, int min, int max) =>
            value < min || value > max ? $"{name} must be {min}..{max}" : null;

        return Range(nameof(Volume), Volume, 1, 100)
            ?? Range(nameof(ToneMs), ToneMs, 50, 2000)
            ?? Range(nameof(PauseMs), PauseMs, 100, 5000)
            ?? Range(nameof(BurstSeconds), BurstSeconds, 1, 30)
            ?? Range(nameof(GraceMinutes), GraceMinutes, 0, 30)
            ?? Range(nameof(ThrottleMinutes), ThrottleMinutes, 1, 240)
            ?? (Exclusions.Count > MaxExclusions ? $"{nameof(Exclusions)}: at most {MaxExclusions} entries" : null)
            ?? (Exclusions.Any(e => e.Length < 2 || e.Length > 200) ? $"{nameof(Exclusions)}: entries must be 2..200 characters" : null);
    }

    private void NormalizeExclusions() {
        Exclusions = Exclusions
            .Where(e => e != null)
            .Select(e => e.Trim())
            .Where(e => e.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
