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
        IPv6PrefixLimits();
        IndependentOperationLimits();
        RollingExpiration();
        ReservationHeadroom(invite);
        Console.WriteLine("PASS room capacity, IPv4/IPv6 prefix budgets, rolling expiration and independent global limits");
        await HttpsLifecycle(invite);
        Console.WriteLine("PASS HTTPS create, atomic claim, owner cancellation and request bounds");
        return 0;
    }

    private static void IPv6PrefixLimits()
    {
        foreach (var (operation, per56, per48) in new[] { (Operation.Create, 8, 12), (Operation.Join, 12, 24), (Operation.Close, 24, 36) })
        {
            var rooms = new RoomDirectory(new ManualTime());
            int admitted = 0;
            // The fourth hextet's low byte varies the /64 within one /56.
            for (var subnet = 0; subnet < 256; subnet++)
                if (rooms.Admit(operation, IPAddress.Parse($"2001:db8:101:{subnet:x}::1"))) admitted++;
            Check(admitted == per56, "one IPv6 /56 shares a budget across all its /64s");
            // Other /56s can still use the remaining /48 budget. Rejections above spent none of it.
            for (var subnet = 1; subnet < 256; subnet++)
                if (rooms.Admit(operation, IPAddress.Parse($"2001:db8:101:{subnet << 8:x}::1"))) admitted++;
            Check(admitted == per48, "one IPv6 /48 shares a budget across its /56s");
            Check(rooms.Admit(operation, IPAddress.Parse("2001:db8:102::1")), "unrelated IPv6 /48 still admitted");
        }
    }

    private static void IndependentOperationLimits()
    {
        var rooms = new RoomDirectory(new ManualTime());
        var source = 0;
        foreach (var (op, limit) in new[] { (Operation.Create, 48), (Operation.Close, 96), (Operation.Join, 120) })
        {
            // Each accepted request comes from a distinct /48. All 264 budgeted requests fit,
            // including new sources arriving after the old 256-source table would have filled.
            for (var i = 0; i < limit; i++)
                Check(rooms.Admit(op, IPAddress.Parse($"2001:db8:{++source:x}::1")), "independent operation capacity");
            Check(!rooms.Admit(op, IPAddress.Loopback), "global operation budget enforced");
        }
    }

    private static void RollingExpiration()
    {
        var clock = new ManualTime();
        var rooms = new RoomDirectory(clock);
        var source = IPAddress.Loopback;
        Check(rooms.Admit(Operation.Create, source), "first create");
        clock.Now = RoomDirectory.Lifetime.Ticks - 1;
        for (var i = 0; i < 3; i++) Check(rooms.Admit(Operation.Create, source), "remaining creates");
        clock.Now++;
        Check(rooms.Admit(Operation.Create, source), "one expired admission reclaimed");
        Check(!rooms.Admit(Operation.Create, source), "recent admissions survive the ten-minute boundary");
        clock.Now += RoomDirectory.Lifetime.Ticks;
        for (var i = 0; i < 4; i++) Check(rooms.Admit(Operation.Create, source), "expired budget reclaimed without a timer tick");
    }

    private static void ReservationHeadroom(string invite)
    {
        var clock = new ManualTime();
        var rooms = new RoomDirectory(clock);
        var sources = Enumerable.Range(0, 16).Select(i => IPAddress.Parse($"2001:db8:101:{i << 8:x}::1")).ToArray();
        for (var window = 0; window < 2; window++)
        {
            clock.Now = window * RoomDirectory.Lifetime.Ticks;
            var admitted = sources.Where(ip => rooms.Admit(Operation.Create, ip)).ToArray();
            Check(admitted.Length == 12, "create budget per /48");
            // The first batch finishes reading JSON after admission. Its number reservations
            // are still occupied (including claimed rooms) when the next batch arrives.
            if (window == 0) clock.Now += TimeSpan.TicksPerSecond;
            foreach (var ip in admitted)
            {
                var room = rooms.Create(invite, ip);
                Check(room != null && rooms.Take(room.Number) != null, "single-claim registration");
            }
        }
        var other = IPAddress.Parse("2001:db8:102::1");
        Check(rooms.Admit(Operation.Create, other) && rooms.Create(invite, other) != null,
            "unrelated /48 can register while retired numbers from both batches occupy slots");
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
