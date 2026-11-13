#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TouchNStars.Server.Services.Alarm;

/// <summary>
/// Turns log records into alarm candidates: every ERROR, a catalogue of WARNINGs that are critical anyway, other
/// WARNINGs only with the Warnings category on. Drops built-in exclusions (own lines, request-driven code, known
/// harmless lines) before they reach the engine.
/// </summary>
public sealed class AlarmLogClassifier {
    private const int MaxKeyLength = 160;

    // Sources never alarmed: the alarm service itself (a failure there must not alarm), TNS's own PHD2 client (logs
    // ERRORs whenever PHD2 is not running; imaging guiding goes through N.I.N.A.'s PHD2Guider, watched separately).
    private static readonly string[] ExcludedSourcePrefixes = { "PinsAlarm", "Alarm" };
    // TNS services that answer app requests (shown in the app): tilter, sky survey downloads, Bahtinov and star
    // detection tools, the internal guider API.
    private static readonly HashSet<string> ExcludedSources = new(StringComparer.OrdinalIgnoreCase) {
        "BackgroundWorker.cs", "PinsPowerBoxAccess.cs", "PHD2Service.cs", "PHD2ImageService.cs", "PHD2Controller.cs",
        "TilterService.cs", "HipsSurveyService.cs", "BahtinovAnalysisService.cs", "StarDetectionOptionsService.cs",
        "InternalGuiderService.cs",
    };

    // Known harmless lines: source, member and (optionally) a message part. From real logs: startup, connecting gear,
    // plate solve attempts that are retried, loading a sequence, the plugin's own failed-beep warning.
    private static readonly (string Source, string Member, string Message)[] ExcludedLines = {
        ("DllLoader.cs", "LoadDllFromAbsolutePath", ""),
        ("PluginLoader.cs", "AssignSequenceEntity", ""),
        ("PluginFetcher.cs", "ValidateAndParseManifest", ""),
        ("MountVM.cs", "UpdateDeviceInfo", "Failed to query product firmware"),
        ("ASTAPSolver.cs", "ReadResult", ""),
        ("JsonCreationConverter.cs", "ReadJson", ""),
        ("PowerBoxDriver.cs", "Beep", ""),
        // .NET runtime noise, not a fault of the rig
        ("Program.cs", "", "unobserved task exception"),
    };

    // WARNINGs that are critical: alarmed whatever the Warnings toggle says
    private static readonly (string Source, string Message, AlarmCategory Category)[] CriticalWarnings = {
        ("OnStepXTelescope.cs", "OnStepX: controller reports error", AlarmCategory.Mount),
        ("OnStepXTelescope.cs", "OnStepX: goto ended with controller error", AlarmCategory.Mount),
        ("PHD2Guider.cs", "PHD2 - Alert (error)", AlarmCategory.Guiding),
    };

    private readonly HashSet<string> controllerTypeNames;

    /// <param name="controllerTypeNames">
    /// Type names of TNS's API controllers: their ERRORs answer app requests and are shown in the app, so they do not
    /// alarm. Matched as "Name.cs" or "Name." (partial files), never by a "Controller.cs" suffix (N.I.N.A. has
    /// controllers of its own, e.g. ImageSaveController.cs).
    /// </param>
    public AlarmLogClassifier(IEnumerable<string> controllerTypeNames) {
        this.controllerTypeNames = new HashSet<string>(controllerTypeNames, StringComparer.Ordinal);
    }

    /// <summary>N.I.N.A.'s shutdown starts (logged before the disconnects that follow).</summary>
    public static bool IsShutdownLine(LogRecord r) {
        return r.Level == "INFO" && r.Source == "Program.cs" && r.Message.Contains("Application shutting down", StringComparison.Ordinal);
    }

    public AlarmCandidate? Classify(LogRecord r, bool warningsOn) {
        bool isError = r.Level is "ERROR" or "FATAL";
        bool isWarning = r.Level == "WARNING";
        if (!isError && !isWarning) {
            return null;
        }
        if (IsExcluded(r)) {
            return null;
        }

        string message = r.Message.Length > 0 ? r.Message : (r.Detail.Length > 0 ? r.Detail : "(exception without message)");

        AlarmCategory category;
        if (isWarning) {
            var critical = CriticalWarnings.FirstOrDefault(c => r.Source == c.Source && message.Contains(c.Message, StringComparison.Ordinal));
            if (critical.Source != null) {
                category = critical.Category;
            } else if (warningsOn) {
                category = AlarmCategory.Warnings;
            } else {
                return null;
            }
        } else {
            category = Recategorize(r, message);
        }

        bool alwaysArmed = category == AlarmCategory.PowerBox && IsAlwaysArmedPowerBox(message);
        string key = NormalizeKey(r.Source, r.Member, message, keepDigits: category == AlarmCategory.PowerBox);
        return new AlarmCandidate(category, key, message, AlarmOrigin.Log, alwaysArmed, r.Detail);
    }

    private bool IsExcluded(LogRecord r) {
        string source = r.Source;
        if (ExcludedSources.Contains(source) || ExcludedSourcePrefixes.Any(p => source.StartsWith(p, StringComparison.Ordinal))) {
            return true;
        }
        if (IsControllerSource(source)) {
            return true;
        }
        // Loading a sequence, template or target (Sequence*CreationConverter.cs, JsonCreationConverter.cs)
        if (source.EndsWith("CreationConverter.cs", StringComparison.Ordinal)) {
            return true;
        }
        // A cancellation (the user stopped an autofocus or the sequence) is no fault
        if (IsCancellation(r.Message) || IsCancellation(r.Detail)) {
            return true;
        }
        string text = r.Message.Length > 0 ? r.Message : r.Detail;
        return ExcludedLines.Any(e => e.Source == source && (e.Member.Length == 0 || e.Member == r.Member) &&
                                      (e.Message.Length == 0 || text.Contains(e.Message, StringComparison.OrdinalIgnoreCase)));
    }

    // A timeout can surface as TaskCanceledException too (HttpClient): that one is a fault
    private static bool IsCancellation(string text) {
        bool cancelled = text.Contains("OperationCanceledException", StringComparison.Ordinal) ||
                         text.Contains("TaskCanceledException", StringComparison.Ordinal);
        return cancelled && !text.Contains("timeout", StringComparison.OrdinalIgnoreCase) &&
               !text.Contains("timed out", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsControllerSource(string source) {
        if (!source.EndsWith(".cs", StringComparison.Ordinal)) {
            return false;
        }
        string name = source[..^3];
        if (controllerTypeNames.Contains(name)) {
            return true;
        }
        // Partial files: InternalGuiderController.Coach.cs
        int dot = name.IndexOf('.');
        return dot > 0 && controllerTypeNames.Contains(name[..dot]);
    }

    private static AlarmCategory Recategorize(LogRecord r, string message) {
        string s = r.Source;
        if (s == "SequenceTrigger.cs" && message.Contains("MeridianFlipTrigger", StringComparison.Ordinal)) {
            // "Failed: Trigger: (Programmable)MeridianFlipTrigger - <localized reason>"
            return AlarmCategory.Mount;
        }
        if (s == "MeridianFlipVM.cs" || s == "MountVM.cs" || s.EndsWith("Telescope.cs", StringComparison.Ordinal)) {
            return AlarmCategory.Mount;
        }
        if (s == "PHD2Guider.cs" || s == "GuiderVM.cs" || s.StartsWith("Guider", StringComparison.Ordinal)) {
            return AlarmCategory.Guiding;
        }
        if (s.StartsWith("SafetyMonitor", StringComparison.Ordinal)) {
            return AlarmCategory.Safety;
        }
        if (s.StartsWith("PowerBox", StringComparison.Ordinal) || message.StartsWith("PowerBox SDK:", StringComparison.Ordinal)) {
            return AlarmCategory.PowerBox;
        }
        if (message.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)) {
            return AlarmCategory.Disk;
        }
        return AlarmCategory.Errors;
    }

    // PowerBox over current and dew alarms are armed without a sequence too (user decision)
    private static bool IsAlwaysArmedPowerBox(string message) {
        return message.Contains("over current", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Dew port", StringComparison.Ordinal);
    }

    /// <summary>
    /// "source|member|message" with digit runs replaced by '#' (so a value in the message does not make another
    /// alarm), whitespace collapsed, at most 160 characters. For PowerBox messages the digits of the port name
    /// (before the first ':' after a "PowerBox SDK:" prefix) stay: "Dew port 1" and "Dew port 2" are different
    /// alarms, a measured duty or current is not.
    /// </summary>
    public static string NormalizeKey(string source, string member, string message, bool keepDigits = false) {
        var sb = new StringBuilder(source.Length + member.Length + message.Length + 2);
        sb.Append(source).Append('|').Append(member).Append('|');
        int keepUntil = 0;
        if (keepDigits) {
            const string Prefix = "PowerBox SDK:";
            int from = message.StartsWith(Prefix, StringComparison.Ordinal) ? Prefix.Length : 0;
            int colon = message.IndexOf(':', from);
            keepUntil = colon < 0 ? 0 : colon;
        }
        bool lastWasSpace = false, lastWasDigit = false;
        for (int i = 0; i < message.Length; i++) {
            char c = message[i];
            keepDigits = i < keepUntil;
            if (char.IsWhiteSpace(c)) {
                if (!lastWasSpace) sb.Append(' ');
                lastWasSpace = true;
                lastWasDigit = false;
                continue;
            }
            lastWasSpace = false;
            if (!keepDigits && char.IsDigit(c)) {
                if (!lastWasDigit) sb.Append('#');
                lastWasDigit = true;
                continue;
            }
            lastWasDigit = false;
            sb.Append(c);
        }
        string key = sb.ToString().TrimEnd();
        return key.Length > MaxKeyLength ? key[..MaxKeyLength] : key;
    }
}
