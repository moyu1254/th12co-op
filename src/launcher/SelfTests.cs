using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace TH12Coop;

internal static partial class SelfTests
{
    public static async Task<int> Run()
    {
        var passed = new List<string>();
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var token = lifetime.Token;
        try
        {
            RunInputTests(passed);
            await LocalConfigurationTests(passed, token);
            using var identity = RoomIdentity.Create();
            var parsed = RoomIdentity.Parse(identity.Invite);
            Expect(parsed.CertificateHash.Length == 32 && parsed.Capability.Length == 32, "invite strength");
            CryptographicOperations.ZeroMemory(parsed.Capability);
            ExpectThrows<ArgumentException>(() => RoomIdentity.Parse("bad"));
            ExpectThrows<ArgumentException>(() => RoomIdentity.Parse("T12L2." + identity.Invite[6..]));
            ExpectThrows<ArgumentException>(() => RoomIdentity.Parse("T12L3." + identity.Invite[6..]));
            passed.Add("Versioned pinned certificate invite + independent 256-bit guest capability");

            foreach (var build in GameBuild.Supported) Expect(GameBuild.FromHash(build.ExeHash.ToUpperInvariant()) == build, "known game identity");
            ExpectThrows<IOException>(() => GameBuild.FromHash(new string('0', 64)));
            AssetProfile Profile(string id)
            {
                var build = GameBuild.Supported.Single(build => build.Id == id);
                return new AssetProfile(build.ExeHash, build.DatHash, GameBuild.MusicHash, "native", "cfg", build.Id, "deps");
            }
            var expectedAssets = Profile("retail-1.00b");
            var steamAssets = Profile("steam-1.00b");
            var oldAssets = Profile("retail-1.00a");
            OnlineProfile.ValidateAssets(expectedAssets, expectedAssets with { });
            OnlineProfile.ValidateAssets(steamAssets, steamAssets);
            OnlineProfile.ValidateAssets(oldAssets, oldAssets);
            OnlineProfile.ValidateAssets(expectedAssets, steamAssets);
            OnlineProfile.ValidateAssets(steamAssets, expectedAssets);
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(expectedAssets, oldAssets));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(oldAssets, expectedAssets));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(steamAssets, oldAssets));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(expectedAssets, expectedAssets with { Build = "steam-1.00b" }));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(expectedAssets, steamAssets with { Build = "retail-1.00b" }));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(expectedAssets, expectedAssets with { Dat = oldAssets.Dat }));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(expectedAssets, expectedAssets with { Bgm = "unknown" }));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(expectedAssets, expectedAssets with { Native = "different" }));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(expectedAssets, expectedAssets with { Configuration = "different" }));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(expectedAssets, expectedAssets with { Dependencies = "different" }));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(expectedAssets, expectedAssets with { Exe = "different" }));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(expectedAssets, null));
            var unknownAssets = expectedAssets with { Exe = new string('0', 64) };
            ExpectThrows<IOException>(() => OnlineProfile.ValidateAssets(unknownAssets, unknownAssets));
            var rulesSession = RandomNumberGenerator.GetBytes(32);
            var rulesManifest = new SessionManifest(SecureChannel.ProtocolVersion, NetBridge.RulesVersion, 3, 123, Convert.ToHexString(rulesSession), expectedAssets);
            OnlineProfile.ValidateRules(rulesManifest, rulesSession);
            ExpectThrows<IOException>(() => OnlineProfile.ValidateRules(rulesManifest with { Rules = NetBridge.RulesVersion - 1 }, rulesSession));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateRules(rulesManifest with { Seed = 0 }, rulesSession));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateRules(rulesManifest with { Delay = 13 }, rulesSession));
            ExpectThrows<IOException>(() => OnlineProfile.ValidateRules(rulesManifest with { Session = "different" }, rulesSession));
            passed.Add("Three known profiles accepted; verified retail/Steam 1.00b compatible; 1.00a/1.00b, forged identities and MOD/config/dependency mismatches rejected");
            passed.Add("Cooperative rules version, seed, delay and session identity checked; old rules rejected");

            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var accepting = Accept(listener, identity, token);
                SecureChannel connected;
                try { connected = await SecureChannel.Connect("127.0.0.1", port, identity.Invite, token); }
                catch (Exception clientError)
                {
                    try { using var failedServer = await accepting; }
                    catch (Exception serverError) { throw new AggregateException(clientError, serverError); }
                    throw;
                }
                using var client = connected;
                using var server = await accepting;
                Expect(client.Session.SequenceEqual(server.Session), "session identity");
                var data = RandomNumberGenerator.GetBytes(SecureChannel.MaximumPayload);
                var receiving = server.Receive(token);
                await client.Send(data, token);
                Expect((await receiving).SequenceEqual(data), "TLS bounded payload");
                await server.Send([5, 6, 7], token);
                Expect((await client.Receive(token)).SequenceEqual(new byte[] { 5, 6, 7 }), "TLS duplex");
                await ExpectThrowsAsync<IOException>(() => client.Send(new byte[4097], token));
                passed.Add("Actual TLS 1.3 pinned connection, mutual role/capability binding, duplex records and 4096-byte bound");

                var manifest = new SessionManifest(SecureChannel.ProtocolVersion, NetBridge.RulesVersion, 3, 123, Convert.ToHexString(client.Session), new AssetProfile("a", "b", "c", "d", "e", "retail-1.00a", "f"));
                var peer = new LockstepPeer(client, manifest, 1);
                var epoch = BinaryPrimitives.ReadUInt64LittleEndian(client.Session);
                byte[] Record(byte kind, uint frame, int size = 16, byte role = 2)
                {
                    var record = new byte[size]; record[0] = kind; record[1] = role;
                    BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), SecureChannel.ProtocolVersion);
                    BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(4), epoch);
                    BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12), frame);
                    return record;
                }
                ExpectThrows<IOException>(() => peer.AcceptRecord(Record(0x12, 0, role: 1)));
                peer.AcceptRecord(Record(0x12, 0));
                var input = Record(0x10, 4, 20); BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(16), 1);
                peer.AcceptRecord(input);
                ExpectThrows<IOException>(() => peer.AcceptRecord(input));
                var invalidMask = Record(0x10, 5, 20); BinaryPrimitives.WriteUInt32LittleEndian(invalidMask.AsSpan(16), 4);
                ExpectThrows<IOException>(() => peer.AcceptRecord(invalidMask));
                ExpectThrows<IOException>(() => peer.AcceptRecord(Record(0x10, 6, 20)));
                var wrongEpoch = Record(0x13, 0); wrongEpoch[4] ^= 1;
                ExpectThrows<IOException>(() => peer.AcceptRecord(wrongEpoch));
                passed.Add("Wrong role, wrong session, duplicate/skipped frame and unknown input bit rejected");

                var digestPeer = new LockstepPeer(client, manifest, 1);
                digestPeer.AcceptRecord(Record(0x12, 0));
                digestPeer.AddLocalDigest(new GameDigest(1, 100, 200, 7, 1, 0, 10, 123));
                var badDigest = Record(0x11, 1, 44);
                uint[] words = [101, 200, 7, 1, 0, 10, 123];
                for (var i = 0; i < words.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(badDigest.AsSpan(16 + i * 4), words[i]);
                ExpectThrows<IOException>(() => digestPeer.AcceptRecord(badDigest));
                var flooding = new LockstepPeer(client, manifest, 1);
                for (var i = 0; i < 8; ++i) flooding.AcceptRecord(Record(0x13, 0));
                ExpectThrows<IOException>(() => flooding.AcceptRecord(Record(0x13, 0)));
                passed.Add("Native digest mismatch and authenticated peer heartbeat flood rejected");
            }
            finally { listener.Stop(); }

            using var wrongIdentity = RoomIdentity.Create();
            await RejectedConnection(identity, wrongIdentity.Invite, token);
            var correct = RoomIdentity.Parse(identity.Invite);
            var wrongCapability = "T12L4." + Convert.ToBase64String(correct.CertificateHash.Concat(RandomNumberGenerator.GetBytes(32)).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            CryptographicOperations.ZeroMemory(correct.Capability);
            await RejectedConnection(identity, wrongCapability, token);
            passed.Add("Wrong TLS certificate pin and wrong guest capability fail closed");

            foreach (var length in new[] { -1, 0, 4097, int.MaxValue }) await MalformedRecord(identity, length, false, token);
            await MalformedRecord(identity, 0, true, token);
            passed.Add("Negative/zero/oversized record prefix rejected before payload read; protocol mismatch rejected");
            await MalformedRecord(identity, 4, false, token, 1);
            await MalformedRecord(identity, 4, false, token, 2);
            passed.Add("Authenticated truncated body rejected and stalled body read cancelled at its deadline");

            var concurrentListener = new TcpListener(IPAddress.Loopback, 0); concurrentListener.Start();
            try
            {
                var concurrentPort = ((IPEndPoint)concurrentListener.LocalEndpoint).Port;
                var accepting = OnlineSession.AcceptGuest(concurrentListener, identity, token);
                using var idle = new TcpClient();
                await idle.ConnectAsync(IPAddress.Loopback, concurrentPort, token);
                using var valid = await SecureChannel.Connect("127.0.0.1", concurrentPort, identity.Invite, token);
                using var accepted = await accepting;
                Expect(accepted.Session.SequenceEqual(valid.Session), "concurrent authenticated admission");
            }
            finally { concurrentListener.Stop(); }
            passed.Add("Actual room admission accepts invited guest while an unauthenticated TLS socket is idle");

            using (var bridge = new NetBridge(Environment.ProcessId, 123, true))
            using (var mapping = MemoryMappedFile.OpenExisting($"Local\\TH12CoopNet_{Environment.ProcessId}"))
            using (var memory = mapping.CreateViewAccessor())
            {
                memory.Write(36, 1u);
                bridge.Commit(1, 1, 0x80);
                Expect(memory.ReadUInt32(272) == 1 && memory.ReadUInt32(280) == 0x80, "sequence-last committed input");
                ExpectThrows<IOException>(() => bridge.Commit(1, 2, 0));
                ExpectThrows<IOException>(() => bridge.Commit(2, 1, 0));
                memory.Write(40, 1u); memory.Write(36, 2u);
                bridge.Commit(2, 0x100, 1);
                bridge.Abort(3);
                Expect(memory.ReadUInt32(28) == 3, "native fail-closed abort");
            }
            passed.Add("Real mapped IPC rejects duplicate/ahead commits and signals native abort");
            if (await ManagedAudit() != 0) throw new IOException("Managed preparation/transport regression suite failed; see managed-audit-results.json.");
            passed.Add("Managed preparation, asset drift, IPC and actual TLS deadline regression suite");
            var result = string.Join(Environment.NewLine, passed.Select(line => "PASS " + line)) + Environment.NewLine + $"PASS {passed.Count} test groups";
            Console.WriteLine(result);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launcher-test-results.txt"), result);
            return 0;
        }
        catch (Exception error)
        {
            var result = string.Join(Environment.NewLine, passed) + Environment.NewLine + "FAIL " + error;
            Console.Error.WriteLine(result);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "launcher-test-results.txt"), result);
            return 1;
        }
    }

    private static async Task<SecureChannel> Accept(TcpListener listener, RoomIdentity identity, CancellationToken token)
        => await SecureChannel.Accept(await listener.AcceptTcpClientAsync(token), identity, token);

    private static async Task RejectedConnection(RoomIdentity identity, string invite, CancellationToken token)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var accept = Accept(listener, identity, token);
            try
            {
                using var unexpected = await SecureChannel.Connect("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port, invite, token);
                throw new Exception("Unauthenticated connection accepted");
            }
            catch (Exception error) when (error is AuthenticationException or IOException) { }
            try { using var unexpected = await accept; throw new Exception("Server accepted unauthenticated peer"); }
            catch (Exception error) when (error is AuthenticationException or IOException) { }
        }
        finally { listener.Stop(); }
    }

    private static async Task MalformedRecord(RoomIdentity identity, int length, bool wrongVersion, CancellationToken token, int bodyTest = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        try
        {
            var accept = Accept(listener, identity, token);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
            using var tls = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => cert != null && cert.GetRawCertData().AsSpan().SequenceEqual(identity.Certificate.RawData));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "th12coop.invalid", EnabledSslProtocols = SslProtocols.Tls13 }, token);
            var hello = new byte[40]; Encoding.ASCII.GetBytes("T12L").CopyTo(hello, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(hello.AsSpan(4), wrongVersion ? (ushort)99 : SecureChannel.ProtocolVersion); hello[6] = 2;
            identity.Capability.CopyTo(hello, 8);
            await tls.WriteAsync(hello, token);
            if (wrongVersion) { await ExpectThrowsAsync<AuthenticationException>(async () => { using var unexpected = await accept; }); return; }
            var acknowledgement = new byte[24]; await tls.ReadExactlyAsync(acknowledgement, token);
            using var server = await accept;
            var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, length);
            await tls.WriteAsync(header, token);
            if (bodyTest == 1) { await tls.WriteAsync(new byte[] { 1, 2 }, token); tls.Dispose(); }
            if (bodyTest == 2)
            {
                await tls.WriteAsync(new byte[] { 1 }, token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(200);
                await ExpectThrowsAsync<OperationCanceledException>(async () => await server.Receive(deadline.Token));
                return;
            }
            await ExpectThrowsAsync<IOException>(async () => await server.Receive(token));
        }
        finally { listener.Stop(); }
    }

    private static void Expect(bool value, string label) { if (!value) throw new Exception(label); }
    private static void ExpectThrows<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    private static async Task ExpectThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
}
