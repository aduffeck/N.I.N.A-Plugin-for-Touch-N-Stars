using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;

namespace TouchNStars.Tests;

/// <summary>Interface fake without a mocking library: every call goes to a handler.</summary>
public class InterfaceFake : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        object? result = Handler?.Invoke(targetMethod!, args);
        return result ?? DefaultFor(targetMethod!.ReturnType);
    }

    public static T Create<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        T proxy = DispatchProxy.Create<T, InterfaceFake>();
        ((InterfaceFake)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? DefaultFor(Type type)
    {
        if (type == typeof(void)) return null;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type == typeof(Task<bool>)) return Task.FromResult(true);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
        {
            Type inner = type.GetGenericArguments()[0];
            MethodInfo fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner);
            return fromResult.Invoke(null, new[] { inner.IsValueType ? Activator.CreateInstance(inner) : null });
        }
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>The native guider with the optional Guiding Coach and flight recorder, as one interface for the fake.</summary>
public interface IFullNativeGuider : IGuidingCoach, IGuideIncidentRecorder
{
}

/// <summary>A minimal IAdvancedGuider backed by fields, plus a mediator that returns it.</summary>
public sealed class FakeNativeGuider
{
    private readonly List<EventHandler<AdvancedGuiderEventArgs>> handlers = new();

    /// <param name="optionalParts">Whether the guider also implements <see cref="IGuidingCoach"/> and <see cref="IGuideIncidentRecorder"/>.</param>
    public FakeNativeGuider(bool optionalParts = true)
    {
        Guider = optionalParts ? InterfaceFake.Create<IFullNativeGuider>(Handle) : InterfaceFake.Create<IAdvancedGuider>(Handle);
        Mediator = InterfaceFake.Create<IGuiderMediator>((method, _) => method.Name == nameof(IGuiderMediator.GetDevice) ? Guider : null);
    }

    public IAdvancedGuider Guider { get; }
    public IGuiderMediator Mediator { get; }
    public bool Connected { get; set; } = true;
    public string State { get; set; } = "Looping";
    public AdvancedGuiderFrame? LatestFrame { get; set; }
    public int SubscriberCount { get { lock (handlers) return handlers.Count; } }

    public AdvancedCoachStatus? CoachStatus { get; set; } = new() { Phase = "Idle", GainMin = 0, GainMax = 400, CurrentGain = 120, CurrentExposureSeconds = 2 };
    public List<AdvancedCoachReport>? CoachHistory { get; set; } = new();
    public List<AdvancedCoachFinding> Hints { get; set; } = new();
    public bool CoachRunning { get; set; }

    /// <summary>Stored incidents of the flight recorder, newest first; the list hands out their summaries.</summary>
    public List<AdvancedIncident> Incidents { get; } = new();
    public bool IncidentsEnabled { get; set; } = true;
    public string? RecordingIncidentId { get; set; }

    /// <summary>Stored incident images by (id, kind, frame) and star crops by (id, frame).</summary>
    public Dictionary<(string Id, string Kind, long Frame), AdvancedIncidentImage> IncidentImages { get; } = new();
    public Dictionary<(string Id, long Frame), List<AdvancedIncidentImage>> IncidentCrops { get; } = new();

    /// <summary>What MarkIncident answers: the id, or null with <see cref="MarkError"/>.</summary>
    public string? MarkId { get; set; } = "20260926-021300-Manual";
    public string? MarkError { get; set; }

    /// <summary>Files of the zip that WriteIncidentArchive writes for a known incident.</summary>
    public Dictionary<string, byte[]> ArchiveFiles { get; } = new() { ["README.txt"] = Encoding.UTF8.GetBytes("PINS incident") };

    /// <summary>Optional per-call behaviour (method name, args); a non-null result replaces the default.</summary>
    public Func<string, object?[]?, object?>? OnCall { get; set; }

    private readonly List<(string Name, object?[]? Args)> calls = new();

    /// <summary>Calls of the given guider method, in order.</summary>
    public List<object?[]?> CallsOf(string name)
    {
        lock (calls) return calls.Where(c => c.Name == name).Select(c => c.Args).ToList();
    }

    /// <summary>A mediator whose device is this guider and whose guider chooser has it selected (as when not connected yet).</summary>
    public IGuiderMediator ChooserMediator() => ChooserMediatorFake.Create(Guider, Guider);

    /// <summary>A mediator whose connected device is a plain (non-native) guider such as PHD2.</summary>
    public static IGuiderMediator PlainGuiderMediator()
    {
        IGuider plain = InterfaceFake.Create<IGuider>((method, _) => method.Name switch
        {
            "get_Connected" => true,
            "get_Id" => "PHD2_Single",
            "get_Name" => "PHD2",
            _ => null
        });
        return InterfaceFake.Create<IGuiderMediator>((method, _) => method.Name == nameof(IGuiderMediator.GetDevice) ? plain : null);
    }

    public void Raise(string type, object payload)
    {
        EventHandler<AdvancedGuiderEventArgs>[] snapshot;
        lock (handlers) snapshot = handlers.ToArray();
        var args = new AdvancedGuiderEventArgs { Type = type, Timestamp = DateTime.UtcNow, Payload = payload };
        foreach (var handler in snapshot) handler(Guider, args);
    }

    private object? Handle(MethodInfo method, object?[]? args)
    {
        lock (calls) calls.Add((method.Name, args));
        object? custom = OnCall?.Invoke(method.Name, args);
        if (custom != null) return custom;

        switch (method.Name)
        {
            case "GetCoachStatus": return CoachStatus;
            case "StartCoach": return Task.FromResult(new AdvancedCoachStartResult { Accepted = true, Status = CoachStatus! });
            case "GetCoachHistory": return CoachHistory?.Take((int)args![0]!).ToList();
            case "DismissHint":
                return Hints.RemoveAll(h => h.Id == (string?)args![0]) > 0;
            case "get_Connected": return Connected;
            case "get_Id": return "PinsNativeGuider";
            case "get_Name": return "PINS Native Guider";
            case "get_State": return State;
            case "GetLatestFrame": return LatestFrame;
            case "GetStatus": return new AdvancedGuiderStatus { State = State, Connected = Connected, Hints = Hints.ToList(), CoachRunning = CoachRunning };
            case "GetIncidents":
                lock (Incidents)
                {
                    return new AdvancedIncidentList
                    {
                        Incidents = Incidents.Select(Summary).ToList(),
                        Enabled = IncidentsEnabled,
                        RecordingId = RecordingIncidentId,
                        UsedBytes = Incidents.Sum(i => i.SizeBytes),
                        BudgetBytes = 1_000_000_000,
                        MaxIncidents = 50
                    };
                }
            case "GetIncident": return FindIncident((string)args![0]!);
            case "GetIncidentImage":
                return IncidentImages.GetValueOrDefault(((string)args![0]!, (string)args[1]!, (long)args[2]!));
            case "GetIncidentCrops":
                return IncidentCrops.TryGetValue(((string)args![0]!, (long)args[1]!), out List<AdvancedIncidentImage>? crops)
                    ? crops
                    : new List<AdvancedIncidentImage>();
            case "SetIncidentKept":
            {
                AdvancedIncident? incident = FindIncident((string)args![0]!);
                if (incident == null) return false;
                incident.Kept = (bool)args[1]!;
                return true;
            }
            case "DeleteIncident":
                lock (Incidents) return Incidents.RemoveAll(i => i.Id == (string)args![0]!) > 0;
            case "DeleteAllIncidents":
                lock (Incidents) return Incidents.RemoveAll(i => !i.Kept);
            case "MarkIncident":
                args![1] = MarkError;
                return MarkId;
            case "WriteIncidentArchive":
            {
                if (FindIncident((string)args![0]!) == null) return Task.FromResult(false);
                // Like the plugin: a zip written straight into the (non-seekable) response stream.
                using (var zip = new ZipArchive((Stream)args[1]!, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach ((string name, byte[] content) in ArchiveFiles)
                    {
                        using Stream entry = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                        entry.Write(content, 0, content.Length);
                    }
                }
                return Task.FromResult(true);
            }
            case "add_AdvancedGuiderEvent":
                lock (handlers) handlers.Add((EventHandler<AdvancedGuiderEventArgs>)args![0]!);
                return null;
            case "remove_AdvancedGuiderEvent":
                lock (handlers) handlers.Remove((EventHandler<AdvancedGuiderEventArgs>)args![0]!);
                return null;
            default:
                return null;
        }
    }

    private AdvancedIncident? FindIncident(string id)
    {
        lock (Incidents) return Incidents.FirstOrDefault(i => i.Id == id);
    }

    /// <summary>The plain summary of an incident, as the contract requires from GetIncidents and incident events.</summary>
    public static AdvancedIncidentSummary Summary(AdvancedIncident incident) => new()
    {
        Id = incident.Id,
        Start = incident.Start,
        End = incident.End,
        Kind = incident.Kind,
        Kinds = incident.Kinds.ToList(),
        Occurrences = incident.Occurrences,
        Ongoing = incident.Ongoing,
        EndReason = incident.EndReason,
        Kept = incident.Kept,
        SizeBytes = incident.SizeBytes,
        FrameCount = incident.FrameCount,
        FramesOmitted = incident.FramesOmitted,
        Note = incident.Note,
        Cause = incident.Cause,
        Tags = incident.Tags
    };

    /// <summary>An incident with three frames (10-12, a star loss at 11), triggers, markers and a diagnosis.</summary>
    public static AdvancedIncident Incident(string id, string kind = "StarLost", bool kept = false)
    {
        var start = new DateTime(2026, 9, 26, 2, 13, 0, DateTimeKind.Utc);
        return new AdvancedIncident
        {
            Id = id,
            Start = start,
            End = start.AddMinutes(3),
            Kind = kind,
            Kinds = { kind },
            Occurrences = 1,
            EndReason = "recovered",
            Kept = kept,
            SizeBytes = 1234,
            FrameCount = 3,
            Cause = "clouds",
            Tags = new AdvancedIncidentTags { ProfileName = "Default", GuideCamera = "Simulator", Simulator = true, PixelScale = 3.2 },
            Triggers = { new AdvancedIncidentTrigger { Time = start.AddSeconds(120), Kind = kind, Code = 100, CodeName = "StarLost", Frame = 11 } },
            Markers = { new AdvancedIncidentMarker { Time = start.AddSeconds(120), Frame = 11, Type = "trigger" } },
            Diagnosis = new AdvancedIncidentDiagnosis
            {
                Cause = "clouds",
                Message = "Likely clouds.",
                Parameters = { ["dropPercent"] = 80.0 },
                Evidence = { new AdvancedIncidentEvidence { Code = "starsFaded", Frame = 11, Parameters = { ["stars"] = 3 } } }
            },
            SensorWidth = 1936,
            SensorHeight = 1216,
            ContextBinning = 4,
            SearchRegionPx = 15,
            Frames =
            {
                new AdvancedIncidentFrame { Frame = 10, State = "Guiding", StarFound = true, Snr = 30, TotalArcsec = double.NaN, HasContext = true, Crops = 2 },
                new AdvancedIncidentFrame { Frame = 11, State = "LostLock", LostStatus = "LowSnr", HasContext = true, HasKey = true },
                new AdvancedIncidentFrame { Frame = 12, State = "Guiding", StarFound = true }
            }
        };
    }

    /// <summary>A stored incident image with a gradient.</summary>
    public static AdvancedIncidentImage IncidentImage(long frame, string kind, int width, int height, int binning, int x0 = 0, int y0 = 0, int star = 0)
    {
        return new AdvancedIncidentImage
        {
            Frame = frame,
            Kind = kind,
            Star = star,
            X0 = x0,
            Y0 = y0,
            Width = width,
            Height = height,
            Binning = binning,
            BitDepth = 16,
            Pixels = Enumerable.Range(0, width * height).Select(i => (ushort)(1000 + 10 * (i % width) + i / width)).ToArray()
        };
    }

    public static AdvancedGuiderFrame Frame(long number, int width = 8, int height = 6)
    {
        return new AdvancedGuiderFrame
        {
            FrameNumber = number,
            Timestamp = DateTime.UtcNow,
            Width = width,
            Height = height,
            BitDepth = 16,
            Pixels = Enumerable.Range(0, width * height).Select(i => (ushort)(1000 + i)).ToArray(),
            LockX = 3.5,
            LockY = 2.5,
            Stars = new List<AdvancedGuideStar>
            {
                new() { X = 4, Y = 3, Snr = 25, IsPrimary = true, Used = true, Weight = 1 },
                new() { X = 1, Y = 1, Snr = 4, Used = false, RejectReason = "LowSnr" }
            }
        };
    }
}

/// <summary>
/// A guider mediator with the private 'handler' field (NINA's GuiderVM) through which the service finds the
/// guider chooser, for the native guider while it is not connected.
/// </summary>
public class ChooserMediatorFake : InterfaceFake
{
    private object? handler;

    /// <summary>What the service reads by reflection: the GuiderVM stand-in with its DeviceChooserVM.</summary>
    public object? GuiderVM => handler;

    public sealed class FakeGuiderVM
    {
        public IDeviceChooserVM? DeviceChooserVM { get; set; }
    }

    public static IGuiderMediator Create(IDevice device, IDevice selected)
    {
        IGuiderMediator proxy = DispatchProxy.Create<IGuiderMediator, ChooserMediatorFake>();
        var fake = (ChooserMediatorFake)(object)proxy;
        fake.Handler = (method, _) => method.Name == nameof(IGuiderMediator.GetDevice) ? device : null;
        fake.handler = new FakeGuiderVM
        {
            DeviceChooserVM = InterfaceFake.Create<IDeviceChooserVM>((method, _) => method.Name switch
            {
                "get_SelectedDevice" => selected,
                "get_Devices" => new List<IDevice> { selected },
                _ => null
            })
        };
        return proxy;
    }
}
