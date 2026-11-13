#nullable enable
using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TouchNStars.Server.Services.Alarm;

/// <summary>
/// Plays bursts, one at a time, on its own task: the buzzer call takes the PowerBox driver's lock and must never run
/// under the engine lock or on a mediator thread. Alarm bursts and test bursts go through the same queue, so they never
/// play at the same time. A request that arrives while one waits replaces it; a running burst is only cut short by a
/// test, silence or stop.
/// </summary>
public sealed class AlarmBeepWorker {
    private sealed record Job(BurstRequest Request, Action<BeepResult> First);

    private readonly IBuzzer buzzer;
    private readonly Action<long, BeepResult> report;
    private readonly object gate = new();
    private Channel<Job>? channel;
    private CancellationTokenSource? stopCts;
    private CancellationTokenSource? burstCts;
    private Task? loop;
    private volatile bool bursting;

    /// <param name="report">Called with the result of the first beep of every alarm burst.</param>
    public AlarmBeepWorker(IBuzzer buzzer, Action<long, BeepResult> report) {
        this.buzzer = buzzer;
        this.report = report;
    }

    public bool Bursting => bursting;

    public void Start() {
        lock (gate) {
            if (loop != null) {
                return;
            }
            // A waiting job that a newer one replaces did not play (in practice only next to a test: alarm bursts
            // are at least 30 s apart and last at most 30 s)
            channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(1) {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            }, dropped => dropped.First(BeepResult.Failed));
            stopCts = new CancellationTokenSource();
            var reader = channel.Reader;
            var token = stopCts.Token;
            loop = Task.Run(() => RunAsync(reader, token));
        }
    }

    public void Stop() {
        Task? t;
        lock (gate) {
            if (loop == null) {
                return;
            }
            if (channel != null) {
                // A job that waits does not play: its alarm is not left "pending"
                while (channel.Reader.TryRead(out var dropped)) {
                    dropped.First(BeepResult.Failed);
                }
                channel.Writer.TryComplete();
            }
            channel = null;
            stopCts?.Cancel();
            burstCts?.Cancel();
            t = loop;
            loop = null;
        }
        try {
            t.Wait(TimeSpan.FromSeconds(3));
        } catch (AggregateException) {
        }
    }

    public bool TryRequest(BurstRequest request) {
        return TryWrite(new Job(request, r => report(request.AlarmId, r)));
    }

    private bool TryWrite(Job job) {
        lock (gate) {
            return channel != null && channel.Writer.TryWrite(job);
        }
    }

    /// <summary>Ends a running burst and drops a waiting one (silence); the tone stops at once.</summary>
    public bool StopCurrent() {
        bool was;
        lock (gate) {
            was = bursting;
            burstCts?.Cancel();
            if (channel != null) {
                while (channel.Reader.TryRead(out var dropped)) {
                    was = true;
                    dropped.First(BeepResult.Failed);
                }
            }
        }
        if (was) {
            buzzer.Beep(0, 1);
        }
        return was;
    }

    /// <summary>A test burst: replaces a running one, returns the result of its first beep (waits at most 3 s for it).</summary>
    public async Task<BeepResult> RunTestAsync(BurstRequest request) {
        var first = new TaskCompletionSource<BeepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) {
            // The test replaces a running burst (and a waiting one)
            burstCts?.Cancel();
        }
        if (!TryWrite(new Job(request, r => first.TrySetResult(r)))) {
            return BeepResult.Failed;
        }
        var done = await Task.WhenAny(first.Task, Task.Delay(3000)).ConfigureAwait(false);
        return done == first.Task ? first.Task.Result : BeepResult.Failed;
    }

    private async Task RunAsync(ChannelReader<Job> reader, CancellationToken token) {
        try {
            while (await reader.WaitToReadAsync(token).ConfigureAwait(false)) {
                while (reader.TryRead(out var job)) {
                    CancellationTokenSource cts;
                    lock (gate) {
                        cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        burstCts = cts;
                    }
                    await PlayAsync(job, cts.Token).ConfigureAwait(false);
                }
            }
        } catch (OperationCanceledException) {
        } catch (ChannelClosedException) {
        }
    }

    private async Task PlayAsync(Job job, CancellationToken token) {
        var request = job.Request;
        bursting = true;
        bool reported = false;
        try {
            for (int i = 0; i < request.Tones; i++) {
                if (token.IsCancellationRequested) {
                    break;
                }
                BeepResult r;
                try {
                    r = buzzer.Beep(request.Volume, request.ToneMs);
                } catch (Exception) {
                    r = BeepResult.Failed;
                }
                if (i == 0) {
                    reported = true;
                    job.First(r);
                }
                if (r != BeepResult.Ok) {
                    break;
                }
                try {
                    await Task.Delay(request.ToneMs + request.PauseMs, token).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    break;
                }
            }
        } finally {
            bursting = false;
            if (!reported) {
                // Cancelled before its first tone
                job.First(BeepResult.Failed);
            }
        }
    }
}
