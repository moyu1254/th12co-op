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
        var invite = "T12L6." + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64));
        SourceLimits(invite);
        IPv6PrefixLimits();
        IndependentOperationLimits();
        RollingExpiration();
        ReservationCapacity(invite);
        DelayedRegistration(invite);
        await Signaling(invite);
        Console.WriteLine("PASS 48 reservations per admission window, expiry from admission, IPv4/IPv6 prefix budgets and independent global limits");
        await HttpsLifecycle(invite);
        Console.WriteLine("PASS HTTPS address proof, single-use join, authenticated ICE exchange, cancellation and request bounds");
        return 0;
    }

    private static void IPv6PrefixLimits()
    {
        foreach (var (operation, per56, per48) in new[] { (Operation.Create, 8, 12), (Operation.Join, 12, 24), (Operation.Proof, 48, 72),
            (Operation.Offer, 24, 36), (Operation.Answer, 24, 36), (Operation.Receive, 24, 36), (Operation.Close, 24, 36) })
        {
            var rooms = new RoomDirectory(new ManualTime());
            int admitted = 0;
            // The fourth hextet's low byte varies the /64 within one /56.
            for (var subnet = 0; subnet < 256; subnet++)
                if (rooms.Admit(operation, IPAddress.Parse($"2001:db8:101:{subnet:x}::1"), out _)) admitted++;
            Check(admitted == per56, "one IPv6 /56 shares a budget across all its /64s");
            // Other /56s can still use the remaining /48 budget. Rejections above spent none of it.
            for (var subnet = 1; subnet < 256; subnet++)
                if (rooms.Admit(operation, IPAddress.Parse($"2001:db8:101:{subnet << 8:x}::1"), out _)) admitted++;
            Check(admitted == per48, "one IPv6 /48 shares a budget across its /56s");
            Check(rooms.Admit(operation, IPAddress.Parse("2001:db8:102::1"), out _), "unrelated IPv6 /48 still admitted");
        }
    }

    private static void IndependentOperationLimits()
    {
        var rooms = new RoomDirectory(new ManualTime());
        var source = 0;
        foreach (var (op, limit) in new[] { (Operation.Create, 48), (Operation.Close, 96), (Operation.Join, 120),
            (Operation.Proof, 384), (Operation.Offer, 96), (Operation.Answer, 96), (Operation.Receive, 120) })
        {
            // Each accepted request comes from a distinct /48. All budgeted requests fit,
            // including new sources arriving after the old 256-source table would have filled.
            for (var i = 0; i < limit; i++)
                Check(rooms.Admit(op, IPAddress.Parse($"2001:db8:{++source:x}::1"), out _), "independent operation capacity");
            Check(!rooms.Admit(op, IPAddress.Loopback, out _), "global operation budget enforced");
        }
    }

    private static void RollingExpiration()
    {
        var clock = new ManualTime();
        var rooms = new RoomDirectory(clock);
        var source = IPAddress.Loopback;
        Check(rooms.Admit(Operation.Create, source, out _), "first create");
        clock.Now = RoomDirectory.Lifetime.Ticks - 1;
        for (var i = 0; i < 3; i++) Check(rooms.Admit(Operation.Create, source, out _), "remaining creates");
        clock.Now++;
        Check(rooms.Admit(Operation.Create, source, out _), "one expired admission reclaimed");
        Check(!rooms.Admit(Operation.Create, source, out _), "recent admissions survive the ten-minute boundary");
        clock.Now += RoomDirectory.Lifetime.Ticks;
        for (var i = 0; i < 4; i++) Check(rooms.Admit(Operation.Create, source, out _), "expired budget reclaimed without a timer tick");
    }

    private static void ReservationCapacity(string invite)
    {
        var clock = new ManualTime();
        var rooms = new RoomDirectory(clock);
        var sources = Enumerable.Range(1, 48).Select(i => IPAddress.Parse($"2001:db8:{i:x}::1")).ToArray();
        for (var window = 0; window < 2; window++)
        {
            clock.Now = window * RoomDirectory.Lifetime.Ticks;
            var admissions = new long[sources.Length];
            for (var i = 0; i < sources.Length; i++)
                Check(rooms.Admit(Operation.Create, sources[i], out admissions[i]), "create within global budget");
            if (window == 0) clock.Now += TimeSpan.TicksPerSecond;
            var numbers = new HashSet<string>();
            for (var i = 0; i < sources.Length; i++)
            {
                var room = rooms.Create(invite, admissions[i])
                    ?? throw new InvalidOperationException("accepted create could not reserve a number");
                Check(numbers.Add(room.Number), "cancelled numbers stay reserved until expiry");
                Check(rooms.Admit(Operation.Close, sources[i], out _), "owner close within budget");
                Check(rooms.Close(room.Number, Convert.FromHexString(room.OwnerToken)), "owner retired its number");
            }
            Check(!rooms.Admit(Operation.Create, IPAddress.Loopback, out _), "49th create denied at admission");
        }
    }

    private static void DelayedRegistration(string invite)
    {
        var clock = new ManualTime();
        var rooms = new RoomDirectory(clock);
        var source = IPAddress.Loopback;
        Check(rooms.Admit(Operation.Create, source, out var issuedAt), "admit delayed registration");
        Check(rooms.Admit(Operation.Create, source, out var unfinishedAt), "admit unfinished registration");
        clock.Now += TimeSpan.TicksPerSecond;
        var issued = rooms.Create(invite, issuedAt) ?? throw new InvalidOperationException("delayed registration failed");
        Check(RoomDirectory.ValidNumber(issued.Number) && RoomDirectory.ValidToken(issued.OwnerToken), "credential encoding");
        clock.Now = RoomDirectory.Lifetime.Ticks;
        Check(rooms.Admit(Operation.Join, source, out _) && !rooms.Close(issued.Number, Convert.FromHexString(issued.OwnerToken)), "TTL starts at admission");
        Check(rooms.Create(invite, unfinishedAt) == null, "expired admission cannot issue a number");
    }

    private static void SourceLimits(string invite)
    {
        Check(RoomDirectory.ValidInvite(invite) && !RoomDirectory.ValidInvite(invite[..^1] + "B"), "canonical invite");
        var clock = new ManualTime();
        var rooms = new RoomDirectory(clock);
        var source = IPAddress.Loopback;
        var ipv6 = IPAddress.Parse("2001:db8::1");
        for (var i = 0; i < 6; ++i) Check(rooms.Admit(Operation.Join, ipv6, out _), "individual join budget");
        Check(!rooms.Admit(Operation.Join, IPAddress.Parse("2001:db8::dead"), out _), "IPv6 /64");
        Check(rooms.Admit(Operation.Close, ipv6, out _), "independent cancellation");
        for (var i = 0; i < 114; ++i)
            Check(rooms.Admit(Operation.Join, new IPAddress(new byte[] { 192, 0, 2, (byte)(1 + i / 6) }), out _), "global budget");
        Check(!rooms.Admit(Operation.Join, source, out _), "global join limit");
        clock.Now += RoomDirectory.Lifetime.Ticks;
        Check(rooms.Admit(Operation.Join, ipv6, out _), "budget replenished");
        for (var i = 0; i < 4; ++i) Check(rooms.Admit(Operation.Create, source, out _), "individual create budget");
        Check(!rooms.Admit(Operation.Create, source.MapToIPv6(), out _), "IPv4-mapped address");
    }

    private static IceDescription Description(params IceEndpoint[] endpoints) =>
        new("abcd", new string('A', 24), endpoints);

    private static async Task Signaling(string invite)
    {
        var clock = new ManualTime();
        var rooms = new RoomDirectory(clock);
        var v4 = IPAddress.Parse("8.8.8.8");
        var v6 = IPAddress.Parse("2606:4700::1");
        string Proof(IPAddress source) => rooms.IssueProof(source, clock.Now)!.Token;
        var offer = new VerifiedIce(Description(new(v6.ToString(), 33001), new(v4.ToString(), 43001)), [Proof(v4), Proof(v6)]);
        var answer = new VerifiedIce(Description(new IceEndpoint(v4.ToString(), 33002)), [Proof(v4)]);
        var room = rooms.Create(invite, clock.Now)!;
        var owner = Convert.FromHexString(room.OwnerToken);
        var wait = rooms.Wait(room.Number, owner, true, default);
        Check(!wait.IsCompleted && await rooms.Wait(room.Number, owner, true, default) == null, "one owner waiter");
        Check(rooms.Join(room.Number, offer with { Proofs = [Proof(v4)] }, v4) == null,
            "cannot publish a second family without its proof");
        var claim = rooms.Join(room.Number, offer, v4)!;
        var guest = Convert.FromHexString(claim.GuestToken);
        Check(await wait == offer.Description && rooms.Join(room.Number, offer, v4) == null,
            "both proven address families reach one owner");
        Check(await rooms.Wait(room.Number, guest, true, default) == null &&
              await rooms.Wait(room.Number, owner, false, default) == null &&
              !rooms.Answer(room.Number, guest, answer, v4), "capability cannot switch roles");
        var receive = rooms.Wait(room.Number, guest, false, default);
        Check(!receive.IsCompleted && rooms.Answer(room.Number, owner, answer, v4) &&
            await receive == answer.Description && !rooms.Answer(room.Number, owner, answer, v4) &&
            await rooms.Wait(room.Number, guest, false, default) == null, "answer wakes guest and retires room");
        var closing = rooms.Create(invite, clock.Now)!;
        var owner2 = Convert.FromHexString(closing.OwnerToken);
        var pending = rooms.Wait(closing.Number, owner2, true, default);
        Check(!rooms.Close(closing.Number, guest) && !pending.IsCompleted &&
            rooms.Close(closing.Number, owner2) && await pending == null, "close only wakes the owner's room");
        var expiring = rooms.Create(invite, clock.Now)!;
        var expired = rooms.Wait(expiring.Number, Convert.FromHexString(expiring.OwnerToken), true, default);
        clock.Now += RoomDirectory.Lifetime.Ticks;
        rooms.RemoveExpired();
        var fresh = rooms.Create(invite, clock.Now)!;
        Check(await expired == null && rooms.Join(fresh.Number, offer, v4) == null, "expiry drops pending waits and proofs");
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
            using var phase1 = await client.PostAsJsonAsync("/v1/rooms/join", new { number = "000000" });
            Check(phase1.StatusCode == HttpStatusCode.NotFound, "old API rejected");
            using var proofResponse = await client.PostAsJsonAsync("/v2/rooms/proof", new { });
            var proof = (await proofResponse.Content.ReadFromJsonAsync<AddressProof>())!;
            Check(proof.Address == IPAddress.Loopback.ToString(), "proof uses socket source, ignores forwarded IP");
            var ice = new VerifiedIce(Description(new IceEndpoint(proof.Address, 3012)), [proof.Token]);
            async Task<Registration> Create()
            {
                using var response = await client.PostAsJsonAsync("/v2/rooms/create", new { invite });
                Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true, "create over TLS");
                return (await response.Content.ReadFromJsonAsync<Registration>())!;
            }
            async Task Close(Registration registration)
            {
                using var response = await client.PostAsJsonAsync("/v2/rooms/close", registration);
                Check(response.StatusCode == HttpStatusCode.NoContent, "idempotent close");
            }
            async Task<Claim?> Join(string number)
            {
                using var response = await client.PostAsJsonAsync("/v2/rooms/join", new { number, offer = ice });
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                Check(response.StatusCode == HttpStatusCode.OK, "join response");
                return await response.Content.ReadFromJsonAsync<Claim>();
            }
            var first = await Create();
            await Close(first with { OwnerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) });
            var matches = await Task.WhenAll(Join(first.Number), Join(first.Number));
            var claim = matches.OfType<Claim>().Single();
            Check(claim.Invite == invite && RoomDirectory.ValidToken(claim.GuestToken), "one guest, original invite");
            using var offer = await client.PostAsJsonAsync("/v2/rooms/offer", first);
            Check(offer.StatusCode == HttpStatusCode.OK, "owner obtains offer");
            using var answered = await client.PostAsJsonAsync("/v2/rooms/answer", new { first.Number, first.OwnerToken, ice });
            Check(answered.StatusCode == HttpStatusCode.NoContent, "owner submits answer");
            using var receive = await client.PostAsJsonAsync("/v2/rooms/receive", new { first.Number, claim.GuestToken });
            var received = await receive.Content.ReadFromJsonAsync<IceDescription>();
            Check(received?.Candidates.Single() == ice.Description.Candidates.Single(), "claimed guest obtains answer");
            var second = await Create();
            await Close(second);
            await Close(second);
            Check(await Join(second.Number) == null, "owner can revoke");
            using var old = await client.PostAsJsonAsync("/v2/rooms/create", new { invite = "T12L5." + invite[6..] });
            Check(old.StatusCode == HttpStatusCode.BadRequest, "old invite");
            using var oversizedBody = new StringContent(new string(' ', 4097), Encoding.UTF8, "application/json");
            using var oversized = await client.PostAsync("/v2/rooms/join", oversizedBody);
            Check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "oversized body");
            using var overrideAddress = await client.PostAsJsonAsync("/v2/rooms/create", new { invite, address = "192.0.2.44" });
            Check(overrideAddress.StatusCode == HttpStatusCode.BadRequest, "unexpected address field");
            using var limited = await client.PostAsJsonAsync("/v2/rooms/create", new { invite });
            Check(limited.StatusCode == HttpStatusCode.TooManyRequests, "exhausted create budget returns 429");
        }
        finally { await app.StopAsync(); }
    }
}
