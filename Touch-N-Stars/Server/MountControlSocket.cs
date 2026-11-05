using EmbedIO.WebSockets;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.INDI;
using NINA.INDI.Devices;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using TouchNStars.Server.Models;
using TouchNStars.Server.Services;

namespace TouchNStars.Server;

/// <summary>
/// WebSocket for manual (press-and-hold) mount slewing, served INDI-direct from the
/// Touch-N-Stars plugin so it can drive the mount by direction only. The slew rate is set
/// separately through POST /api/indi/mount/slew-rate (the capability model); this socket never
/// touches the rate, which keeps a chosen discrete step (e.g. "Guide") from being overwritten on
/// every keepalive message. pins' native OnStepX mount driver is moved the same way, at its
/// selected move rate. Any other mount NINA has connected (e.g. ASCOM Alpaca) is moved through
/// NINA's telescope mediator at the rate selected for it (see <see cref="GenericMount"/>).
///
/// Protocol: the client sends { "direction": "north|south|east|west|stop" } on press and repeats
/// it as a keepalive (~every 800 ms); it sends "stop" on release. A server-side dead-man watchdog
/// stops all motion if no message arrives within the timeout, so a dropped connection or dead tab
/// cannot leave the mount slewing.
/// </summary>
public class MountControlSocket : WebSocketModule
{
    // ~2.5 keepalives of the client
    private static readonly TimeSpan DeadManTimeout = TimeSpan.FromSeconds(2);

    // also serializes the device calls, so a dead-man stop and a fresh move reach the mount in order
    private static readonly object _lock = new object();

    // counts the move and stop messages; a scheduled dead-man stop only acts if no message came after its own
    private static long _messageCount;
    private static string _lastDirection = string.Empty;

    /// <summary>
    /// Moves an axis in the direction of a sign (0 stops it). <paramref name="Resends"/>: the move is repeated on
    /// every keepalive, which INDI and OnStepX take cheaply; a mediator move is a blocking driver call (several HTTP
    /// requests on Alpaca) and is sent only when the direction changes. <paramref name="Refusal"/>: why an axis
    /// can't move now, checked before each move; null when the driver reports that itself.
    /// </summary>
    private sealed record Mover(Action<TelescopeAxes, int> Move, bool Resends, Func<TelescopeAxes, string> Refusal);

    public MountControlSocket(string urlPath) : base(urlPath, true)
    {
    }

    protected override async Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
    {
        ApiResponse response;
        try
        {
            var message = Encoding.GetString(buffer);
            var json = JsonSerializer.Deserialize<Dictionary<string, object>>(message);
            string direction = json != null && json.TryGetValue("direction", out var d) ? d?.ToString()?.ToLowerInvariant() : null;

            var mover = MountMover();
            if (mover == null)
            {
                response = Error("No mount is currently connected");
            }
            else
            {
                response = HandleDirection(mover, direction);
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"MountControlSocket error: {ex}");
            response = Error(ex.Message);
        }

        await context.WebSocket.SendAsync(Encoding.GetBytes(JsonSerializer.Serialize(response)), true);
    }

    /// <summary>
    /// Direction-only motion of the connected mount: pins' native OnStepX driver when NINA uses it,
    /// otherwise the INDI mount, otherwise any mount connected in NINA; null when none is connected.
    /// </summary>
    private static Mover MountMover()
    {
        if (TouchNStars.Mediators?.Telescope?.GetDevice() is OnStepXTelescope { Connected: true } onStepX)
        {
            return new Mover(Reversed(onStepX.MoveAxisDirection), true, null);
        }

        var mount = INDIClient.Instance.GetRegisteredDevice<INDITelescope>();
        if (mount != null)
        {
            return new Mover(Reversed(mount.MoveAxisDirection), true, null);
        }

        var generic = GenericMount.Connected();
        if (generic != null)
        {
            // the mediator applies the profile's axis reversal itself
            return new Mover((axis, sign) => GenericMount.Move(generic, axis, sign), false, GenericMount.Refusal);
        }

        return null;
    }

    /// <summary>
    /// Applies the profile's "primary/secondary reversed", which NINA applies in TelescopeVM.MoveAxis; the INDI and
    /// OnStepX movers bypass it.
    /// </summary>
    private static Action<TelescopeAxes, int> Reversed(Action<TelescopeAxes, int> move)
    {
        return (axis, sign) =>
        {
            var settings = TouchNStars.Mediators?.Profile?.ActiveProfile?.TelescopeSettings;
            bool reversed = axis == TelescopeAxes.Primary
                ? settings?.PrimaryReversed == true
                : settings?.SecondaryReversed == true;
            move(axis, reversed ? -sign : sign);
        };
    }

    private static ApiResponse HandleDirection(Mover mover, string direction)
    {
        TelescopeAxes axis;
        int sign;
        switch (direction)
        {
            case "north":
                (axis, sign) = (TelescopeAxes.Secondary, 1);
                break;
            case "south":
                (axis, sign) = (TelescopeAxes.Secondary, -1);
                break;
            case "east":
                (axis, sign) = (TelescopeAxes.Primary, 1);
                break;
            case "west":
                (axis, sign) = (TelescopeAxes.Primary, -1);
                break;
            case "stop":
                lock (_lock)
                {
                    // a pending dead-man stop has nothing left to do
                    _messageCount++;
                    _lastDirection = string.Empty;
                    StopAll(mover);
                }
                return new ApiResponse { Success = true, Response = "Stopped Move", StatusCode = 200, Type = "MountControl" };
            default:
                return Error("Invalid direction");
        }

        var refusal = mover.Refusal?.Invoke(axis);
        if (refusal != null)
        {
            return Error(refusal);
        }

        long message;
        lock (_lock)
        {
            if (mover.Resends || _lastDirection != direction)
            {
                mover.Move(axis, sign);
            }

            message = ++_messageCount;
            _lastDirection = direction;
        }
        ScheduleDeadManStop(mover, message);

        return new ApiResponse { Success = true, Response = "Moving", StatusCode = 200, Type = "MountControl" };
    }

    /// <summary>
    /// Stops all motion if no message arrived within <see cref="DeadManTimeout"/> after move message
    /// number <paramref name="message"/>. The count is the token, not the wall clock: the app sets the
    /// system time of the Pi, and a clock set back during a slew made the elapsed time negative, so the
    /// stop was skipped and the mount kept moving. Task.Delay itself runs on the monotonic clock.
    /// </summary>
    private static void ScheduleDeadManStop(Mover mover, long message)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(DeadManTimeout);
            try
            {
                lock (_lock)
                {
                    if (_messageCount != message)
                    {
                        return; // a newer message superseded this one
                    }

                    // cleared first, so that a failed stop doesn't keep the next move from being sent
                    _lastDirection = string.Empty;
                    StopAll(mover);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"MountControlSocket dead-man stop failed: {ex}");
            }
        });
    }

    private static void StopAll(Mover mover)
    {
        mover.Move(TelescopeAxes.Primary, 0);
        mover.Move(TelescopeAxes.Secondary, 0);
    }

    private static ApiResponse Error(string error)
        => new ApiResponse { Success = false, Error = error, StatusCode = 400, Type = "Error" };
}
