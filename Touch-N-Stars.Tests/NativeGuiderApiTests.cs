using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using NINA.Equipment.Equipment.MyGuider.Advanced;
using NINA.Equipment.Interfaces;
using Newtonsoft.Json.Linq;
using TouchNStars.Server;
using TouchNStars.Server.Controllers;
using TouchNStars.Server.Services;
using Xunit;

namespace TouchNStars.Tests;

public class NativeGuiderJsonTests
{
    [Fact]
    public void Serialize_UsesCamelCaseAndNullForNonFiniteNumbers()
    {
        var status = new AdvancedGuiderStatus
        {
            State = "Guiding",
            IsSettling = true,
            LockX = double.NaN,
            PixelScale = double.PositiveInfinity,
            PrimaryStar = new AdvancedGuideStar { Snr = 42.5, Hfd = double.NaN },
            WindowStats = new AdvancedGuiderStats { RmsTotalArcsec = 0.61, DriftRaArcsecPerMin = null }
        };

        JObject json = JObject.Parse(NativeGuiderJson.Serialize(status));

        Assert.Equal("Guiding", (string?)json["state"]);
        Assert.True((bool)json["isSettling"]!);
        Assert.Equal(JTokenType.Null, json["lockX"]!.Type);
        Assert.Equal(JTokenType.Null, json["pixelScale"]!.Type);
        Assert.Equal(42.5, (double)json["primaryStar"]!["snr"]!);
        Assert.Equal(JTokenType.Null, json["primaryStar"]!["hfd"]!.Type);
        Assert.Equal(0.61, (double)json["windowStats"]!["rmsTotalArcsec"]!);
        Assert.Null(json["State"]);
    }

    [Fact]
    public void Serialize_WritesTheDecDriftStateAsAnObjectOrNull()
    {
        var drift = new AdvancedGuiderStatus
        {
            State = "Guiding",
            DecDrift = new AdvancedDecDriftState { Direction = AdvancedDecDriftDirections.North, DriftArcsecPerMin = double.NaN, SafetyValveOpen = true }
        };

        JObject json = JObject.Parse(NativeGuiderJson.Serialize(drift));
        Assert.Equal("North", (string?)json["decDrift"]!["direction"]);
        Assert.Equal(JTokenType.Null, json["decDrift"]!["driftArcsecPerMin"]!.Type);
        Assert.True((bool)json["decDrift"]!["safetyValveOpen"]!);

        JObject none = JObject.Parse(NativeGuiderJson.Serialize(new AdvancedGuiderStatus { State = "Guiding" }));
        Assert.Equal(JTokenType.Null, none["decDrift"]!.Type);
    }

    [Fact]
    public void Envelope_WrapsTypeTimestampAndPayload()
    {
        var step = new AdvancedGuideStep { Frame = 12, RaArcsec = -0.4, DecDirection = "North" };
        JObject json = JObject.Parse(NativeGuiderJson.Envelope("step", new DateTime(2026, 9, 23, 21, 0, 0, DateTimeKind.Utc), step));

        Assert.Equal("step", (string?)json["type"]);
        Assert.StartsWith("2026-09-23T21:00:00", json["timestamp"]!.ToString(Newtonsoft.Json.Formatting.None).Trim('"'));
        Assert.EndsWith("Z", json["timestamp"]!.ToString(Newtonsoft.Json.Formatting.None).Trim('"'));
        Assert.Equal(12, (long)json["payload"]!["frame"]!);
        Assert.Equal("North", (string?)json["payload"]!["decDirection"]);
    }

    [Fact]
    public void Envelope_NeverShipsFramePixels()
    {
        AdvancedGuiderFrame frame = FakeNativeGuider.Frame(7);
        string text = NativeGuiderJson.Envelope("frame", DateTime.UtcNow, frame);
        JObject json = JObject.Parse(text);

        Assert.DoesNotContain("pixels", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(7, (long)json["payload"]!["frameNumber"]!);
        Assert.Equal(2, (int)json["payload"]!["starCount"]!);
        Assert.Equal(1, (int)json["payload"]!["starsUsed"]!);
    }

    [Fact]
    public void Envelope_PassesSmallFrameNotificationsThrough()
    {
        JObject json = JObject.Parse(NativeGuiderJson.Envelope("frame", DateTime.UtcNow, new { FrameNumber = 3L, Width = 10 }));
        Assert.Equal(3, (long)json["payload"]!["frameNumber"]!);
    }
}

public class NativeGuiderSettingBodyTests
{
    [Theory]
    [InlineData("{\"name\":\"MultiStar\",\"value\":true}", "MultiStar", "true")]
    [InlineData("{\"name\":\"MultiStar\",\"value\":false}", "MultiStar", "false")]
    [InlineData("{\"name\":\"ExposureSeconds\",\"value\":1.5}", "ExposureSeconds", "1.5")]
    [InlineData("{\"name\":\"Gain\",\"value\":120}", "Gain", "120")]
    [InlineData("{\"name\":\"DecGuideMode\",\"value\":\"North\"}", "DecGuideMode", "North")]
    [InlineData("{\"Name\":\"Gain\",\"Value\":\"7\"}", "Gain", "7")]
    public void ParsesValuesToInvariantStrings(string body, string expectedName, string expectedValue)
    {
        Assert.True(NativeGuiderRequest.TryParseSettingBody(body, out string name, out string value, out string error), error);
        Assert.Equal(expectedName, name);
        Assert.Equal(expectedValue, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"value\":1}")]
    [InlineData("{\"name\":\"Gain\"}")]
    [InlineData("{\"name\":\"Gain\",\"value\":[1,2]}")]
    public void RejectsBadBodies(string body)
    {
        Assert.False(NativeGuiderRequest.TryParseSettingBody(body, out _, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

public class NativeGuiderDarksBodyTests
{
    [Fact]
    public void EmptyBodyUsesDefaults()
    {
        Assert.True(NativeGuiderRequest.TryParseDarksBody("", out double min, out double max, out int frames, out _));
        Assert.Equal(0.5, min);
        Assert.Equal(4.0, max);
        Assert.Equal(5, frames);
    }

    [Fact]
    public void ParsesAllFields()
    {
        Assert.True(NativeGuiderRequest.TryParseDarksBody("{\"minExposure\":1,\"maxExposure\":2.5,\"frames\":7}", out double min, out double max, out int frames, out _));
        Assert.Equal(1.0, min);
        Assert.Equal(2.5, max);
        Assert.Equal(7, frames);
    }

    [Theory]
    [InlineData("{\"minExposure\":3,\"maxExposure\":1}")]
    [InlineData("{\"minExposure\":0}")]
    [InlineData("{\"frames\":0}")]
    [InlineData("{\"maxExposure\":100}")]
    [InlineData("nope")]
    public void RejectsInvalidRanges(string body)
    {
        Assert.False(NativeGuiderRequest.TryParseDarksBody(body, out _, out _, out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}

public class NativeGuiderServiceTests
{
    [Fact]
    public void WithoutMediator_ReportsAReason()
    {
        var service = new NativeGuiderService(() => null!);
        Assert.Null(service.GetConnectedGuider(out string reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void DisconnectedGuider_IsNotAvailable()
    {
        var fake = new FakeNativeGuider { Connected = false };
        var service = new NativeGuiderService(() => fake.Mediator);
        Assert.Null(service.GetConnectedGuider(out string reason));
        Assert.Contains("connected", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConnectedGuider_IsResolved()
    {
        var fake = new FakeNativeGuider();
        var service = new NativeGuiderService(() => fake.Mediator);
        Assert.Same(fake.Guider, service.GetConnectedGuider(out _));
    }

    [Fact]
    public void GetFrame_KeepsRecentFramesAddressable()
    {
        var fake = new FakeNativeGuider();
        var service = new NativeGuiderService(() => fake.Mediator);

        fake.LatestFrame = FakeNativeGuider.Frame(1);
        Assert.Equal(1, service.GetFrame(fake.Guider, null)!.FrameNumber);
        fake.LatestFrame = FakeNativeGuider.Frame(2);
        Assert.Equal(2, service.GetFrame(fake.Guider, null)!.FrameNumber);

        // Frame 1 was already served, so /image?frame=1 still gets exactly that frame.
        Assert.Equal(1, service.GetFrame(fake.Guider, 1)!.FrameNumber);
        // An unknown frame falls back to the latest one.
        Assert.Equal(2, service.GetFrame(fake.Guider, 99)!.FrameNumber);
    }

    [Fact]
    public void FrameInfo_ContainsOverlayAndPrimaryCrop()
    {
        var service = new NativeGuiderService(() => null!);
        AdvancedGuiderFrame frame = FakeNativeGuider.Frame(5, 20, 20);
        JObject json = JObject.Parse(NativeGuiderJson.Serialize(service.BuildFrameInfo(frame, 5)));

        Assert.Equal(5, (long)json["frameNumber"]!);
        Assert.Equal(20, (int)json["width"]!);
        Assert.Equal(3.5, (double)json["lockX"]!);
        Assert.Equal(2, json["stars"]!.Count());
        Assert.Equal("LowSnr", (string?)json["stars"]![1]!["rejectReason"]);
        JToken crop = json["primaryCrop"]!;
        Assert.Equal(2, (int)crop["x0"]!);
        Assert.Equal(5, (int)crop["width"]!);
        Assert.Equal(25, crop["pixels"]!.Count());
        Assert.Empty(json["secondaryCrops"]!);
    }

    [Fact]
    public void FrameInfo_CropsTheStrongestSecondariesThatAreStars()
    {
        var service = new NativeGuiderService(() => null!);
        AdvancedGuiderFrame frame = FakeNativeGuider.Frame(5, 40, 40);
        frame.Stars.AddRange(new AdvancedGuideStar[]
        {
            new() { X = 10, Y = 10, Snr = 12, Used = true },
            new() { X = 30, Y = 30, Snr = 20, Used = true },
            new() { X = 20, Y = 30, Snr = 15, Used = false, RejectReason = "NotMeasured" },
            new() { X = 30, Y = 10, Snr = 50, Used = false, RejectReason = "Lost" },
            new() { X = 20, Y = 10, Snr = 40, Used = false, RejectReason = "DroppedZero" }
        });

        JObject json = JObject.Parse(NativeGuiderJson.Serialize(service.BuildFrameInfo(frame, 5, secondaries: 2)));

        JToken secondaries = json["secondaryCrops"]!;
        Assert.Equal(2, secondaries.Count());
        Assert.Equal(20, (double)secondaries[0]!["star"]!["snr"]!);
        Assert.Equal(28, (int)secondaries[0]!["crop"]!["x0"]!);
        Assert.Equal(25, secondaries[0]!["crop"]!["pixels"]!.Count());
        Assert.Equal(15, (double)secondaries[1]!["star"]!["snr"]!);
        Assert.Equal(28, (int)secondaries[1]!["crop"]!["y0"]!);
    }

    [Fact]
    public void Subscription_FollowsTheDevice()
    {
        var fake = new FakeNativeGuider();
        var service = new NativeGuiderService(() => fake.Mediator);
        var received = new List<string>();
        service.EventReceived += e => received.Add(e.Type);

        service.SyncSubscription();
        Assert.Equal(1, fake.SubscriberCount);
        Assert.Contains("device", received);

        fake.Raise("step", new AdvancedGuideStep { Frame = 1 });
        Assert.Contains("step", received);

        service.StopWatching();
        Assert.Equal(0, fake.SubscriberCount);
    }

    [Fact]
    public void ThrowingConsumer_DoesNotReachTheGuiderThread()
    {
        var fake = new FakeNativeGuider();
        var service = new NativeGuiderService(() => fake.Mediator);
        service.EventReceived += _ => throw new InvalidOperationException("boom");
        service.SyncSubscription();

        fake.Raise("alert", new AdvancedGuiderAlert { Title = "x" });
        service.StopWatching();
    }
}

public class NativeGuiderActionApiTests
{
    [Fact]
    public async Task Status_ReportsTheDeviceAndTheContractVersion()
    {
        var fake = new FakeNativeGuider { State = "Guiding" };
        using var host = new NativeGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Get("status");

        Assert.Equal(200, status);
        JToken response = json["response"]!;
        Assert.True((bool)response["available"]!);
        Assert.True((bool)response["connected"]!);
        Assert.True((bool)response["isNative"]!);
        Assert.Equal("PinsNativeGuider", (string?)response["deviceId"]);
        Assert.Equal("PINS Native Guider", (string?)response["deviceName"]);
        Assert.Equal(JTokenType.Null, response["reason"]!.Type);
        Assert.Equal("Guiding", (string?)response["status"]!["state"]);
        Assert.Equal(AdvancedGuiderContract.Version, (int)response["contractVersion"]!);
    }

    [Fact]
    public async Task Action_AnswersWithTheState()
    {
        var fake = new FakeNativeGuider { State = "Looping" };
        using var host = new NativeGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("loop");

        Assert.Equal(200, status);
        Assert.Equal("loop", (string?)json["response"]!["action"]);
        Assert.Equal("Looping", (string?)json["response"]!["state"]);
        Assert.Single(fake.CallsOf(nameof(IAdvancedGuider.StartLooping)));
    }

    [Fact]
    public async Task Action_RejectedByTheGuider_Answers409()
    {
        var fake = new FakeNativeGuider { State = "Stopped" };
        fake.OnCall = (name, _) => name == nameof(IAdvancedGuider.SetPaused) ? Task.FromResult(false) : null;
        using var host = new NativeGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("pause");

        Assert.Equal(409, status);
        Assert.Equal("Rejected", (string?)json["code"]);
        Assert.Contains("'pause'", (string?)json["error"]);
    }

    [Fact]
    public async Task Action_Failing_Answers500()
    {
        var fake = new FakeNativeGuider();
        fake.OnCall = (name, _) => name == nameof(IAdvancedGuider.StartLooping)
            ? Task.FromException<bool>(new InvalidOperationException("camera gone"))
            : null;
        using var host = new NativeGuiderApiHost(fake.Mediator);

        (int status, JObject json) = await host.Post("loop");

        Assert.Equal(500, status);
        Assert.Equal("camera gone", (string?)json["error"]);
    }

    [Fact]
    public async Task Action_StillRunning_Answers202AndPublishesTheOutcomeLater()
    {
        var fake = new FakeNativeGuider { State = "Guiding" };
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.OnCall = (name, _) => name == nameof(IAdvancedGuider.SetPaused) ? completion.Task : null;
        using var host = new NativeGuiderApiHost(fake.Mediator);
        var waitFor = EventWaiter.Collect(host.Service);

        (int status, JObject json) = await host.Post("pause");
        Assert.Equal(202, status);
        Assert.Equal("pause", (string?)json["response"]!["action"]);
        Assert.True((bool)json["response"]!["pending"]!);

        completion.SetResult(false);
        NativeGuiderEvent action = await waitFor(e => e.Type == "action");
        JObject payload = EventWaiter.PayloadJson(action);
        Assert.Equal("pause", (string?)payload["action"]);
        Assert.False((bool)payload["success"]!);
        Assert.Contains("rejected 'pause'", (string?)payload["error"]);
    }
}

public class NativeGuiderSocketTests
{
    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task<JObject> ReceiveUntil(ClientWebSocket socket, string type, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var builder = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            JObject message = JObject.Parse(builder.ToString());
            if ((string?)message["type"] == type) return message;
        }
    }

    [Fact]
    public async Task PushesHelloAndGuiderEventsAsEnvelopes()
    {
        var fake = new FakeNativeGuider();
        var service = new NativeGuiderService(() => fake.Mediator);
        int port = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var server = new WebServer(o => o.WithUrlPrefix($"http://127.0.0.1:{port}/").WithMode(HttpListenerMode.EmbedIO))
            .WithModule(new NativeGuiderSocket("/ws/native-guider", service));
        _ = server.RunAsync(cts.Token);

        using var client = new ClientWebSocket();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws/native-guider"), cts.Token);
                break;
            }
            catch when (attempt < 20)
            {
                await Task.Delay(100, cts.Token);
            }
        }

        JObject hello = await ReceiveUntil(client, "hello", cts.Token);
        Assert.True((bool)hello["payload"]!["available"]!);
        Assert.Equal("PinsNativeGuider", (string?)hello["payload"]!["deviceId"]);

        // The socket starts the device watch on connect; wait for the subscription.
        for (int i = 0; i < 50 && fake.SubscriberCount == 0; i++) await Task.Delay(50, cts.Token);
        Assert.Equal(1, fake.SubscriberCount);

        fake.Raise("step", new AdvancedGuideStep { Frame = 42, Snr = double.NaN, RaArcsec = 0.3 });
        JObject step = await ReceiveUntil(client, "step", cts.Token);
        Assert.Equal(42, (long)step["payload"]!["frame"]!);
        Assert.Equal(JTokenType.Null, step["payload"]!["snr"]!.Type);

        fake.Raise("frame", FakeNativeGuider.Frame(43));
        JObject frame = await ReceiveUntil(client, "frame", cts.Token);
        Assert.Equal(43, (long)frame["payload"]!["frameNumber"]!);
        Assert.Null(frame["payload"]!["pixels"]);

        await client.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"ping\"}"), WebSocketMessageType.Text, true, cts.Token);
        await ReceiveUntil(client, "pong", cts.Token);

        service.StopWatching();
    }
}
