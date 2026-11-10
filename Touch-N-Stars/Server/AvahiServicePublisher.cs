using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using NINA.Core.Utility;

namespace TouchNStars.Server;

// Registers with the system responder over D-Bus via Avahi's unprivileged client.
// Never publishes host records or opens an mDNS socket itself.
//
// avahi-publish-service keeps running through a remote name collision (it picks "name #2") and, with --no-fail,
// through a daemon restart. It exits on a local collision (another avahi-publish-service with the same name, e.g.
// left by an earlier NINA) and on a client failure, and keeps running without publishing after "Failed to register".
// Every end that Stop() did not cause is restarted with a backoff, whatever the exit code (a signal exits with 0).
internal sealed class AvahiServicePublisher
{
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);
    // a publisher that ran this long counts as healthy: the next failure starts the backoff over
    private static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(2);
    private const int KeptErrorLines = 5;

    private readonly object gate = new();
    private readonly Queue<string> recentErrors = new();
    private Process publisher;
    private string[] currentArguments;
    private ProcessStartInfo currentInfo;
    private DateTime startedAt;
    private TimeSpan retryDelay = FirstRetry;
    private Timer restartTimer;
    // bumped by every start and Stop(): events of an older process are ignored
    private int generation;

    internal static ProcessStartInfo BuildStartInfo(string name, string type, int port,
        IReadOnlyDictionary<string, string> properties)
    {
        var info = new ProcessStartInfo("/usr/bin/avahi-publish-service")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Wait for Avahi at boot and reconnect after daemon restarts.
        info.ArgumentList.Add("--no-fail");
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(type.TrimEnd('.'));
        info.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        // ServiceProfile supplied this DNS-SD TXT version on the previous path.
        if (properties == null || !properties.ContainsKey("txtvers"))
            info.ArgumentList.Add("txtvers=1");
        if (properties != null)
        {
            foreach (var pair in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrEmpty(pair.Value))
                    info.ArgumentList.Add($"{pair.Key}={pair.Value}");
            }
        }
        return info;
    }

    public void StartOrUpdate(string name, string type, int port,
        IReadOnlyDictionary<string, string> properties)
    {
        var info = BuildStartInfo(name, type, port, properties);
        var arguments = info.ArgumentList.ToArray();
        Process replaced;
        lock (gate)
        {
            if (currentInfo != null && arguments.SequenceEqual(currentArguments)
                && ((publisher != null && !publisher.HasExited) || restartTimer != null))
                return;

            replaced = DetachLocked();
            currentInfo = info;
            currentArguments = arguments;
            retryDelay = FirstRetry;
        }

        Terminate(replaced);

        lock (gate)
        {
            // a Stop() or another update came in between
            if (!ReferenceEquals(currentInfo, info) || publisher != null)
                return;

            try
            {
                StartLocked();
            }
            catch
            {
                currentInfo = null;
                currentArguments = null;
                Logger.Warning("Linux discovery requires avahi-daemon and avahi-utils. No in-process mDNS responder will be started.");
                throw;
            }
        }
    }

    public void Stop()
    {
        Process process;
        lock (gate)
        {
            process = DetachLocked();
            currentInfo = null;
            currentArguments = null;
        }

        Terminate(process);
    }

    // Caller holds gate. Forgets the running publisher and a pending restart; the process is ended by Terminate.
    private Process DetachLocked()
    {
        generation++;
        restartTimer?.Dispose();
        restartTimer = null;
        var process = publisher;
        publisher = null;
        return process;
    }

    private static void Terminate(Process process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to stop Avahi publisher: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }

    // Caller holds gate.
    private void StartLocked()
    {
        int myGeneration = ++generation;
        recentErrors.Clear();
        var process = new Process { StartInfo = currentInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) => OnErrorLine(process, myGeneration, e.Data);
        process.Exited += (_, _) => OnExited(process, myGeneration);
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        publisher = process;
        startedAt = DateTime.UtcNow;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    private void OnErrorLine(Process process, int processGeneration, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        Logger.Debug($"Avahi publisher: {line}");

        bool notPublishing = line.Contains("Failed to register", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Client failure", StringComparison.OrdinalIgnoreCase);
        lock (gate)
        {
            if (processGeneration != generation) return;
            recentErrors.Enqueue(line);
            while (recentErrors.Count > KeptErrorLines) recentErrors.Dequeue();
        }

        if (notPublishing)
        {
            // it keeps running without publishing: ended, so OnExited restarts it
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception ex)
            {
                Logger.Debug($"Avahi publisher: could not end it after \"{line}\": {ex.Message}");
            }
        }
    }

    private void OnExited(Process process, int processGeneration)
    {
        try
        {
            // the last stderr lines may still be on their way
            process.WaitForExit();
        }
        catch
        {
            // disposed by Stop meanwhile
        }

        lock (gate)
        {
            if (processGeneration != generation || !ReferenceEquals(process, publisher))
                return;

            string exitCode;
            try
            {
                exitCode = process.ExitCode.ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                exitCode = "?";
            }

            if (DateTime.UtcNow - startedAt >= HealthyRun)
                retryDelay = FirstRetry;

            var delay = retryDelay;
            retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetry.Ticks));

            string errors = recentErrors.Count > 0 ? $": {string.Join(" | ", recentErrors)}" : string.Empty;
            Logger.Warning($"Avahi service publisher exited (code {exitCode}){errors}. Discovery is off until it is restarted in "
                + $"{delay.TotalSeconds:F0} s. An exit at once usually means another avahi-publish-service publishes the same name "
                + "(e.g. left by an earlier NINA).");

            publisher = null;
            process.Dispose();
            int restartGeneration = generation;
            restartTimer = new Timer(_ => Restart(restartGeneration), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Restart(int expectedGeneration)
    {
        lock (gate)
        {
            if (expectedGeneration != generation || publisher != null || currentInfo == null)
                return;

            restartTimer?.Dispose();
            restartTimer = null;
            try
            {
                StartLocked();
                Logger.Info("Avahi service publisher restarted.");
            }
            catch (Exception ex)
            {
                var delay = retryDelay;
                retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetry.Ticks));
                Logger.Warning($"Restarting the Avahi service publisher failed: {ex.Message}; next try in {delay.TotalSeconds:F0} s");
                int restartGeneration = generation;
                restartTimer = new Timer(_ => Restart(restartGeneration), null, delay, Timeout.InfiniteTimeSpan);
            }
        }
    }
}
