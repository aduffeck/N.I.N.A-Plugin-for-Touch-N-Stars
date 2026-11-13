using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using TouchNStars.Server.Infrastructure;
using TouchNStars.Utility;

namespace TouchNStars.Server.Services;

internal static class BackgroundWorker {
    private static FileSystemWatcher watcher;
    private static FileSystemWatcher afWatcher;

    // The log this process writes, followed by byte offset rather than re-read per change. pins puts
    // the process id into the log's name (<date>-<version>.<pid>-<yyyyMM>.log) and keeps it when it
    // renames the log after a clock correction or starts a new one (month, size limit), so the
    // directory is watched and the own log is told apart by that id. Guarded by logLock.
    private static readonly object logLock = new();
    private static string logPath;
    private static long logOffset;
    private static byte[] partialLine = Array.Empty<byte>();

    private static string OwnLogMarker => $".{Environment.ProcessId}-";

    public static void MonitorLogForEvents() {
        if (watcher != null) return;  // Prevent multiple instances

        try {
            lock (logLock) {
                // Start at the end: only what is logged from now on counts. A restart after a
                // watcher error keeps its place.
                if (logPath == null) {
                    logPath = FindOwnLog();
                    logOffset = logPath != null ? new FileInfo(logPath).Length : 0;
                    partialLine = Array.Empty<byte>();
                }
            }

            watcher = new FileSystemWatcher(CoreUtility.LogPath, "*.log");
            watcher.Changed += OnLogFileChanged;
            watcher.Created += OnLogFileChanged;
            watcher.Renamed += OnLogFileRenamed;
            watcher.Error += OnLogWatcherError;
            watcher.EnableRaisingEvents = true;
        } catch (Exception ex) {
            Logger.Error($"Failed to start log monitoring: {ex.Message}");
        }
    }

    private static string FindOwnLog() {
        return Directory.GetFiles(CoreUtility.LogPath, "*.log")
            .Where(f => Path.GetFileName(f).Contains(OwnLogMarker, StringComparison.Ordinal))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static void OnLogWatcherError(object sender, ErrorEventArgs e) {
        Logger.Error($"Log FileSystemWatcher failed, restarting: {e.GetException()?.Message}");
        StopLogWatcher();
        MonitorLogForEvents();
    }

    private static void StopLogWatcher() {
        if (watcher != null) {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnLogFileChanged;
            watcher.Created -= OnLogFileChanged;
            watcher.Renamed -= OnLogFileRenamed;
            watcher.Error -= OnLogWatcherError;
            watcher.Dispose();
            watcher = null;
        }
    }

    private static void OnLogFileRenamed(object sender, RenamedEventArgs e) {
        bool followed;
        lock (logLock) {
            // pins renamed its log after a clock correction: the same file, so keep the place.
            // It renames the session's earlier logs too; those are not followed and are ignored.
            followed = logPath != null && string.Equals(e.OldFullPath, logPath, StringComparison.Ordinal);
            if (followed) {
                logPath = e.FullPath;
            }
        }
        if (followed) {
            ProcessNewLines(e.FullPath);
        }
    }

    private static void OnLogFileChanged(object sender, FileSystemEventArgs e) {
        ProcessNewLines(e.FullPath);
    }

    private static void ProcessNewLines(string changedPath) {
        if (!Path.GetFileName(changedPath).Contains(OwnLogMarker, StringComparison.Ordinal)) return;

        try {
            lock (logLock) {
                if (!string.Equals(changedPath, logPath, StringComparison.Ordinal)) {
                    if (logPath != null && File.Exists(logPath)) {
                        // A new log (month or size limit): finish the old one, then read the new one from its start
                        ProcessLines(ReadNewLinesLocked());
                        logOffset = 0;
                        partialLine = Array.Empty<byte>();
                    }
                    // Otherwise the followed log was renamed without an event seen for it; the
                    // place is kept, and a file shorter than it is read from the start below.
                    logPath = changedPath;
                }

                ProcessLines(ReadNewLinesLocked());
            }
        } catch (Exception ex) {
            Logger.Error($"Error processing log file: {ex.Message}");
        }
    }

    // Reads what was appended to logPath since the last call. Callers hold logLock.
    private static List<string> ReadNewLinesLocked() {
        var lines = new List<string>();
        if (logPath == null || !File.Exists(logPath)) return lines;

        byte[] added;
        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
            long length = fs.Length;
            if (length < logOffset) {
                // Not the file the place belongs to
                logOffset = 0;
                partialLine = Array.Empty<byte>();
            }
            if (length == logOffset) return lines;

            fs.Seek(logOffset, SeekOrigin.Begin);
            added = new byte[length - logOffset];
            int read = fs.ReadAtLeast(added, added.Length, throwOnEndOfStream: false);
            if (read < added.Length) Array.Resize(ref added, read);
            logOffset += read;
        }

        byte[] data = partialLine.Length == 0 ? added : partialLine.Concat(added).ToArray();
        int lastNewline = Array.LastIndexOf(data, (byte)'\n');
        if (lastNewline < 0) {
            // No complete line yet; keep it for the next change
            partialLine = data;
            return lines;
        }

        partialLine = data[(lastNewline + 1)..];
        lines.AddRange(Encoding.UTF8.GetString(data, 0, lastNewline).Split('\n'));
        return lines;
    }

    /// <summary>
    /// Reads what the watcher may have missed (a lost inotify event); called by the alarm service's poll, never
    /// under its locks.
    /// </summary>
    public static void CatchUp() {
        string path;
        lock (logLock) {
            path = logPath;
        }
        if (path != null) {
            ProcessNewLines(path);
        }
    }

    private static void ProcessLines(List<string> lines) {
        // Runs under logLock: the alarm service only takes the lines over
        Alarm.PinsAlarmService.Instance.EnqueueLogLines(lines);

        foreach (string line in lines) {
            // "|StartAutoFocus|" catches NINA's built-in AutoFocus, which logs its failures from
            // that method. HocusFocus does not go through it — it fails in RunAutoFocus and then
            // announces it from PerformPostAutoFocusActions — so match its message text as well,
            // or a failed HocusFocus run looks exactly like a successful one to every consumer of
            // DataContainer.afError. Matching the text rather than the method name keeps this
            // working when line numbers move.
            if ((line.Contains("|ERROR|") || line.Contains("|WARNING|")) &&
                (line.Contains("|StartAutoFocus|") || line.Contains("AutoFocus did not complete successfully"))) {
                DataContainer.afRun = false;
                DataContainer.afError = true;

                string[] parts = line.Split('|');
                if (parts.Length >= 6) {
                    DataContainer.afErrorText = parts[5].Trim();
                }
            } else if (line.Contains("|INFO|") && line.Contains("Starting AutoFocus")) {
                DataContainer.afRun = true;
            }
        }
    }

    public static void Cleanup() {
        StopLogWatcher();
        lock (logLock) {
            logPath = null;
            logOffset = 0;
            partialLine = Array.Empty<byte>();
        }

        if (afWatcher != null) {
            afWatcher.EnableRaisingEvents = false;
            afWatcher.Changed -= OnAFFileChanged;
            afWatcher.Error -= OnAfWatcherError;
            afWatcher.Dispose();
            afWatcher = null;
        }
    }
    public static void MonitorLastAF() {
        if (afWatcher != null) {
            Logger.Info("MonitorLastAF is already running. Skipping re-initialization.");
            return;
        }

        try {
            if (!Directory.Exists(CoreUtility.AfPath)) {
                Logger.Info($"AF-Verzeichnis existiert nicht, wird erstellt: {CoreUtility.AfPath}");
                Directory.CreateDirectory(CoreUtility.AfPath);
            }

            afWatcher = new FileSystemWatcher(CoreUtility.AfPath);
            afWatcher.EnableRaisingEvents = true;
            afWatcher.Created += OnAFFileChanged;
            afWatcher.Error += OnAfWatcherError;
        } catch (Exception ex) {
            Logger.Error($"Failed to start AF report monitoring: {ex.Message}");
        }
    }

    private static void OnAfWatcherError(object sender, ErrorEventArgs e) {
        Logger.Error($"AF FileSystemWatcher failed, restarting: {e.GetException()?.Message}");
        if (afWatcher != null) {
            afWatcher.EnableRaisingEvents = false;
            afWatcher.Created -= OnAFFileChanged;
            afWatcher.Error -= OnAfWatcherError;
            afWatcher.Dispose();
            afWatcher = null;
        }
        MonitorLastAF();
    }

    private static void OnAFFileChanged(object sender, FileSystemEventArgs e) {
        if (e == null || string.IsNullOrEmpty(e.FullPath)) {
            Logger.Error("Invalid file system event received");
            return;
        }
        if (afWatcher == null) {
            Logger.Error("afWatcher is null, skipping event processing.");
            return;
        }
        if (e.ChangeType == WatcherChangeTypes.Created && e.FullPath.EndsWith(".json")) {
            Logger.Info("Found new AF report");
            DataContainer.afRun = false;
            DataContainer.newAfGraph = true;
        }
    }
}