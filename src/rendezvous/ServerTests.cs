using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TH12Coop.Rendezvous;

internal static class ServerTests
{
    private sealed class ManualTime : TimeProvider
    {
        internal long Now;
        public override long GetTimestamp() => Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    private static void Check(bool ok, string name)
    {
        if (!ok) throw new InvalidOperationException(name);
    }

    internal static async Task<int> Run()
    {
        var invite = "T12L5." + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64));
        CapacityAndLimits(invite);
        Console.WriteLine("PASS capacity, expiration and source/global budgets");
        await HttpsLifecycle(invite);
        Console.WriteLine("PASS HTTPS create, atomic claim, owner cancellation and request bounds");
        return 0;
    }

    private static void CapacityAndLimits(string invite)
    {
        Check(RoomDirectory.ValidInvite(invite) && !RoomDirectory.ValidInvite(invite[..^1] + "B"), "canonical invite");
        var clock = new ManualTime();
        var rooms = new RoomDirectory(clock);
        var source = IPAddress.Loopback;
        var first = rooms.Create(invite, source)!;
        Check(RoomDirectory.ValidNumber(first.Number) && RoomDirectory.ValidToken(first.OwnerToken), "credential encoding");
        for (var i = 1; i < 32; ++i) Check(rooms.Create(invite, source) != null, "capacity");
        Check(rooms.Create(invite, source) == null, "full");
        clock.Now += RoomDirectory.Lifetime.Ticks;
        Check(rooms.Take(first.Number) == null && rooms.Create(invite, source) != null, "TTL and capacity reclaimed");
        var ipv6 = IPAddress.Parse("2001:db8::1");
        for (var i = 0; i < 6; ++i) Check(rooms.Admit(Operation.Join, ipv6), "individual join budget");
        Check(!rooms.Admit(Operation.Join, IPAddress.Parse("2001:db8::dead")), "IPv6 /64");
        Check(rooms.Admit(Operation.Close, ipv6), "independent cancellation");
        for (var i = 0; i < 114; ++i)
            Check(rooms.Admit(Operation.Join, new IPAddress(new byte[] { 192, 0, 2, (byte)(1 + i / 6) })), "global budget");
        Check(!rooms.Admit(Operation.Join, source), "global join limit");
        clock.Now += RoomDirectory.Lifetime.Ticks;
        Check(rooms.Admit(Operation.Join, ipv6), "budget replenished");
        for (var i = 0; i < 4; ++i) Check(rooms.Admit(Operation.Create, source), "individual create budget");
        Check(!rooms.Admit(Operation.Create, source.MapToIPv6()), "IPv4-mapped address");
    }

    internal static X509Certificate2 TestCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var pfx = generated.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    private static async Task HttpsLifecycle(string invite)
    {
        using var certificate = TestCertificate();
        await using var app = Program.Build(certificate, new(), IPAddress.Loopback, 0, TimeProvider.System);
        await app.StartAsync();
        try
        {
            var handler = new HttpClientHandler
            {
                UseProxy = false,
                SslProtocols = SslProtocols.Tls13,
                ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer != null && peer.RawData.AsSpan().SequenceEqual(certificate.RawData)
            };
            using var client = new HttpClient(handler) { BaseAddress = new(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.Add("X-Forwarded-For", "192.0.2.44");
            async Task<Registration> Create()
            {
                using var response = await client.PostAsJsonAsync("/v1/rooms/create", new { invite, port = RoomDirectory.Port });
                Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true, "create over TLS");
                return (await response.Content.ReadFromJsonAsync<Registration>())!;
            }
            async Task Close(Registration registration)
            {
                using var response = await client.PostAsJsonAsync("/v1/rooms/close", registration);
                Check(response.StatusCode == HttpStatusCode.NoContent, "idempotent close");
            }
            async Task<Candidate?> Join(string number)
            {
                using var response = await client.PostAsJsonAsync("/v1/rooms/join", new { number });
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                Check(response.StatusCode == HttpStatusCode.OK, "join response");
                return await response.Content.ReadFromJsonAsync<Candidate>();
            }
            var first = await Create();
            await Close(first with { OwnerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) });
            var matches = await Task.WhenAll(Join(first.Number), Join(first.Number));
            Check(matches.OfType<Candidate>().Single() == new Candidate(invite, IPAddress.Loopback.ToString(), RoomDirectory.Port),
                "one guest, original invite and observed socket address");
            var second = await Create();
            await Close(second);
            await Close(second);
            Check(await Join(second.Number) == null, "owner can revoke");
            using var old = await client.PostAsJsonAsync("/v1/rooms/create", new { invite = "T12L4." + invite[6..], port = RoomDirectory.Port });
            Check(old.StatusCode == HttpStatusCode.BadRequest, "old invite");
            using var oversizedBody = new StringContent(new string(' ', 513), Encoding.UTF8, "application/json");
            using var oversized = await client.PostAsync("/v1/rooms/join", oversizedBody);
            Check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "oversized body");
            using var overrideAddress = await client.PostAsJsonAsync("/v1/rooms/create", new { invite, port = RoomDirectory.Port, address = "192.0.2.44" });
            Check(overrideAddress.StatusCode == HttpStatusCode.BadRequest, "unexpected address field");
        }
        finally { await app.StopAsync(); }
    }
}
