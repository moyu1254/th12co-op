using System.Net.WebSockets;

namespace TH12Coop.Rendezvous;

internal sealed record RelayLimits(int Pairs, long Bytes, TimeSpan Wait, TimeSpan Lifetime)
{
    internal static readonly RelayLimits Default = new(8, 64L * 1024 * 1024,
        TimeSpan.FromSeconds(35), TimeSpan.FromHours(2));

    internal void Validate()
    {
        if (Pairs < 1 || Pairs > Default.Pairs || Bytes < 1 || Bytes > Default.Bytes ||
            Wait <= TimeSpan.Zero || Wait > Default.Wait || Lifetime <= TimeSpan.Zero || Lifetime > Default.Lifetime)
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
                socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext { DangerousEnableCompression = false });
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
            await CopyMessages(socket, remote, live.Token);
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

    private async Task CopyMessages(WebSocket source, WebSocket destination, CancellationToken token)
    {
        var messages = new RelayMessages();
        while (true)
        {
            var payload = await messages.Read(source, token);
            if (payload.IsEmpty) return;
            if (Interlocked.Add(ref transferred, payload.Length) > limits.Bytes) throw new InvalidDataException();
            await destination.SendAsync(payload, WebSocketMessageType.Binary, true, token);
        }
    }
}
