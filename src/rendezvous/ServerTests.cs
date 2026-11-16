using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

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
        var invite = "T12L7." + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64));
        SourceLimits(invite);
        IPv6PrefixLimits();
        IndependentOperationLimits();
        RollingExpiration();
        ReservationCapacity(invite);
        DelayedRegistration(invite);
        await Signaling(invite);
        Console.WriteLine("PASS 48 reservations per admission window, expiry from admission, IPv4/IPv6 prefix budgets and independent global limits");
        await PollBudgets(invite, host: true);
        await PollBudgets(invite, host: false);
        Console.WriteLine("PASS busy and overlapping polls preserve offer/receive budgets; new waits, bad credentials and replayed reads remain metered");
        await HttpsLifecycle(invite);
        Console.WriteLine("PASS HTTPS address proof, single-use join, authenticated ICE exchange, cancellation and request bounds");
        await RelayServerTests.Run(invite);
        return 0;
    }

    private static void IPv6PrefixLimits()
    {
        foreach (var (operation, per56, per48) in new[] { (Operation.Create, 8, 12), (Operation.Join, 12, 24), (Operation.Proof, 48, 72),
            (Operation.Offer, 320, 480), (Operation.Answer, 24, 36), (Operation.Receive, 640, 1280), (Operation.Close, 24, 36),
            (Operation.Relay, 48, 72) })
        {
            var rooms = new RoomDirectory(new ManualTime());
            int admitted = 0;
            // The fourth hextet's low byte varies the /64 within one /56.
            for (var i = 0; i <= per56; i++)
                if (rooms.Admit(operation, IPAddress.Parse($"2001:db8:101:{i % 256:x}::1"), out _)) admitted++;
            Check(admitted == per56, "one IPv6 /56 shares a budget across all its /64s");
            // Other /56s can still use the remaining /48 budget. Rejections above spent none of it.
            for (var i = 0; i <= per48 - per56; i++)
                if (rooms.Admit(operation, IPAddress.Parse($"2001:db8:101:{(1 + i % 255) << 8:x}::1"), out _)) admitted++;
            Check(admitted == per48, "one IPv6 /48 shares a budget across its /56s");
            Check(rooms.Admit(operation, IPAddress.Parse("2001:db8:102::1"), out _), "unrelated IPv6 /48 still admitted");
        }
    }

    private static void IndependentOperationLimits()
    {
        var rooms = new RoomDirectory(new ManualTime());
        var source = 0;
        foreach (var (op, limit) in new[] { (Operation.Create, 48), (Operation.Close, 96), (Operation.Join, 120),
            (Operation.Proof, 384), (Operation.Offer, 1920), (Operation.Answer, 96), (Operation.Receive, 3200), (Operation.Relay, 192) })
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

    private static async Task PollBudgets(string invite, bool host)
    {
        var clock = new ManualTime();
        var rooms = new RoomDirectory(clock, pollLimit: 1);
        var source = IPAddress.Loopback;
        var proof = rooms.IssueProof(source, clock.Now)!;
        var ice = new VerifiedIce(Description(new IceEndpoint(proof.Address, 3012)), [proof.Token]);
        var operation = host ? Operation.Offer : Operation.Receive;
        var (sourceLimit, globalLimit) = host ? (160, 1920) : (320, 3200);
        // Seed the windows in memory. Exercise the real HTTP admission path with six admissions left.
        for (int i = 0; i < sourceLimit - 6; i++)
            Check(rooms.Admit(operation, source, out _), "seed source poll budget");
        for (int i = 0; i < globalLimit - sourceLimit; i++)
            Check(rooms.Admit(operation, IPAddress.Parse($"2001:db8:{i:x}::1"), out _), "seed global poll budget");

        (Registration Room, byte[] Token) Create()
        {
            var room = rooms.Create(invite, clock.Now)!;
            var key = host ? room.OwnerToken : rooms.Join(room.Number, ice, source)!.GuestToken;
            return (room, Convert.FromHexString(key));
        }
        void Publish(Registration room) => Check(host ? rooms.Join(room.Number, ice, source) != null :
            rooms.Answer(room.Number, Convert.FromHexString(room.OwnerToken), ice, source), "publish pending candidates");
        var first = Create();
        var second = Create();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task<HttpStatusCode> Poll((Registration Room, byte[] Token) peer, bool malformed = false)
        {
            var fields = new Dictionary<string, string>
            {
                ["number"] = peer.Room.Number,
                [host ? "ownerToken" : "guestToken"] = Convert.ToHexString(peer.Token)
            };
            using var body = new MemoryStream(malformed ? "{"u8.ToArray() : JsonSerializer.SerializeToUtf8Bytes(fields));
            var context = new DefaultHttpContext { RequestAborted = timeout.Token };
            context.Connection.RemoteIpAddress = source;
            context.Request.Scheme = "https";
            context.Request.Path = host ? "/v3/rooms/offer" : "/v3/rooms/receive";
            context.Request.Method = "POST";
            context.Request.ContentType = "application/json";
            context.Request.Body = body;
            await Program.Dispatch(context, rooms);
            return (HttpStatusCode)context.Response.StatusCode;
        }

        using var abort = new CancellationTokenSource();
        var interrupted = rooms.Wait(first.Room.Number, first.Token, host, source, abort.Token);
        abort.Cancel();
        try { await interrupted; throw new InvalidOperationException("cancelled waiter remained active"); }
        catch (OperationCanceledException) { }
        var firstWait = Poll(first);
        Check(!firstWait.IsCompleted, "first room reuses the only slot after disconnecting");
        for (int retry = 0; retry < 3; retry++)
        {
            Check(await Poll(first) == HttpStatusCode.NoContent, "duplicate poll retries without spending a slot");
            Check(await Poll(second) == HttpStatusCode.ServiceUnavailable, "different room retries while the slot is full");
        }
        Check(await Poll((first.Room, second.Token)) == HttpStatusCode.NotFound, "reject wrong credentials while full");
        Check(await Poll(first, malformed: true) == HttpStatusCode.BadRequest, "reject malformed poll while full");
        Publish(first.Room);
        Check(await firstWait == HttpStatusCode.OK, "first accepted poll receives candidates");
        var secondWait = Poll(second);
        Check(!secondWait.IsCompleted, "other room can now take the freed slot");
        Check(await Poll(first) == HttpStatusCode.OK, "available candidates need no wait slot");
        Publish(second.Room);
        Check(await secondWait == HttpStatusCode.OK, "second room receives candidates after busy retries");
        Check(await Poll(second) == HttpStatusCode.TooManyRequests, "three waits, two invalid requests and a replay consume six admissions");
        Check(await Poll(second, malformed: true) == HttpStatusCode.TooManyRequests, "exhausted poll budget also rejects malformed attempts");
        Check(!rooms.Admit(operation, IPAddress.IPv6Loopback, out _), "same six admissions also fill the global budget");
    }

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
        using var abort = new CancellationTokenSource();
        var disconnected = rooms.Wait(room.Number, owner, true, v4, abort.Token);
        Check(!disconnected.IsCompleted && (await rooms.Wait(room.Number, owner, true, v4, default)).Status == HttpStatusCode.NoContent,
            "overlapping owner poll does not occupy another waiter");
        abort.Cancel();
        try { await disconnected; throw new InvalidOperationException("cancelled poll remained active"); }
        catch (OperationCanceledException) { }
        var wait = rooms.Wait(room.Number, owner, true, v4, default);
        Check(rooms.Join(room.Number, offer with { Proofs = [Proof(v4)] }, v4) == null,
            "cannot publish a second family without its proof");
        var claim = rooms.Join(room.Number, offer, v4)!;
        var guest = Convert.FromHexString(claim.GuestToken);
        Check((await wait).Description == offer.Description && rooms.Join(room.Number, offer, v4) == null &&
              (await rooms.Wait(room.Number, owner, true, v4, default)).Description == offer.Description,
            "after a lost HTTP response only the owner can fetch the same offer again");
        Check((await rooms.Wait(room.Number, guest, true, v4, default)).Status == HttpStatusCode.NotFound &&
              (await rooms.Wait(room.Number, owner, false, v4, default)).Status == HttpStatusCode.NotFound &&
              !rooms.Answer(room.Number, guest, answer, v4), "capability cannot switch roles");
        var receive = rooms.Wait(room.Number, guest, false, v4, default);
        Check(!receive.IsCompleted && rooms.Answer(room.Number, owner, answer, v4) &&
            (await receive).Description == answer.Description && rooms.Answer(room.Number, owner, answer, v4) &&
            !rooms.Answer(room.Number, owner, offer, v4) && (await rooms.Wait(room.Number, owner, true, v4, default)).Status == HttpStatusCode.NotFound &&
            (await rooms.Wait(room.Number, guest, false, v4, default)).Description == answer.Description,
            "publishing acknowledges offer; identical publish and guest reads are replayable, replacement rejected");
        clock.Now += RoomDirectory.AnswerRetention.Ticks;
        Check((await rooms.Wait(room.Number, guest, false, v4, default)).Status == HttpStatusCode.NotFound && rooms.Join(room.Number, offer, v4) == null,
            "reading answer repeatedly does not extend retention or reopen the number");
        var closing = rooms.Create(invite, clock.Now)!;
        var owner2 = Convert.FromHexString(closing.OwnerToken);
        var pending = rooms.Wait(closing.Number, owner2, true, v4, default);
        Check(!rooms.Close(closing.Number, guest) && !pending.IsCompleted &&
            rooms.Close(closing.Number, owner2) && (await pending).Status == HttpStatusCode.NotFound, "close only wakes the owner's room");
        var expiring = rooms.Create(invite, clock.Now)!;
        var expired = rooms.Wait(expiring.Number, Convert.FromHexString(expiring.OwnerToken), true, v4, default);
        clock.Now += RoomDirectory.Lifetime.Ticks;
        rooms.RemoveExpired();
        var fresh = rooms.Create(invite, clock.Now)!;
        Check((await expired).Status == HttpStatusCode.NotFound && rooms.Join(fresh.Number, offer, v4) == null, "expiry drops pending waits and proofs");
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
        await using var app = Program.Build(certificate, new(), IPAddress.Loopback, 0, TimeProvider.System, pollLimit: 1);
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
            using var phase2 = await client.PostAsJsonAsync("/v2/rooms/join", new { number = "000000" });
            Check(phase2.StatusCode == HttpStatusCode.NotFound, "old API rejected");
            using var proofResponse = await client.PostAsJsonAsync("/v3/rooms/proof", new { });
            var proof = (await proofResponse.Content.ReadFromJsonAsync<AddressProof>())!;
            Check(proof.Address == IPAddress.Loopback.ToString(), "proof uses socket source, ignores forwarded IP");
            var ice = new VerifiedIce(Description(new IceEndpoint(proof.Address, 3012)), [proof.Token]);
            async Task<Registration> Create()
            {
                using var response = await client.PostAsJsonAsync("/v3/rooms/create", new { invite });
                Check(response.StatusCode == HttpStatusCode.OK && response.Headers.CacheControl?.NoStore == true, "create over TLS");
                return (await response.Content.ReadFromJsonAsync<Registration>())!;
            }
            async Task Close(Registration registration)
            {
                using var response = await client.PostAsJsonAsync("/v3/rooms/close", registration);
                Check(response.StatusCode == HttpStatusCode.NoContent, "idempotent close");
            }
            async Task<Claim?> Join(string number)
            {
                using var response = await client.PostAsJsonAsync("/v3/rooms/join", new { number, offer = ice });
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                Check(response.StatusCode == HttpStatusCode.OK, "join response");
                return await response.Content.ReadFromJsonAsync<Claim>();
            }
            var first = await Create();
            var second = await Create();
            await Close(first with { OwnerToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) });
            var poll1 = client.PostAsJsonAsync("/v3/rooms/offer", first);
            var poll2 = client.PostAsJsonAsync("/v3/rooms/offer", first);
            var duplicate = await Task.WhenAny(poll1, poll2);
            using var overlap = await duplicate;
            Check(overlap.StatusCode == HttpStatusCode.NoContent && overlap.Headers.ConnectionClose == true,
                "same owner already has a pending poll");
            using var busy = await client.PostAsJsonAsync("/v3/rooms/offer", second);
            Check(busy.StatusCode == HttpStatusCode.ServiceUnavailable && busy.Headers.ConnectionClose == true,
                "full poll budget refuses extra poll without keeping the HTTP socket");
            using var whileFull = await client.PostAsJsonAsync("/v3/rooms/proof", new { });
            Check(whileFull.StatusCode == HttpStatusCode.OK, "proof still served with all poll slots busy");
            var matches = await Task.WhenAll(Join(first.Number), Join(first.Number));
            var claim = matches.OfType<Claim>().Single();
            Check(claim.Invite == invite && RoomDirectory.ValidToken(claim.GuestToken), "one guest, original invite");
            using var unread = await (duplicate == poll1 ? poll2 : poll1);
            Check(unread.StatusCode == HttpStatusCode.OK, "join wakes original owner poll");
            using var offer = await client.PostAsJsonAsync("/v3/rooms/offer", first);
            Check(offer.StatusCode == HttpStatusCode.OK && offer.Headers.ConnectionClose == true, "owner obtains offer again");
            using var waiting = await client.PostAsJsonAsync("/v3/rooms/receive", new { first.Number, claim.GuestToken });
            Check(waiting.StatusCode == HttpStatusCode.NoContent && waiting.Headers.ConnectionClose == true,
                "empty answer poll finishes within client timeout and releases connection");
            using var answered = await client.PostAsJsonAsync("/v3/rooms/answer", new { first.Number, first.OwnerToken, ice });
            Check(answered.StatusCode == HttpStatusCode.NoContent, "owner submits answer");
            using var receive = await client.PostAsJsonAsync("/v3/rooms/receive", new { first.Number, claim.GuestToken });
            var received = await receive.Content.ReadFromJsonAsync<IceDescription>();
            Check(received?.Candidates.Single() == ice.Description.Candidates.Single(), "claimed guest obtains answer");
            using var replay = await client.PostAsJsonAsync("/v3/rooms/receive", new { first.Number, claim.GuestToken });
            Check((await replay.Content.ReadAsStringAsync()) == (await receive.Content.ReadAsStringAsync()), "repeat guest read is identical");
            await Close(second);
            await Close(second);
            Check(await Join(second.Number) == null, "owner can revoke");
            using var old = await client.PostAsJsonAsync("/v3/rooms/create", new { invite = "T12L6." + invite[6..] });
            Check(old.StatusCode == HttpStatusCode.BadRequest, "old invite");
            using var oversizedBody = new StringContent(new string(' ', 4097), Encoding.UTF8, "application/json");
            using var oversized = await client.PostAsync("/v3/rooms/join", oversizedBody);
            Check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "oversized body");
            using var overrideAddress = await client.PostAsJsonAsync("/v3/rooms/create", new { invite, address = "192.0.2.44" });
            Check(overrideAddress.StatusCode == HttpStatusCode.BadRequest, "unexpected address field");
            using var limited = await client.PostAsJsonAsync("/v3/rooms/create", new { invite });
            Check(limited.StatusCode == HttpStatusCode.TooManyRequests, "exhausted create budget returns 429");
        }
        finally { await app.StopAsync(); }
    }
}
