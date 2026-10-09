using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.WebApi;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using Newtonsoft.Json.Linq;
using TouchNStars.Server.Controllers;
using TouchNStars.Server.Services;

namespace TouchNStars.Tests;

/// <summary>The internal guider controller on a real EmbedIO server, bound to its own service and fake guider.</summary>
internal sealed class InternalGuiderApiHost : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly WebServer server;
    private readonly HttpClient client;

    /// <param name="internalGuider">The internal guider pins exports to plugins (also while it is not connected).</param>
    public InternalGuiderApiHost(IGuiderMediator mediator, IAdvancedGuider? internalGuider = null)
    {
        // A short wait so that "still running" calls answer 202 quickly in tests.
        Service = new InternalGuiderService(() => mediator, () => internalGuider!)
        {
            CallTimeout = TimeSpan.FromMilliseconds(500)
        };
        Port = FreePort();
        server = new WebServer(o => o.WithUrlPrefix($"http://127.0.0.1:{Port}/").WithMode(HttpListenerMode.EmbedIO))
            .WithWebApi("/api", m => m.WithController(() => new InternalGuiderController(Service)));
        _ = server.RunAsync(lifetime.Token);
        client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}/api/internal-guider/"), Timeout = TimeSpan.FromSeconds(20) };
    }

    public InternalGuiderService Service { get; }
    public int Port { get; }

    public Task<(int Status, JObject Json)> Get(string path) => Send(HttpMethod.Get, path, null);

    public Task<(int Status, JObject Json)> Post(string path, string? body = null) => Send(HttpMethod.Post, path, body);

    public Task<(int Status, JObject Json)> Delete(string path) => Send(HttpMethod.Delete, path, null);

    /// <summary>A GET whose body is not JSON (images, downloads): the status, the response (headers) and the body.</summary>
    public async Task<(int Status, HttpResponseMessage Response, byte[] Body)> GetRaw(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                HttpResponseMessage response = await client.GetAsync(path);
                byte[] body = await response.Content.ReadAsByteArrayAsync();
                return ((int)response.StatusCode, response, body);
            }
            catch (HttpRequestException) when (attempt < 30)
            {
                // The listener may not be up yet.
                await Task.Delay(100);
            }
        }
    }

    private async Task<(int Status, JObject Json)> Send(HttpMethod method, string path, string? body)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            try
            {
                using HttpResponseMessage response = await client.SendAsync(request);
                string text = await response.Content.ReadAsStringAsync();
                return ((int)response.StatusCode, JObject.Parse(text));
            }
            catch (HttpRequestException) when (attempt < 30)
            {
                // The listener may not be up yet.
                await Task.Delay(100);
            }
        }
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        Service.StopWatching();
        lifetime.Cancel();
        client.Dispose();
        server.Dispose();
    }
}

internal static class EventWaiter
{
    /// <summary>Collects the service's events; <see cref="WaitFor"/> returns the first match.</summary>
    public static Func<Func<InternalGuiderEvent, bool>, Task<InternalGuiderEvent>> Collect(InternalGuiderService service)
    {
        var events = new List<InternalGuiderEvent>();
        service.EventReceived += e => { lock (events) events.Add(e); };
        return async predicate =>
        {
            for (int i = 0; i < 100; i++)
            {
                lock (events)
                {
                    InternalGuiderEvent? match = events.FirstOrDefault(predicate);
                    if (match != null) return match;
                }
                await Task.Delay(50);
            }
            throw new TimeoutException("Expected event was not published.");
        };
    }

    public static JObject PayloadJson(InternalGuiderEvent evt) => JObject.Parse(InternalGuiderJson.Serialize(evt.Payload));
}
