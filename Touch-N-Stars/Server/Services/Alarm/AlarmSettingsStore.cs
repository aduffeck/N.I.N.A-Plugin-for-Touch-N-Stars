#nullable enable
using NINA.Core.Utility;
using System;
using System.Text.Json;
using TouchNStars.Server.Controllers;

namespace TouchNStars.Server.Services.Alarm;

/// <summary>
/// The alarm settings in the TNS settings file (key "pins_alarm"). Read and write are injected, so tests never touch
/// the real file. A value written through the generic /api/settings is picked up by <see cref="Refresh"/>.
/// </summary>
public sealed class AlarmSettingsStore {
    public const string Key = "pins_alarm";

    private readonly object gate = new();
    private readonly Func<string?> read;
    private readonly Action<string> write;
    private AlarmSettings current = AlarmSettings.Defaults();
    private bool stored;
    private string? lastRaw;
    private bool loaded;

    public AlarmSettingsStore(Func<string?> read, Action<string> write) {
        this.read = read;
        this.write = write;
    }

    public static AlarmSettingsStore CreateDefault() => new(
        () => SettingsController.TryGetRawSetting(Key, out var value) ? value : null,
        value => SettingsController.SetRawSetting(Key, value));

    public AlarmSettings Current { get { lock (gate) { EnsureLoaded(); return current; } } }

    /// <summary>False while the defaults are used (nothing or nothing valid stored).</summary>
    public bool Stored { get { lock (gate) { EnsureLoaded(); return stored; } } }

    public void Refresh() {
        lock (gate) {
            Load();
        }
    }

    /// <summary>Applies <paramref name="patch"/> and stores the result. Write errors propagate.</summary>
    public bool TryUpdate(JsonElement patch, out AlarmSettings result, out string error) {
        lock (gate) {
            Load();
            if (!AlarmSettings.TryMerge(current, patch, out result, out error)) {
                return false;
            }
            string raw = result.Serialize();
            write(raw);
            lastRaw = raw;
            current = result;
            stored = true;
            return true;
        }
    }

    private void EnsureLoaded() {
        if (!loaded) {
            Load();
        }
    }

    // Callers hold the lock
    private void Load() {
        string? raw;
        try {
            raw = read();
        } catch (Exception ex) {
            Logger.Warning($"Alarm settings could not be read, keeping the current ones: {ex.Message}");
            loaded = true;
            return;
        }
        if (loaded && raw == lastRaw) {
            return;
        }
        loaded = true;
        lastRaw = raw;
        var parsed = AlarmSettings.Parse(raw);
        if (parsed == null && raw != null) {
            Logger.Warning("Stored alarm settings are invalid, using the defaults");
        }
        current = parsed ?? AlarmSettings.Defaults();
        stored = parsed != null;
    }
}
