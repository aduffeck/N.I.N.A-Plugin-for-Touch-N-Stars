using System;
using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using EmbedIO.WebSockets;
using NINA.Core.Utility;
using TouchNStars.Server.Services;

namespace TouchNStars.Server;

/// <summary>
/// Live feed of the internal guider (<c>/ws/internal-guider</c>): every guider event plus hello, device, action and
/// heartbeat messages as <c>{ type, timestamp, payload }</c>; see INTERNAL_GUIDER_API.md.
///
/// The guider raises its events on its own loop thread, so the handler here only enqueues;
/// one dispatcher serializes, and every client has its own bounded queue and sender task. A
/// slow or dead client therefore loses its own oldest messages and never delays the guider or
/// the other clients.
/// </summary>
public class InternalGuiderSocket : WebSocketModule
{
    private const int DispatchQueueSize = 512;
    private const int ClientQueueSize = 256;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(10);

    private sealed class Client
    {
        public Client(IWebSocketContext context)
        {
            Context = context;
            Queue = Channel.CreateBounded<string>(new BoundedChannelOptions(ClientQueueSize)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
        }

        public IWebSocketContext Context { get; }
        public Channel<string> Queue { get; }
    }

    private readonly InternalGuiderService service;
    private readonly ConcurrentDictionary<string, Client> clients = new();
    private readonly Channel<InternalGuiderEvent> dispatchQueue;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Timer heartbeatTimer;

    public InternalGuiderSocket(string urlPath) : this(urlPath, InternalGuiderService.Instance)
    {
    }

    public InternalGuiderSocket(string urlPath, InternalGuiderService service) : base(urlPath, true)
    {
        this.service = service;
        dispatchQueue = Channel.CreateBounded<InternalGuiderEvent>(new BoundedChannelOptions(DispatchQueueSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        service.EventReceived += OnServiceEvent;
        _ = Task.Run(DispatchLoopAsync);
        heartbeatTimer = new Timer(_ => SendHeartbeat(), null, HeartbeatInterval, HeartbeatInterval);
    }

    protected override Task OnClientConnectedAsync(IWebSocketContext context)
    {
        var client = new Client(context);
        clients[context.Id] = client;
        _ = Task.Run(() => SendLoopAsync(client));

        try
        {
            client.Queue.Writer.TryWrite(InternalGuiderJson.Envelope("hello", DateTime.UtcNow, service.GetDeviceSummary()));
        }
        catch (Exception ex)
        {
            Logger.Debug($"InternalGuiderSocket: hello failed: {ex.Message}");
        }

        service.EnsureWatching();
        return Task.CompletedTask;
    }

    protected override Task OnClientDisconnectedAsync(IWebSocketContext context)
    {
        if (clients.TryRemove(context.Id, out Client client))
        {
            client.Queue.Writer.TryComplete();
        }
        return Task.CompletedTask;
    }

    protected override Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result)
    {
        try
        {
            string message = Encoding.GetString(buffer);
            if (message.Contains("\"ping\"", StringComparison.OrdinalIgnoreCase) && clients.TryGetValue(context.Id, out Client client))
            {
                client.Queue.Writer.TryWrite(InternalGuiderJson.Envelope("pong", DateTime.UtcNow, null));
            }
        }
        catch (Exception ex)
        {
            Logger.Debug($"InternalGuiderSocket: message handling failed: {ex.Message}");
        }
        return Task.CompletedTask;
    }

    private void OnServiceEvent(InternalGuiderEvent evt)
    {
        // Runs on the guider thread: enqueue only.
        if (clients.IsEmpty) return;
        dispatchQueue.Writer.TryWrite(evt);
    }

    private async Task DispatchLoopAsync()
    {
        try
        {
            while (await dispatchQueue.Reader.WaitToReadAsync(lifetime.Token).ConfigureAwait(false))
            {
                while (dispatchQueue.Reader.TryRead(out InternalGuiderEvent evt))
                {
                    string json;
                    try
                    {
                        json = InternalGuiderJson.Envelope(evt.Type, evt.Timestamp, evt.Payload);
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"InternalGuiderSocket: could not serialize '{evt.Type}' event: {ex.Message}");
                        continue;
                    }
                    Broadcast(json);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error($"InternalGuiderSocket: dispatcher stopped: {ex}");
        }
    }

    private void Broadcast(string json)
    {
        foreach (Client client in clients.Values)
        {
            client.Queue.Writer.TryWrite(json);
        }
    }

    private async Task SendLoopAsync(Client client)
    {
        try
        {
            while (await client.Queue.Reader.WaitToReadAsync(lifetime.Token).ConfigureAwait(false))
            {
                while (client.Queue.Reader.TryRead(out string json))
                {
                    await SendAsync(client.Context, json).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Debug($"InternalGuiderSocket: client {client.Context.Id} send loop ended: {ex.Message}");
            clients.TryRemove(client.Context.Id, out _);
        }
    }

    private void SendHeartbeat()
    {
        if (clients.IsEmpty) return;
        try
        {
            Broadcast(InternalGuiderJson.Envelope("heartbeat", DateTime.UtcNow, service.GetDeviceSummary()));
        }
        catch (Exception ex)
        {
            Logger.Debug($"InternalGuiderSocket: heartbeat failed: {ex.Message}");
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            service.EventReceived -= OnServiceEvent;
            heartbeatTimer.Dispose();
            lifetime.Cancel();
            dispatchQueue.Writer.TryComplete();
            foreach (Client client in clients.Values)
            {
                client.Queue.Writer.TryComplete();
            }
            clients.Clear();
        }
        base.Dispose(disposing);
    }
}
