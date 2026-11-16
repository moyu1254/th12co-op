using System.Net.WebSockets;

namespace TH12Coop.Rendezvous;

internal sealed record RelayLimits(int Pairs, long Bytes, TimeSpan Wait, TimeSpan Lifetime, TimeSpan Idle)
{
    internal static readonly RelayLimits Default = new(8, 64L * 1024 * 1024,
        TimeSpan.FromSeconds(35), TimeSpan.FromHours(2), TimeSpan.FromSeconds(60));

    internal void Validate()
    {
        if (Pairs < 1 || Pairs > Default.Pairs || Bytes < 1 || Bytes > Default.Bytes ||
            Wait <= TimeSpan.Zero || Wait > Default.Wait || Lifetime <= TimeSpan.Zero || Lifetime > Default.Lifetime ||
            Idle <= TimeSpan.Zero || Idle > Default.Idle)
            throw new ArgumentOutOfRangeException(nameof(RelayLimits));
    }
}

// One pair per answered room. Both role credentials must arrive before either HTTP
// upgrade succeeds, so waiting for the other route never uses the peer TLS deadline.
internal sealed class RelayPair(bool firstHost, RelayLimits limits, TimeProvider clock, Action releaseCapacity)
{
    private readonly object gate = new();
    private readonly CancellationTokenSource deadline = new(limits.Wait, clock);
    private readonly TaskCompletionSource rolesReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource socketsReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly WebSocket?[] sockets = new WebSocket?[2];
    private bool paired, finished;
    internal bool IsPaired { get { lock (gate) return paired; } }
    private int holders = 1, closeCount;
    private long transferred;

    internal bool ReserveOther(bool host)
    {
        lock (gate)
        {
            if (host == firstHost || paired || finished || deadline.IsCancellationRequested) return false;
            paired = true;
            holders++;
            deadline.CancelAfter(TimeSpan.FromSeconds(5)); // Both HTTP upgrades must finish.
            rolesReady.TrySetResult();
            return true;
        }
    }

    internal void StopIfWaiting()
    {
        lock (gate)
            if (!paired && !finished) deadline.Cancel();
    }

    internal void Stop()
    {
        lock (gate)
            if (!finished) deadline.Cancel();
    }

    internal async Task Serve(HttpContext context, bool host, CancellationToken stopping)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping, deadline.Token);
        var token = session.Token;
        int side = host ? 0 : 1;
        WebSocket? socket = null;
        try
        {
            await rolesReady.Task.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            // AcceptWebSocketAsync itself has no cancellation parameter.
            using (token.Register(context.Abort))
                socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
                {
                    DangerousEnableCompression = false,
                    SubProtocol = context.WebSockets.WebSocketRequestedProtocols.Contains(RelayMessages.HeartbeatProtocol, StringComparer.Ordinal)
                        ? RelayMessages.HeartbeatProtocol : null
                });
            lock (gate)
            {
                sockets[side] = socket;
                if (sockets[1 - side] != null)
                {
                    deadline.CancelAfter(limits.Lifetime);
                    socketsReady.TrySetResult();
                }
            }
            await socketsReady.Task.WaitAsync(token);
            var remote = sockets[1 - side]!;
            // Once upgraded, read disconnects from the socket itself. A request-abort
            // notification can race the final ciphertext and close frame already on it.
            using var live = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, stopping);
            await CopyMessages(socket, remote, live);
            // Each direction has one writer. Forward its Close after its last data;
            // the other writer can finish sending to our still-readable peer. Both
            // upgraded requests must stay alive through the reverse close. Allow
            // the application's 40-second read window, within the original pair TTL.
            live.CancelAfter(TimeSpan.FromSeconds(40));
            await remote.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, live.Token);
            if (Interlocked.Increment(ref closeCount) == 2) closed.TrySetResult();
            await closed.Task.WaitAsync(live.Token);
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException or
            InvalidDataException or WebSocketException or InvalidOperationException)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = StatusCodes.Status408RequestTimeout;
        }
        finally
        {
            Stop();
            try { socket?.Dispose(); }
            finally
            {
                lock (gate)
                {
                    if (--holders == 0)
                    {
                        finished = true;
                        Array.Clear(sockets);
                        deadline.Dispose();
                        releaseCapacity();
                    }
                }
            }
        }
    }

    private async Task CopyMessages(WebSocket source, WebSocket destination, CancellationTokenSource live)
    {
        var messages = new RelayMessages();
        var token = live.Token;
        // The launcher sends at most 720 heartbeats in a full two-hour session.
        // A per-direction cap bounds control traffic even when messages arrive in a burst.
        int heartbeats = 0;
        void Heartbeat()
        {
            if (++heartbeats > 1024) throw new InvalidDataException();
            live.CancelAfter(limits.Idle);
        }
        Action? heartbeat = source.SubProtocol == RelayMessages.HeartbeatProtocol ? Heartbeat : null;
        while (true)
        {
            // Completed ciphertext or a negotiated transport heartbeat renews only
            // this direction. Partial messages and stalled sends remain bounded.
            live.CancelAfter(limits.Idle);
            var payload = await messages.Read(source, token, heartbeat);
            if (payload.IsEmpty) return;
            if (Interlocked.Add(ref transferred, payload.Length) > limits.Bytes) throw new InvalidDataException();
            await destination.SendAsync(payload, WebSocketMessageType.Binary, true, token);
        }
    }
}
