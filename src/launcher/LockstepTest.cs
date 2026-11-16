using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text.Json;

namespace TH12Coop;

internal static partial class SelfTests
{
    internal delegate Task StartNumberedTestPeer(bool host, Action<string> status, CancellationToken token,
        Func<uint, uint> input, Action<LockstepPeer> observe, int? character);

    public static async Task<int> Lockstep(string gameDirectory, string? nativePath, int seconds, bool disconnectTest = false, string? guestGameDirectory = null,
        int playerOne = 0, int playerTwo = 5, bool nativeMenu = false, StartNumberedTestPeer? numbered = null)
    {
        using var lifetime = new CancellationTokenSource();
        using var identity = numbered == null ? RoomIdentity.Create() : null;
        using var guestLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        guestGameDirectory ??= gameDirectory;
        var hostBuild = await GameBuild.Identify(gameDirectory, lifetime.Token);
        var guestBuild = await GameBuild.Identify(guestGameDirectory, lifetime.Token);
        var durationClock = Stopwatch.StartNew();
        long? disconnectMilliseconds = null;
        var disconnectRequested = false;
        int port = 0;
        if (numbered == null)
        {
            using var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start();
            port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        }
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LockstepPeer? hostPeer = null, guestPeer = null;
        var notes = new List<string>();
        var quiet = new List<QuietTestEvidence>();
        var localPlayerRoles = new List<uint>();
        string? failure = null;
        var peerErrors = new List<object>();
        void Status(string prefix, string text)
        {
            if (numbered == null) lock (notes) notes.Add(prefix + text);
            if (prefix == "Host: " && text.StartsWith("接続待ち", StringComparison.Ordinal)) listening.TrySetResult();
        }
        uint Input(bool host, uint frame)
        {
            var peer = host ? hostPeer : guestPeer;
            if (peer?.StageObserved != true) return (host || nativeMenu) && frame % 90 is >= 10 and < 15 ? 1u : 0u;
            var direction = ((frame / 120 & 1) == 0) == host ? 0x40u : 0x80u;
            return 1 | direction;
        }
        Task Start(bool isHost)
        {
            var token = isHost ? lifetime.Token : guestLifetime.Token;
            Action<string> status = text => Status(isHost ? "Host: " : "Guest: ", text);
            Func<uint, uint> input = frame => Input(isHost, frame);
            void Observe(LockstepPeer peer) { if (isHost) hostPeer = peer; else guestPeer = peer; }
            int? character = nativeMenu ? null : isHost ? playerOne : playerTwo;
            if (numbered != null) return numbered(isHost, status, token, input, Observe, character);
            return isHost ? OnlineSession.Host(gameDirectory, port, identity!, 3, status, token, nativePath, true, input, Observe, character)
                : OnlineSession.Join(guestGameDirectory, "127.0.0.1", port, identity!.Invite, status, token, nativePath, true, input, Observe, character);
        }
        var host = Start(true);
        Task? guest = null;
        try
        {
            if (numbered == null)
            {
                var wait = listening.Task.WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
                if (await Task.WhenAny(wait, host) == host) await host;
                await wait;
            }
            guest = Start(false);
            var duration = Task.Delay(TimeSpan.FromSeconds(seconds), lifetime.Token);
            var finished = await Task.WhenAny(host, guest, duration);
            if (finished != duration) { await finished; failure = "A peer ended before the requested duration."; }
            else
            {
                foreach (var peer in new[] { hostPeer, guestPeer })
                {
                    var pid = peer?.GameProcessId ?? throw new IOException("Game PID was not recorded.");
                    quiet.Add(QuietTestEnvironment.ReadEvidence(pid));
                    using var mapping = MemoryMappedFile.OpenExisting($"Local\\TH12CoopNet_{pid}");
                    using var memory = mapping.CreateViewAccessor(120, 4, MemoryMappedFileAccess.Read);
                    localPlayerRoles.Add(memory.ReadUInt32(0));
                }
                if (!localPlayerRoles.SequenceEqual(new uint[] { 1, 2 })) throw new IOException("Native local player roles were not assigned to host/guest.");
                if (quiet.Any(evidence => !evidence.Passed)) throw new IOException("Quiet/background execution was not confirmed.");
            }
            if (finished == duration && disconnectTest)
            {
                disconnectRequested = true;
                var stopping = Stopwatch.StartNew();
                guestLifetime.Cancel();
                // Cancel only the guest. The host must independently observe the
                // transport ending and release its own game, not await our token.
                var stopped = Task.WhenAll(host, guest);
                try { await stopped.WaitAsync(TimeSpan.FromSeconds(10)); }
                catch (Exception) when (stopped.IsCompleted) { }
                disconnectMilliseconds = stopping.ElapsedMilliseconds;
            }
        }
        catch (Exception error) { failure = numbered == null ? error.ToString() : error.GetType().Name; }
        finally
        {
            lifetime.Cancel();
            foreach (var (name, task) in new[] { ("host", host), ("guest", guest) })
            {
                if (task == null) continue;
                try { await task; }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
                catch (Exception error) { peerErrors.Add(new { peer = name, error = numbered == null ? error.ToString() : error.GetType().Name }); }
            }
        }
        object? Summary(LockstepPeer? peer) => peer == null ? null : new
        {
            peer.CommittedFrame, peer.ConfirmedFrame, peer.StageObserved, peer.MaximumP1Shots, peer.MaximumP2Shots,
            peer.GameProcessId,
            minP2X = float.IsFinite(peer.MinimumP2X) ? peer.MinimumP2X : (float?)null,
            maxP2X = float.IsFinite(peer.MaximumP2X) ? peer.MaximumP2X : (float?)null,
        };
        bool Sufficient(LockstepPeer? peer) => peer != null && peer.ConfirmedFrame > 600 && peer.StageObserved &&
            peer.MaximumP1Shots > 1 && peer.MaximumP2Shots > 1 && peer.MaximumP2X - peer.MinimumP2X > 10;
        bool Exited(int? id)
        {
            if (id == null) return false;
            try { using var process = Process.GetProcessById(id.Value); return process.HasExited; }
            catch (ArgumentException) { return true; }
        }
        var ownedProcessesExited = Exited(hostPeer?.GameProcessId) && Exited(guestPeer?.GameProcessId);
        var passed = failure == null && (numbered == null || peerErrors.Count == 0) && quiet.Count == 2 && quiet.All(evidence => evidence.Passed) && Sufficient(hostPeer) && Sufficient(guestPeer) && ownedProcessesExited &&
            (!disconnectTest || disconnectRequested && disconnectMilliseconds < 10000);
        var result = new { passed, failure, peerErrors, seconds, elapsedMilliseconds = durationClock.ElapsedMilliseconds,
            protocol = SecureChannel.ProtocolVersion, rules = NetBridge.RulesVersion, nativeMenu,
            startupPlayerOne = nativeMenu ? (int?)null : playerOne, startupPlayerTwo = nativeMenu ? (int?)null : playerTwo, quiet, localPlayerRoles,
            hostGame = new { build = hostBuild.Id, exeHash = hostBuild.ExeHash },
            guestGame = new { build = guestBuild.Id, exeHash = guestBuild.ExeHash },
            disconnectTest, disconnectRequested, disconnectMilliseconds, ownedProcessesExited, host = Summary(hostPeer), guest = Summary(guestPeer), notes,
            scope = "Two actual TH12 processes, separate game state and working directories, pinned TLS 1.3 input lockstep over loopback. No video/audio stream. Not a second physical PC or WAN test." };
        var filename = numbered != null ? "numbered-lockstep-test-results.json" : disconnectTest ? "disconnect-test-results.json" : "lockstep-test-results.json";
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, filename), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
}
