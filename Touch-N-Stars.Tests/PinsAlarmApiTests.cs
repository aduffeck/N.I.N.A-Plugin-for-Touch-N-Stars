using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.WebApi;
using Newtonsoft.Json.Linq;
using TouchNStars.Server.Controllers;
using TouchNStars.Server.Services.Alarm;
using Xunit;

namespace TouchNStars.Tests;

internal sealed class FakeBuzzer : IBuzzer
{
    private readonly List<(int Volume, int Ms)> calls = new();
    public BeepResult Result { get; set; } = BeepResult.Ok;

    public IReadOnlyList<(int Volume, int Ms)> Calls { get { lock (calls) return calls.ToList(); } }

    public BeepResult Beep(int volume, int durationMs)
    {
        lock (calls) calls.Add((volume, durationMs));
        return volume == 0 ? BeepResult.Ok : Result;
    }
}

public class AlarmBeepWorkerTests
{
    private static async Task WaitUntil(Func<bool> cond)
    {
        for (int i = 0; i < 200 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond());
    }

    [Fact]
    public async Task PlaysTheBurstAndReportsTheFirstResult()
    {
        var buzzer = new FakeBuzzer();
        var reports = new List<(long, BeepResult)>();
        var w = new AlarmBeepWorker(buzzer, (id, r) => { lock (reports) reports.Add((id, r)); });
        w.Start();
        Assert.True(w.TryRequest(new BurstRequest(7, 40, 10, 20, 4)));
        await WaitUntil(() => buzzer.Calls.Count == 4);
        await WaitUntil(() => !w.Bursting);
        Assert.Equal(new[] { (7L, BeepResult.Ok) }, reports);
        Assert.All(buzzer.Calls, c => Assert.Equal((40, 10), c));
        w.Stop();
    }

    [Fact]
    public async Task AFailedBeepEndsTheBurst()
    {
        var buzzer = new FakeBuzzer { Result = BeepResult.NoPowerBox };
        var reports = new List<(long, BeepResult)>();
        var w = new AlarmBeepWorker(buzzer, (id, r) => { lock (reports) reports.Add((id, r)); });
        w.Start();
        w.TryRequest(new BurstRequest(1, 40, 10, 20, 5));
        await WaitUntil(() => reports.Count == 1);
        await Task.Delay(100);
        Assert.Single(buzzer.Calls);
        Assert.Equal(BeepResult.NoPowerBox, reports[0].Item2);
        w.Stop();
    }

    [Fact]
    public async Task SilenceStopsTheToneAtOnce()
    {
        var buzzer = new FakeBuzzer();
        var w = new AlarmBeepWorker(buzzer, (_, _) => { });
        w.Start();
        w.TryRequest(new BurstRequest(1, 40, 100, 400, 10));
        await WaitUntil(() => w.Bursting);
        Assert.True(w.StopCurrent());
        await WaitUntil(() => !w.Bursting);
        Assert.Contains(buzzer.Calls, c => c.Volume == 0);
        int count = buzzer.Calls.Count;
        await Task.Delay(700);
        Assert.Equal(count, buzzer.Calls.Count);
        w.Stop();
    }

    [Fact]
    public async Task TestBurstReturnsTheResultAndReplacesARunningBurst()
    {
        var buzzer = new FakeBuzzer();
        var w = new AlarmBeepWorker(buzzer, (_, _) => { });
        w.Start();
        w.TryRequest(new BurstRequest(1, 40, 100, 400, 10));
        await WaitUntil(() => w.Bursting);
        Assert.Equal(BeepResult.Ok, await w.RunTestAsync(new BurstRequest(2, 20, 10, 10, 2)));
        await WaitUntil(() => !w.Bursting);
        // the alarm burst did not continue after the test
        await Task.Delay(600);
        Assert.Equal(2, buzzer.Calls.Count(c => c.Volume == 20));
        Assert.True(buzzer.Calls.Count(c => c.Volume == 40) < 3);
        buzzer.Result = BeepResult.Failed;
        Assert.Equal(BeepResult.Failed, await w.RunTestAsync(new BurstRequest(3, 40, 10, 10, 3)));
        w.Stop();
    }
}

/// <summary>The alarm controller on a real EmbedIO server, bound to a service with fake settings and buzzer.</summary>
internal sealed class PinsAlarmApiHost : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly WebServer server;
    private readonly HttpClient client;

    public PinsAlarmApiHost(FakeBuzzer buzzer)
    {
        Store = new AlarmSettingsStore(() => Raw, v => Raw = v);
        Service = new PinsAlarmService(new FakeAlarmClock(), Store, buzzer);
        Service.Start();
        int port = InternalGuiderApiHost.FreePort();
        server = new WebServer(o => o.WithUrlPrefix($"http://127.0.0.1:{port}/").WithMode(HttpListenerMode.EmbedIO))
            .WithWebApi("/api", m => m.WithController(() => new PinsAlarmController(Service)));
        _ = server.RunAsync(lifetime.Token);
        client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/pins/"), Timeout = TimeSpan.FromSeconds(20) };
    }

    public string? Raw { get; set; }
    public AlarmSettingsStore Store { get; }
    public PinsAlarmService Service { get; }

    public async Task<(int Status, JObject Json)> Send(HttpMethod method, string path, string? body = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            try
            {
                using var response = await client.SendAsync(request);
                return ((int)response.StatusCode, JObject.Parse(await response.Content.ReadAsStringAsync()));
            }
            catch (HttpRequestException) when (attempt < 30)
            {
                await Task.Delay(100);
            }
        }
    }

    public void Dispose()
    {
        Service.Stop();
        lifetime.Cancel();
        client.Dispose();
        server.Dispose();
    }
}

public class PinsAlarmApiTests
{
    [Fact]
    public async Task SettingsDefaultsUpdateAndValidation()
    {
        using var host = new PinsAlarmApiHost(new FakeBuzzer());
        var (status, json) = await host.Send(HttpMethod.Get, "alarm");
        Assert.Equal(200, status);
        Assert.False((bool)json["Response"]!["Stored"]!);
        Assert.Equal(50, (int)json["Response"]!["Settings"]!["Volume"]!);

        (status, json) = await host.Send(HttpMethod.Put, "alarm", "{\"Volume\":30,\"Categories\":{\"Warnings\":true}}");
        Assert.Equal(200, status);
        Assert.Equal(30, (int)json["Response"]!["Settings"]!["Volume"]!);
        Assert.True((bool)json["Response"]!["Settings"]!["Categories"]!["Warnings"]!);
        Assert.Contains("\"Volume\":30", host.Raw);

        (status, json) = await host.Send(HttpMethod.Put, "alarm", "{\"Volume\":0}");
        Assert.Equal(400, status);
        Assert.Contains("Volume", (string)json["Error"]!);
        (status, _) = await host.Send(HttpMethod.Put, "alarm", "not json");
        Assert.Equal(400, status);
        (status, _) = await host.Send(HttpMethod.Put, "alarm", "{\"Volume\":20,\"Volume\":30}");
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task TestBeepsAndReportsTheRealResult()
    {
        var buzzer = new FakeBuzzer();
        using var host = new PinsAlarmApiHost(buzzer);
        var (status, json) = await host.Send(HttpMethod.Post, "alarm/test", "{\"Volume\":20,\"BurstSeconds\":1}");
        Assert.Equal(200, status);
        Assert.Equal(1, (int)json["Response"]!["Tones"]!);
        Assert.Equal((20, 400), buzzer.Calls[0]);
        Assert.Null(host.Raw);  // overrides are not saved

        buzzer.Result = BeepResult.NoPowerBox;
        (status, _) = await host.Send(HttpMethod.Post, "alarm/test");
        Assert.Equal(404, status);
        buzzer.Result = BeepResult.Failed;
        (status, _) = await host.Send(HttpMethod.Post, "alarm/test");
        Assert.Equal(500, status);
        (status, _) = await host.Send(HttpMethod.Post, "alarm/test", "{\"Volume\":101}");
        Assert.Equal(400, status);

        (status, json) = await host.Send(HttpMethod.Get, "alarm/history?limit=5");
        Assert.Equal(200, status);
        var entries = (JArray)json["Response"]!["Entries"]!;
        Assert.Equal("BeepFailed", (string)entries[0]["Outcome"]!);
        Assert.Equal("Test", (string)entries[0]["Origin"]!);
        (status, _) = await host.Send(HttpMethod.Get, "alarm/history?limit=21");
        Assert.Equal(400, status);
    }

    [Fact]
    public async Task SilenceAndStatus()
    {
        using var host = new PinsAlarmApiHost(new FakeBuzzer());
        var (status, json) = await host.Send(HttpMethod.Post, "alarm/silence", "{\"Minutes\":15}");
        Assert.Equal(200, status);
        Assert.NotNull((string?)json["Response"]!["MutedUntilUtc"]);
        (status, json) = await host.Send(HttpMethod.Post, "alarm/silence", "{\"Minutes\":0}");
        Assert.Equal(200, status);
        Assert.Equal(JTokenType.Null, json["Response"]!["MutedUntilUtc"]!.Type);
        (status, _) = await host.Send(HttpMethod.Post, "alarm/silence", "{\"Minutes\":721}");
        Assert.Equal(400, status);
        (status, _) = await host.Send(HttpMethod.Post, "alarm/silence", "{\"Minutes\":\"30\"}");
        Assert.Equal(400, status);

        (status, json) = await host.Send(HttpMethod.Get, "alarm/status");
        Assert.Equal(200, status);
        Assert.True((bool)json["Response"]!["Enabled"]!);
        Assert.True((bool)json["Response"]!["Running"]!);
        Assert.True((int)json["Response"]!["GraceRemainingSeconds"]! > 0);
    }
}
