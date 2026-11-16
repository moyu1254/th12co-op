using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TH12Coop;

internal sealed class RendezvousClient : IDisposable
{
    private const string Variable = "TH12COOP_RENDEZVOUS_URL";
    private readonly HttpClient http, ipv4, ipv6;
    internal Uri Origin => http.BaseAddress!;
    internal bool AllowLoopback => Origin.IsLoopback;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 6, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal sealed record Registration(string Number, string OwnerToken);
    internal sealed record Claim(string Invite, string GuestToken);

    public static RendezvousClient Configured() => new(Environment.GetEnvironmentVariable(Variable));

    internal RendezvousClient(string? origin, HttpMessageHandler? testHandler = null)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            url.UserInfo.Length != 0 || url.AbsolutePath != "/" || url.Query.Length != 0 || url.Fragment.Length != 0 || url.Port == 0)
            throw new IOException($"合流サーバーの HTTPS アドレスを {Variable} に設定してください。");
        HttpClient Client(HttpMessageHandler handler, bool dispose = true) => new(handler, dispose)
        {
            BaseAddress = url, Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 4096
        };
        http = Client(testHandler ?? Handler(null));
        ipv4 = Client(testHandler ?? Handler(AddressFamily.InterNetwork), testHandler == null);
        ipv6 = Client(testHandler ?? Handler(AddressFamily.InterNetworkV6), testHandler == null);
    }

    private static SocketsHttpHandler Handler(AddressFamily? family)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false, MaxResponseHeadersLength = 8,
            SslOptions = new() { EnabledSslProtocols = SslProtocols.Tls13 }
        };
        if (family is { } af)
        {
            // Resolve only the configured HTTPS origin, then use its TLS name as usual.
            handler.ConnectCallback = async (context, ct) =>
            {
                var target = context.DnsEndPoint;
                var addresses = await Dns.GetHostAddressesAsync(target.Host, af, ct);
                var socket = new Socket(af, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                    await socket.ConnectAsync(addresses, target.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            };
        }
        return handler;
    }

    internal static bool IsNumber(string value) => value.Length == 6 && value.All(char.IsAsciiDigit);
    private static bool IsToken(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigitUpper);

    public async Task<Registration> Create(string invite, CancellationToken token)
    {
        using var response = await Post("create", new { invite }, HttpStatusCode.OK, token);
        var room = await Read<Registration>(response, token);
        if (!IsNumber(room.Number) || !IsToken(room.OwnerToken)) throw InvalidResponse();
        return room;
    }

    public async Task<Claim> Join(string number, IceDescription offer, CancellationToken token)
    {
        if (!IsNumber(number)) throw new IOException("部屋番号は半角数字6桁で入力してください。");
        var verified = await VerifyLocal(offer, token);
        using var response = await Post("join", new { number, offer = verified }, HttpStatusCode.OK, token);
        var claim = await Read<Claim>(response, token);
        if (!IsToken(claim.GuestToken)) throw InvalidResponse();
        try { CryptographicOperations.ZeroMemory(RoomIdentity.Parse(claim.Invite).Capability); }
        catch (ArgumentException)
        {
            await CancelSetup(number, claim.GuestToken, host: false);
            throw InvalidResponse();
        }
        return claim;
    }

    internal Task<IceDescription> WaitOffer(Registration room, CancellationToken token) =>
        Receive("offer", room, token);

    internal Task<IceDescription> WaitAnswer(string number, string guestToken, CancellationToken token) =>
        Receive("receive", new { number, guestToken }, token);

    private async Task<IceDescription> Receive<T>(string action, T credential, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(action == "offer" ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(90));
        while (true)
        {
            using var response = await Post(action, credential, HttpStatusCode.OK, lifetime.Token);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var ice = await Read<IceDescription>(response, lifetime.Token);
                if (!ice.IsValidSignal(AllowLoopback)) throw InvalidResponse();
                return ice;
            }
            // Empty poll, or all wait slots occupied. Keep the original room and credentials.
            await Task.Delay(response.StatusCode == HttpStatusCode.ServiceUnavailable ? 1000 : 250, lifetime.Token);
        }
    }

    internal async Task<IceDescription> Answer(Registration room, IceDescription answer, CancellationToken token)
    {
        var ice = await VerifyLocal(answer, token);
        using var response = await Post("answer", new { room.Number, room.OwnerToken, ice }, HttpStatusCode.NoContent, token);
        return ice.Description;
    }

    internal async Task<bool> AgreeDirect(string number, string credential, bool host, bool direct, CancellationToken token)
    {
        var proposal = new { number, token = credential, host, direct };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            while (true)
            {
                using var response = await Post("route", proposal, HttpStatusCode.OK, deadline.Token);
                switch (await Read<string>(response, deadline.Token))
                {
                    case "direct" when direct: return true;
                    case "relay": return false;
                    case "pending" when direct: await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token); break;
                    default: throw InvalidResponse();
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("接続経路が35秒以内に確定しませんでした。"); }
    }

    private async Task<VerifiedIce> VerifyLocal(IceDescription ice, CancellationToken token)
    {
        if (!ice.IsValidSignal(AllowLoopback)) throw new IOException("有効な UDP 接続候補を取得できませんでした。");
        async Task<AddressProof?> Proof(AddressFamily af)
        {
            try
            {
                using var response = await Post("proof", new { }, HttpStatusCode.OK, token,
                    client: af == AddressFamily.InterNetworkV6 ? ipv6 : ipv4);
                var proof = await Read<AddressProof>(response, token);
                if (IsToken(proof.Token) && ice.Candidates.Any(c => c.Address == proof.Address &&
                    IPAddress.Parse(c.Address).AddressFamily == af)) return proof;
                return null;
            }
            catch (IOException) { return null; } // The other family may be usable.
        }
        var families = ice.Candidates.Select(c => IPAddress.Parse(c.Address).AddressFamily).Distinct();
        var observed = (await Task.WhenAll(families.Select(Proof))).OfType<AddressProof>().ToArray();
        var allowed = ice.Candidates.Where(c => observed.Any(p => p.Address == c.Address)).ToArray();
        token.ThrowIfCancellationRequested();
        if (allowed.Length == 0) return new(IceDescription.NoDirect, []);
        return new(ice with { Candidates = allowed }, observed.Select(p => p.Token).ToArray());
    }

    internal async Task CancelSetup(string number, string credential, bool host, Action<string>? status = null)
    {
        // The caller's token may already be cancelled. Give the peer a bounded
        // notification attempt without replacing the original setup failure.
        object request = host ? new { number, ownerToken = credential } : new { number, guestToken = credential };
        try { using var response = await Post("close", request, HttpStatusCode.NoContent, CancellationToken.None); }
        catch (IOException) { status?.Invoke("部屋を取り消せませんでした。部屋は期限が来ると自動で閉じます。"); }
    }

    internal Task<SecureChannel> AcceptRelay(Registration room, RoomIdentity identity, CancellationToken token) =>
        OpenRelay(room.Number, room.OwnerToken, true, (stream, ct) => SecureChannel.AcceptTls(stream, identity, true, ct), token);

    internal async Task<SecureChannel> ConnectRelay(string number, Claim claim, CancellationToken token)
    {
        var (pin, capability) = RoomIdentity.Parse(claim.Invite);
        try
        {
            return await OpenRelay(number, claim.GuestToken, false,
                (stream, ct) => SecureChannel.ConnectTls(stream, pin, capability, true, ct), token);
        }
        finally { CryptographicOperations.ZeroMemory(capability); }
    }

    private async Task<SecureChannel> OpenRelay(string number, string credential, bool host,
        Func<Stream, CancellationToken, Task<SecureChannel>> authenticate, CancellationToken token)
    {
        if (!IsNumber(number) || !IsToken(credential)) throw InvalidResponse();
        var url = new UriBuilder(Origin) { Scheme = "wss", Path = host ? "/v3/relay/host" : "/v3/relay/guest" }.Uri;
        var socket = new ClientWebSocket();
        RelayStream? transport = null;
        try
        {
            socket.Options.SetRequestHeader("Authorization", "Bearer " + credential);
            socket.Options.AddSubProtocol(RelayMessages.HeartbeatProtocol);
            socket.Options.SetRequestHeader("X-TH12-Room", number);
            // The same HTTPS handler verifies the outer certificate and disallows redirects.
            // The relay sends the upgrade response after admitting both room members.
            using var pairing = CancellationTokenSource.CreateLinkedTokenSource(token);
            pairing.CancelAfter(TimeSpan.FromSeconds(40));
            await socket.ConnectAsync(url, http, pairing.Token);
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(TimeSpan.FromSeconds(10));
            transport = new RelayStream(socket);
            return await authenticate(transport, handshake.Token);
        }
        catch (Exception error)
        {
            if (transport == null) socket.Dispose();
            else transport.Dispose();
            if (error is OperationCanceledException && !token.IsCancellationRequested)
                throw new TimeoutException("中継への接続または相手の認証が期限内に完了しませんでした。", error);
            if (error is WebSocketException)
                throw new IOException("合流サーバーの中継に接続できませんでした。", error);
            throw;
        }
    }

    private async Task<HttpResponseMessage> Post<T>(string action, T value, HttpStatusCode expected, CancellationToken token,
        HttpClient? client = null)
    {
        bool poll = action is "offer" or "receive";
        int attempts = poll ? 4 : action is "answer" or "route" or "close" ? 2 : 1;
        int seconds = action switch { "offer" => 25, "receive" or "answer" or "route" => 4, "close" => 2, _ => 8 };
        HttpResponseMessage response;
        for (int attempt = 1; ; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                response = await (client ?? http).PostAsJsonAsync($"v3/rooms/{action}", value, Json, deadline.Token);
                break;
            }
            catch (Exception e) when (!token.IsCancellationRequested && e is (OperationCanceledException or HttpRequestException))
            {
                bool interrupted = e is OperationCanceledException or HttpRequestException
                    { HttpRequestError: HttpRequestError.ConnectionError or HttpRequestError.ResponseEnded };
                if (!interrupted || attempt == attempts)
                    throw new IOException("合流サーバーに接続できませんでした。HTTPS の接続と設定を確認してください。", e);
                await Task.Delay(250, token);
            }
        }
        if (response.StatusCode == expected || poll && response.StatusCode is (HttpStatusCode.NoContent or HttpStatusCode.ServiceUnavailable))
            return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new IOException(status switch
        {
            HttpStatusCode.NotFound when action == "route" => "接続準備が取り消されたか、部屋の期限が切れました。",
            HttpStatusCode.NotFound => "部屋が見つかりません。使用済みまたは期限切れの場合は新しい番号を作成してください。",
            HttpStatusCode.TooManyRequests => "合流サーバーへの試行回数が上限に達しました。しばらく待ってください。",
            _ => "合流サーバーがリクエストを受け付けませんでした。"
        });
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken token) where T : class
    {
        try { return await response.Content.ReadFromJsonAsync<T>(Json, token) ?? throw InvalidResponse(); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private static IOException InvalidResponse() => new("合流サーバーの応答が正しくありません。");
    public void Dispose() { ipv4.Dispose(); ipv6.Dispose(); http.Dispose(); }
}
