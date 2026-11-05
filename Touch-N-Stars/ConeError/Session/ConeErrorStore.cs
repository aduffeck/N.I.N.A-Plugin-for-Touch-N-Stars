using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NINA.Core.Utility;
using TouchNStars.ConeError.Domain;
using TouchNStars.Utility;

namespace TouchNStars.ConeError.Session;

/// <summary>Settings of the cone error wizard (TnsCache/cone-error/settings.json).</summary>
public sealed class ConeErrorSettings
{
    public const double DefaultSunExclusionDegrees = 30.0;
    public const double MinSunExclusionDegrees = 15.0;

    public double SunExclusionDegrees { get; set; } = DefaultSunExclusionDegrees;

    /// <summary>Minimum altitude of the automated measurement.</summary>
    public double AutoMinAltitudeDegrees { get; set; } = 20.0;

    /// <summary>Minimum altitude of the daytime check (landmarks are low).</summary>
    public double ManualMinAltitudeDegrees { get; set; } = 0.0;

    /// <summary>Also check the RA-first and Dec-first paths of a flip against the Sun.</summary>
    public bool StrictPath { get; set; }

    public double? DovetailMm { get; set; }

    public int Repetitions { get; set; } = 1;

    public double? ExposureSeconds { get; set; }

    /// <summary>Distance of the optical axis from the RA axis, for the parallax of the daytime estimate.</summary>
    public double? AxisOffsetMm { get; set; }

    /// <summary>Mounts whose pier side labels the user has confirmed against the counterweight position.</summary>
    public List<string> VerifiedPierLabels { get; set; } = new();

    /// <summary>Clamps values a client may have sent out of range.</summary>
    public ConeErrorSettings Sanitized()
    {
        return new ConeErrorSettings
        {
            SunExclusionDegrees = Math.Clamp(double.IsNaN(SunExclusionDegrees) ? DefaultSunExclusionDegrees : SunExclusionDegrees, MinSunExclusionDegrees, 90.0),
            AutoMinAltitudeDegrees = Math.Clamp(AutoMinAltitudeDegrees, 0.0, 80.0),
            ManualMinAltitudeDegrees = Math.Clamp(ManualMinAltitudeDegrees, -10.0, 80.0),
            StrictPath = StrictPath,
            DovetailMm = DovetailMm is > 0 and < 2000 ? DovetailMm : null,
            Repetitions = Math.Clamp(Repetitions, TargetPlanner.MinRepetitions, TargetPlanner.MaxRepetitions),
            ExposureSeconds = ExposureSeconds is > 0 and <= 120 ? ExposureSeconds : null,
            AxisOffsetMm = AxisOffsetMm is > 0 and < 2000 ? AxisOffsetMm : null,
            VerifiedPierLabels = (VerifiedPierLabels ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList()
        };
    }
}

/// <summary>One east/west pair of the automated measurement.</summary>
public sealed class ConePairRecord
{
    public int Index { get; set; }
    public double RightAscensionHours { get; set; }
    public double DeclinationDegrees { get; set; }
    public double? DeltaRaEastArcmin { get; set; }
    public double? DeltaRaWestArcmin { get; set; }

    /// <summary>ΔDec(W) − ΔDec(E): a Dec index error shows here, not in the cone.</summary>
    public double? DeltaDecWestMinusEastArcmin { get; set; }

    public double? ConeArcmin { get; set; }
    public bool Rejected { get; set; }
    public string Problem { get; set; }
}

/// <summary>A finished measurement, kept for the before/after comparison (TnsCache/cone-error/results.json).</summary>
public sealed class ConeResultRecord
{
    public string Id { get; set; }

    /// <summary>"auto" (plate solves) or "manual" (daytime estimate).</summary>
    public string Mode { get; set; }

    public DateTime TimestampUtc { get; set; }
    public string ProfileId { get; set; }
    public string ProfileName { get; set; }
    public string MountName { get; set; }
    public double DeclinationDegrees { get; set; }
    public double? DovetailMm { get; set; }
    public double ConeArcmin { get; set; }
    public double? StdDevArcmin { get; set; }
    public double? StdErrorArcmin { get; set; }
    public double? MedianArcmin { get; set; }
    public int PairsUsed { get; set; }
    public int PairsTotal { get; set; }
    public string Quality { get; set; }
    public bool Significant { get; set; }
    public double? ShimMm { get; set; }

    /// <summary>"Front", "Rear" or "None".</summary>
    public string ShimEnd { get; set; }

    public double? ParallaxArcmin { get; set; }
    public List<ConePairRecord> Pairs { get; set; } = new();
}

/// <summary>
/// Written before the sessions change the mount's preferred pier side or tracking, cleared after the
/// restore (TnsCache/cone-error/restore.json). A leftover journal means the plugin stopped mid-session;
/// the UI then offers to restore. Restoring only changes settings, it never moves the mount.
/// </summary>
public sealed class RestoreJournal
{
    public DateTime CreatedUtc { get; set; }
    public string MountName { get; set; }
    public string Mode { get; set; }

    /// <summary>The preferred pier side before the session, as the pier side control reported it; null if unchanged.</summary>
    public string PreferredPierSide { get; set; }

    /// <summary>Tracking before the session; null if the session does not change it.</summary>
    public bool? Tracking { get; set; }

    /// <summary>
    /// The journal could not be read (kept as restore.json.corrupt): a session forced the mount's settings, but what they
    /// were before is unknown.
    /// </summary>
    public bool OriginalUnknown { get; set; }
}

/// <summary>
/// JSON files of the wizard, in one directory, each written under a lock. Settings and results go through
/// <see cref="JsonFileStore"/> (synced to disk, previous save kept as .bkp, an unparsable file moved aside); a file
/// that cannot be read throws, so it is never replaced by a save that started from nothing.
/// </summary>
public sealed class ConeErrorStore
{
    public const int MaxResults = 50;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object fileLock = new();

    public ConeErrorStore(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "TnsCache", "cone-error");

    private string SettingsPath => Path.Combine(Directory, "settings.json");
    private string ResultsPath => Path.Combine(Directory, "results.json");
    private string JournalPath => Path.Combine(Directory, "restore.json");
    private string CorruptJournalPath => Path.Combine(Directory, "restore.json.corrupt");

    public ConeErrorSettings LoadSettings() => (Read<ConeErrorSettings>(SettingsPath, "cone error settings") ?? new ConeErrorSettings()).Sanitized();

    public ConeErrorSettings SaveSettings(ConeErrorSettings settings)
    {
        ConeErrorSettings clean = settings.Sanitized();
        Save(SettingsPath, clean);
        return clean;
    }

    public List<ConeResultRecord> LoadResults() => Read<List<ConeResultRecord>>(ResultsPath, "cone error results") ?? new List<ConeResultRecord>();

    public void AddResult(ConeResultRecord result)
    {
        lock (fileLock)
        {
            List<ConeResultRecord> results = LoadResults();
            results.Add(result);
            Save(ResultsPath, results.OrderByDescending(r => r.TimestampUtc).Take(MaxResults).OrderBy(r => r.TimestampUtc).ToList());
        }
    }

    /// <summary>The latest earlier result of the same mode for the same profile and mount, for before/after.</summary>
    public ConeResultRecord PreviousResult(ConeResultRecord current)
    {
        return LoadResults()
            .Where(r => r.Id != current.Id && r.Mode == current.Mode && r.ProfileId == current.ProfileId && r.MountName == current.MountName && r.TimestampUtc < current.TimestampUtc)
            .OrderByDescending(r => r.TimestampUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// The restore journal, null when none is open. An unreadable journal counted as none, so a session's forced
    /// settings were never put back: it is kept as restore.json.corrupt and stays open with its originals unknown.
    /// </summary>
    public RestoreJournal LoadJournal()
    {
        lock (fileLock)
        {
            if (File.Exists(JournalPath))
            {
                try
                {
                    return JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllText(JournalPath), Json)
                        ?? new RestoreJournal { OriginalUnknown = true };
                }
                catch (Exception ex)
                {
                    Logger.Warning($"ConeError: the restore journal cannot be read, keeping it as {CorruptJournalPath}: {ex.Message}");
                    try
                    {
                        File.Move(JournalPath, CorruptJournalPath, overwrite: true);
                    }
                    catch (Exception moveEx)
                    {
                        Logger.Warning($"ConeError: could not keep the unreadable restore journal: {moveEx.Message}");
                    }

                    return new RestoreJournal { OriginalUnknown = true };
                }
            }

            return File.Exists(CorruptJournalPath)
                ? new RestoreJournal { OriginalUnknown = true, CreatedUtc = File.GetLastWriteTimeUtc(CorruptJournalPath) }
                : null;
        }
    }

    public void SaveJournal(RestoreJournal journal) => Write(JournalPath, journal);

    public void ClearJournal()
    {
        lock (fileLock)
        {
            try
            {
                File.Delete(JournalPath);
                File.Delete(CorruptJournalPath);
            }
            catch (Exception ex)
            {
                Logger.Warning($"ConeError: could not delete the restore journal: {ex.Message}");
            }
        }
    }

    private T Read<T>(string path, string what) where T : class
    {
        lock (fileLock)
        {
            return JsonFileStore.Read(path, json => JsonSerializer.Deserialize<T>(json, Json), () => null, what);
        }
    }

    private void Save<T>(string path, T value)
    {
        lock (fileLock)
        {
            JsonFileStore.Write(path, JsonSerializer.Serialize(value, Json));
        }
    }

    // The restore journal keeps its own handling (LoadJournal, ClearJournal): no backup, so a cleared journal stays gone.
    private void Write<T>(string path, T value)
    {
        lock (fileLock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, overwrite: true);
        }
    }
}
