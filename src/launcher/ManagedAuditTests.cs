using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace TH12Coop;

internal static partial class SelfTests
{
    // Filesystem and IPC regressions run without loading an original game or DLL.
    internal static async Task<int> ManagedAudit(string? filter = null)
    {
        var results = new List<object>();
        var failed = 0;
        async Task Check(string name, Func<Task> test)
        {
            if (filter != null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) return;
            try { await test(); results.Add(new { name, passed = true, error = (string?)null }); }
            catch (Exception error) { ++failed; results.Add(new { name, passed = false, error = error.ToString() }); }
        }
        await Check("Cancelled Steam identity creation leaves no final file and can retry", async () =>
        {
            using var fixture = new PreparationFixture();
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await ExpectThrowsAsync<OperationCanceledException>(() => GameBuild.PrepareSteamIdentity(fixture.GameDirectory, cancelled.Token));
            Expect(!File.Exists(Path.Combine(fixture.GameDirectory, "steam_appid.txt")), "cancelled identity left a final file");
            await GameBuild.PrepareSteamIdentity(fixture.GameDirectory, CancellationToken.None);
            Expect(File.ReadAllText(Path.Combine(fixture.GameDirectory, "steam_appid.txt")) == GameBuild.SteamAppId, "identity retry");
        });
        await Check("Cancelled online preparation leaves no final directory and can retry", async () =>
        {
            using var fixture = new PreparationFixture();
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await ExpectThrowsAsync<OperationCanceledException>(() => fixture.Profile.Prepare(fixture.Session, 1, cancelled.Token));
            Expect(!Directory.Exists(fixture.Destination), "cancelled preparation left a final directory");
            var prepared = await fixture.Profile.Prepare(fixture.Session, 1, CancellationToken.None);
            Expect(File.ReadAllBytes(Path.Combine(prepared, "th12.cfg")).SequenceEqual(fixture.Configuration), "session configuration");
        });
        await Check("Failed online asset preparation leaves no partially published directory", async () =>
        {
            using var fixture = new PreparationFixture();
            File.Delete(Path.Combine(fixture.GameDirectory, "th12.dat"));
            await ExpectThrowsAsync<IOException>(() => fixture.Profile.Prepare(fixture.Session, 1, CancellationToken.None));
            Expect(!Directory.Exists(fixture.Destination), "failed preparation published partial assets");
        });
        await Check("Game assets changed after the manifest was read are rejected", async () =>
        {
            using var fixture = new PreparationFixture();
            File.WriteAllBytes(Path.Combine(fixture.GameDirectory, "d3dx9_40.dll"), [9, 9, 9]);
            await ExpectThrowsAsync<IOException>(() => fixture.Profile.Prepare(fixture.Session, 1, CancellationToken.None));
            Expect(!Directory.Exists(fixture.Destination), "changed dependency was published");
        });
        await Check("Native DLL changed after the manifest was read is rejected", async () =>
        {
            using var fixture = new PreparationFixture();
            File.WriteAllBytes(fixture.NativePath, [9, 9, 9]);
            await ExpectThrowsAsync<IOException>(() => fixture.Profile.Prepare(fixture.Session, 1, CancellationToken.None));
            Expect(!Directory.Exists(fixture.Destination), "changed native DLL was published");
        });
        await Check("Failed startup restores the prior valid configuration exactly", () =>
        {
            using var fixture = new PreparationFixture();
            var path = Path.Combine(fixture.GameDirectory, "th12.cfg");
            File.WriteAllBytes(path, fixture.Configuration);
            var type = typeof(GameSession).GetNestedType("ConfigurationSnapshot", BindingFlags.NonPublic)!;
            var snapshot = type.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [path]);
            File.WriteAllBytes(path, [0, 1, 2]);
            type.GetMethod("RestoreAfterFailedStartup", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(snapshot, null);
            Expect(File.ReadAllBytes(path).SequenceEqual(fixture.Configuration), "prior configuration was not restored");
            File.Delete(path);
            snapshot = type.GetMethod("Read", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [path]);
            File.WriteAllBytes(path, [0, 1, 2]);
            type.GetMethod("RestoreAfterFailedStartup", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(snapshot, null);
            Expect(!File.Exists(path), "new corrupt configuration was retained");
            return Task.CompletedTask;
        });
        await Check("Native local-input IPC rejects unsupported bits and unstable frame", () =>
        {
            using var bridge = new NetBridge(Environment.ProcessId, 123, true);
            using var mapping = MemoryMappedFile.OpenExisting($"Local\\TH12CoopNet_{Environment.ProcessId}");
            using var memory = mapping.CreateViewAccessor();
            memory.Write(80, 0x1fbu); memory.Write(84, 1u);
            Expect(bridge.LocalInput(1) == 0x1fb, "allowed local mask");
            memory.Write(80, 4u);
            ExpectThrows<IOException>(() => bridge.LocalInput(1));
            memory.Write(84, 2u);
            ExpectThrows<IOException>(() => bridge.LocalInput(1));
            return Task.CompletedTask;
        });
        await Check("Cleanup preserves the first native abort reason", () =>
        {
            using var bridge = new NetBridge(Environment.ProcessId, 123, true);
            using var mapping = MemoryMappedFile.OpenExisting($"Local\\TH12CoopNet_{Environment.ProcessId}");
            using var memory = mapping.CreateViewAccessor();
            bridge.Abort(3);
            bridge.Abort();
            Expect(memory.ReadUInt32(28) == 3, "cleanup overwrote the synchronization failure reason");
            return Task.CompletedTask;
        });
        await Check("Initial configuration writes are atomic and preserve existing files", async () =>
        {
            using var fixture = new PreparationFixture();
            var path = Path.Combine(fixture.GameDirectory, "th12.cfg");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await ExpectThrowsAsync<OperationCanceledException>(() => GameConfiguration.WriteNew(path, fixture.Configuration, cancelled.Token));
            Expect(!File.Exists(path), "cancelled initial configuration was published");
            await GameConfiguration.WriteNew(path, fixture.Configuration, CancellationToken.None);
            await ExpectThrowsAsync<IOException>(() => GameConfiguration.WriteNew(path, [9, 9], CancellationToken.None));
            Expect(File.ReadAllBytes(path).SequenceEqual(fixture.Configuration), "existing configuration overwritten");
            Expect(Directory.GetFiles(fixture.GameDirectory, "*.tmp").Length == 0, "temporary configuration leak");
        });
        await Check("A disconnected peer cancels preparation before publishing a session", async () =>
        {
            using var fixture = new PreparationFixture();
            await WithChannels(async (client, server, token) =>
            {
                server.Dispose();
                await Task.Delay(100, token);
                var manifest = new SessionManifest(SecureChannel.ProtocolVersion, NetBridge.RulesVersion, 3, 123,
                    Convert.ToHexString(client.Session), fixture.Profile.Assets);
                var peer = new LockstepPeer(client, manifest, 1);
                // Missing injector and synthetic unrecognized game bytes also
                // prevent a process launch if the original broken path proceeds.
                await ExpectThrowsAsync<IOException>(() => peer.Run(fixture.Profile, true, null, _ => { }, token));
                var directory = Path.Combine(Path.GetDirectoryName(fixture.GameDirectory)!, "sessions", Convert.ToHexString(client.Session), "host");
                Expect(peer.GameProcessId == null && !Directory.Exists(directory), "disconnected startup still published a session");
            });
        });
        await Check("TLS EOF is a transport failure and caller cancellation remains cancellation", async () =>
        {
            await WithChannels(async (client, server, token) =>
            {
                using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
                cancelled.CancelAfter(100);
                await ExpectThrowsAsync<OperationCanceledException>(() => client.Receive(cancelled.Token));
                server.Dispose();
                await ExpectThrowsAsync<IOException>(() => client.Receive(token));
            });
        });
        await Check("TLS receive inactivity deadline is reported as timeout, not user cancellation", async () =>
        {
            await WithChannels(async (client, server, token) =>
            {
                var elapsed = Stopwatch.StartNew();
                await ExpectThrowsAsync<TimeoutException>(() => client.Receive(token));
                Expect(elapsed.Elapsed >= TimeSpan.FromSeconds(35) && elapsed.Elapsed < TimeSpan.FromSeconds(50), "actual receive deadline");
            });
        });
        await Check("TLS connection and authentication deadlines are reported as timeouts", async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var identity = RoomIdentity.Create();
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var connecting = SecureChannel.Connect("127.0.0.1", port, identity.Invite, deadline.Token);
                using (var silentServer = await listener.AcceptTcpClientAsync(deadline.Token))
                    await ExpectThrowsAsync<TimeoutException>(async () => { using var unexpected = await connecting; });
                using var silentClient = new TcpClient();
                await silentClient.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
                var raw = await listener.AcceptTcpClientAsync(deadline.Token);
                await ExpectThrowsAsync<TimeoutException>(async () => { using var unexpected = await SecureChannel.Accept(raw, identity, deadline.Token); });
            }
            finally { listener.Stop(); }
        });
        await Check("TLS backpressure reaches the production send deadline without an unbounded wait", async () =>
        {
            await WithChannels(async (client, server, token) =>
            {
                var socket = typeof(SecureChannel).GetField("client", BindingFlags.NonPublic | BindingFlags.Instance)!;
                ((TcpClient)socket.GetValue(client)!).SendBufferSize = 4096;
                ((TcpClient)socket.GetValue(server)!).ReceiveBufferSize = 4096;
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(TimeSpan.FromSeconds(15));
                var payload = new byte[SecureChannel.MaximumPayload];
                await ExpectThrowsAsync<TimeoutException>(async () =>
                {
                    for (var i = 0; i < 16384; ++i) await client.Send(payload, limit.Token);
                    throw new Exception("non-reading peer did not exert backpressure within the test bound");
                });
            });
        });
        await Check("Diagnostic write failure cannot replace a desync error or skip its abort", async () =>
        {
            using var fixture = new PreparationFixture();
            await WithChannels((client, server, token) =>
            {
                using var bridge = new NetBridge(Environment.ProcessId, 123, true);
                using var mapping = MemoryMappedFile.OpenExisting($"Local\\TH12CoopNet_{Environment.ProcessId}");
                using var memory = mapping.CreateViewAccessor();
                var manifest = new SessionManifest(SecureChannel.ProtocolVersion, NetBridge.RulesVersion, 3, 123,
                    Convert.ToHexString(client.Session), fixture.Profile.Assets);
                var peer = new LockstepPeer(client, manifest, 1);
                typeof(LockstepPeer).GetField("bridge", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(peer, bridge);
                typeof(LockstepPeer).GetField("sessionDirectory", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(peer, Path.Combine(fixture.GameDirectory, "missing-diagnostic-directory"));
                byte[] Record(byte kind, uint frame, int length)
                {
                    var data = new byte[length]; data[0] = kind; data[1] = 2;
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), SecureChannel.ProtocolVersion);
                    client.Session.AsSpan(0, 8).CopyTo(data.AsSpan(4));
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), frame);
                    return data;
                }
                peer.AcceptRecord(Record(0x12, 0, 16));
                peer.AddLocalDigest(new GameDigest(1, 123, 0, 0, 0, 0, 0, 0));
                IOException? stopped = null;
                try { peer.AcceptRecord(Record(0x11, 1, 44)); } catch (IOException error) { stopped = error; }
                Expect(stopped != null && stopped.Message.Contains("同期を停止", StringComparison.Ordinal), "diagnostic error replaced the desync reason");
                Expect(memory.ReadUInt32(28) == 3, "diagnostic error prevented desync abort");
                return Task.CompletedTask;
            });
        });
        await Check("Diagnostic tail write failure cannot replace requested cancellation", async () =>
        {
            using var fixture = new PreparationFixture();
            await WithChannels(async (client, server, token) =>
            {
                var manifest = new SessionManifest(SecureChannel.ProtocolVersion, NetBridge.RulesVersion, 3, 123,
                    Convert.ToHexString(client.Session), fixture.Profile.Assets);
                var peer = new LockstepPeer(client, manifest, 1);
                typeof(LockstepPeer).GetField("sessionDirectory", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .SetValue(peer, Path.Combine(fixture.GameDirectory, "missing-diagnostic-directory"));
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                await ExpectThrowsAsync<OperationCanceledException>(() => peer.Run(fixture.Profile, true, null, _ => { }, cancelled.Token));
                Expect(peer.GameProcessId == null, "cancelled fixture launched a game");
            });
        });
        var passed = failed == 0 && results.Count > 0;
        var report = new { passed, filter, groups = results.Count, failed, results,
            scope = "Synthetic temporary files, real mapped IPC, and actual loopback TLS with production connect/authentication/send/receive deadlines; no original game assets, native DLL loading, or game processes." };
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "managed-audit-results.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        return passed ? 0 : 1;
    }

    private static async Task WithChannels(Func<SecureChannel, SecureChannel, CancellationToken, Task> test)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        using var identity = RoomIdentity.Create();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var accepting = Accept(listener, identity, deadline.Token);
            using var client = await SecureChannel.Connect("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, identity.Invite, deadline.Token);
            using var server = await accepting;
            await test(client, server, deadline.Token);
        }
        finally { listener.Stop(); }
    }

    private sealed class PreparationFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "TH12Coop-managed-audit-" + Guid.NewGuid().ToString("N"));
        internal string GameDirectory { get; }
        internal string NativePath { get; }
        internal byte[] Configuration { get; } = new byte[60];
        internal byte[] Session { get; } = RandomNumberGenerator.GetBytes(16);
        internal OnlineProfile Profile { get; }
        internal string Destination => Path.Combine(root, "sessions", Convert.ToHexString(Session), "host");
        internal PreparationFixture()
        {
            GameDirectory = Path.Combine(root, "game");
            NativePath = Path.Combine(root, "native-fixture.bin");
            Directory.CreateDirectory(GameDirectory);
            BinaryPrimitives.WriteUInt32LittleEndian(Configuration, 0x00120001);
            var hashes = new Dictionary<string, string>();
            foreach (var name in new[] { "th12.exe", "th12.dat", "thbgm.dat", "d3dx9_40.dll" })
            {
                var bytes = RandomNumberGenerator.GetBytes(64);
                File.WriteAllBytes(Path.Combine(GameDirectory, name), bytes);
                hashes.Add(name, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            }
            File.WriteAllBytes(NativePath, [1, 2, 3]);
            var assets = new AssetProfile(hashes["th12.exe"], hashes["th12.dat"], hashes["thbgm.dat"],
                Convert.ToHexString(SHA256.HashData([1, 2, 3])).ToLowerInvariant(), Convert.ToHexString(SHA256.HashData(Configuration)),
                "retail-1.00b", GameBuild.DependencyDigest(hashes));
            // Bypass only production game identification: these are synthetic files,
            // while the actual preparation implementation runs unchanged.
            Profile = (OnlineProfile)typeof(OnlineProfile).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
                .Invoke([GameDirectory, NativePath, Configuration, assets, hashes.Keys.ToArray()]);
        }
        public void Dispose() => Directory.Delete(root, true);
    }
}
