// SPDX-License-Identifier: MPL-2.0
// Ported from NINA.Mcp/Observatory/DeviceConnectionTracker.cs (pins), on the monotonic alarm clock.
#nullable enable
using System;
using System.Collections.Generic;

namespace TouchNStars.Server.Services.Alarm;

/// <summary>A connection change judged by <see cref="DeviceConnectionTracker"/>; <paramref name="Lost"/>: not asked for.</summary>
public sealed record ConnectionChange(bool Lost, string Message, bool Connected);

/// <summary>
/// Tells requested disconnects from lost connections for one device. The state comes from polling the device info; the
/// VM's Disconnected event (<see cref="DisconnectRequested"/>) marks a disconnect as asked for, and
/// <see cref="ConnectionDropped"/> marks one the VM did by itself because the device went away (the guider VM calls its
/// own Disconnect then, which raises Disconnected as well). Thread-safe: the events come from mediator threads.
/// </summary>
/// <remarks>
/// A VM marks its info disconnected before it raises Disconnected (a driver disconnect can take seconds in between), so a
/// disconnect seen without a request is only judged one poll later. Disconnected events while the device is not
/// connected are ignored: several VMs call Disconnect at the start of every connect.
/// </remarks>
public sealed class DeviceConnectionTracker {
    /// <summary>A connection that ends within this time after a Disconnected event was asked for, not lost.</summary>
    public const long ExpectedDisconnectWindowMs = 15_000;

    /// <summary>The VM's own Disconnected event follows a drop at once; within this time it does not count as a request.</summary>
    public const long DropEventGraceMs = 5_000;

    private const long Never = long.MinValue / 2;

    private readonly object gate = new();
    private bool wasConnected;
    private long requestedAt = Never;
    private long droppedAt = Never;
    private long? pendingLossSince;

    public DeviceConnectionTracker(string device, bool connected) {
        ArgumentException.ThrowIfNullOrEmpty(device);
        Device = device;
        wasConnected = connected;
    }

    public string Device { get; }

    public bool WasConnected { get { lock (gate) return wasConnected; } }

    /// <summary>The VM raised Disconnected (asked for). Ignored while not connected at the last poll (unless a loss is pending) and right after a drop.</summary>
    public void DisconnectRequested(long nowMs) {
        lock (gate) {
            if ((!wasConnected && pendingLossSince is null) || nowMs - droppedAt <= DropEventGraceMs) {
                return;
            }
            requestedAt = nowMs;
        }
    }

    /// <summary>
    /// A PowerBox port that powers devices was switched off on purpose: a disconnect now counts as asked for, also one
    /// the VM saw as a drop (the guider VM reports a device that lost its power as dropped).
    /// </summary>
    public void PortSwitchedOff(long nowMs) {
        lock (gate) {
            requestedAt = nowMs;
            droppedAt = Never;
        }
    }

    /// <summary>The device went away by itself; the disconnect that follows is a lost connection.</summary>
    public void ConnectionDropped(long nowMs) {
        lock (gate) {
            droppedAt = nowMs;
            requestedAt = Never;
        }
    }

    /// <summary>Compares <paramref name="connected"/> with the last poll; the changes, oldest first.</summary>
    public IReadOnlyList<ConnectionChange> Poll(bool connected, long nowMs) {
        var changes = new List<ConnectionChange>();
        lock (gate) {
            if (pendingLossSince is { } since && !connected) {
                pendingLossSince = null;
                changes.Add(requestedAt >= since - ExpectedDisconnectWindowMs ? Disconnected() : Lost());
            }

            if (connected == wasConnected) {
                return changes;
            }

            wasConnected = connected;

            if (connected) {
                pendingLossSince = null;
                droppedAt = Never;
                changes.Add(new ConnectionChange(false, $"{Device} connected.", true));
            } else if (nowMs - droppedAt <= ExpectedDisconnectWindowMs) {
                changes.Add(Lost());
            } else if (nowMs - requestedAt <= ExpectedDisconnectWindowMs) {
                changes.Add(Disconnected());
            } else {
                pendingLossSince = nowMs;
            }
        }
        return changes;
    }

    private ConnectionChange Disconnected() => new(false, $"{Device} disconnected.", false);

    private ConnectionChange Lost() =>
        new(true, $"{Device} lost its connection (not disconnected by request): check cable, power and driver.", false);
}
