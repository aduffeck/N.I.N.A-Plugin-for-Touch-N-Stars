#nullable enable
using NINA.Core.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TouchNStars.Server.Services.Alarm;

namespace TouchNStars.Server.Services;

/// <summary>State of one PowerBox port; <paramref name="PortKey"/> e.g. "dew|1".</summary>
public sealed record PowerBoxPortState(string PortKey, string Label, bool Enabled, bool Overcurrent);

/// <summary>
/// The PINS PowerBox driver, reached by reflection (no compile-time dependency on the PINS plugin, which loads in an
/// AssemblyLoadContext of its own and possibly after TNS).
/// </summary>
public static class PinsPowerBoxAccess {
    private static Type? pinsType;

    private static Type? PinsType => pinsType ??= Type.GetType("NINA.PINS.PINS, NINA.PINS")
        ?? AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "NINA.PINS")
            .Select(a => a.GetType("NINA.PINS.PINS"))
            .FirstOrDefault(t => t != null);

    /// <summary>The connected PowerBox driver, or null.</summary>
    public static object? GetConnectedPowerBox() {
        try {
            var powerBox = PinsType?.GetProperty("ConnectedPowerBox", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return powerBox != null && Get(powerBox, "Connected") is true ? powerBox : null;
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>
    /// Starts a tone (PowerBoxDriver.Beep returns at once). An older driver without a bool result counts as started.
    /// Failures are logged by the driver as warnings, never as errors (errors raise alarms).
    /// </summary>
    public static BeepResult TryBeep(int volume, int durationMs) {
        var powerBox = GetConnectedPowerBox();
        if (powerBox == null) {
            return BeepResult.NoPowerBox;
        }
        var method = powerBox.GetType().GetMethod("Beep", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int), typeof(int) }, null);
        if (method == null) {
            return BeepResult.NotSupported;
        }
        try {
            object? result = method.Invoke(powerBox, new object[] { volume, durationMs });
            return method.ReturnType != typeof(bool) || result is true ? BeepResult.Ok : BeepResult.Failed;
        } catch (Exception ex) {
            Logger.Warning($"PowerBox beep failed: {ex.InnerException?.Message ?? ex.Message}");
            return BeepResult.Failed;
        }
    }

    /// <summary>The state of all ports the box reports; empty without a PowerBox.</summary>
    public static IReadOnlyList<PowerBoxPortState> ReadPorts() {
        var flags = new List<PowerBoxPortState>();
        var powerBox = GetConnectedPowerBox();
        if (powerBox == null) {
            return flags;
        }
        try {
            Add(flags, powerBox, "PowerPorts", "ActualPowerPortCount", "12v", "12 V port");
            Add(flags, powerBox, "USBPorts", "ActualUSBPortCount", "usb", "USB port");
            Add(flags, powerBox, "DewPorts", "ActualDewPortCount", "dew", "Dew port");
            Add(flags, powerBox, "BuckPorts", "ActualBuckPortCount", "buck", "Adjustable port");
            Add(flags, powerBox, "PWMPorts", "ActualPWMPortCount", "pwm", "PWM port");
        } catch (Exception) {
            // A driver of another version: no flags this poll
        }
        return flags;
    }

    private static void Add(List<PowerBoxPortState> flags, object powerBox, string group, string countProperty, string key, string label) {
        if (Get(Get(powerBox, group), "Ports") is not IEnumerable ports) {
            return;
        }
        int count = Get(powerBox, countProperty) is int c ? c : int.MaxValue;
        int n = 0;
        foreach (var port in ports) {
            if (port == null || n++ >= count) {
                continue;
            }
            int index = Get(port, "Index") is int i ? i : n - 1;
            string name = Get(port, "Name") as string ?? "";
            string text = string.IsNullOrWhiteSpace(name) ? $"{label} {index + 1}" : $"{label} {index + 1} ({name})";
            flags.Add(new PowerBoxPortState($"{key}|{index + 1}", text, Get(port, "Enabled") is true, Get(port, "Overcurrent") is true));
        }
    }

    private static object? Get(object? obj, string property) {
        return obj?.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
    }
}

/// <summary>The buzzer of the connected PINS PowerBox.</summary>
public sealed class PinsBuzzer : IBuzzer {
    public BeepResult Beep(int volume, int durationMs) => PinsPowerBoxAccess.TryBeep(volume, durationMs);
}
