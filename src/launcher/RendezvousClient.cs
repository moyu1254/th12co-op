using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
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
        catch (ArgumentException) { throw InvalidResponse(); }
        return claim;
    }

    internal Task<IceDescription> WaitOffer(Registration room, CancellationToken token) =>
        Receive("offer", room, token);

    internal Task<IceDescription> WaitAnswer(string number, string guestToken, CancellationToken token) =>
        Receive("receive", new { number, guestToken }, token);

    private async Task<IceDescription> Receive<T>(string action, T credential, CancellationToken token)
    {
        using var response = await Post(action, credential, HttpStatusCode.OK, token, wait: true);
        var ice = await Read<IceDescription>(response, token);
        if (!ice.IsValid(AllowLoopback)) throw InvalidResponse();
        return ice;
    }

    internal async Task Answer(Registration room, IceDescription answer, CancellationToken token)
    {
        var ice = await VerifyLocal(answer, token);
        using var response = await Post("answer", new { room.Number, room.OwnerToken, ice }, HttpStatusCode.NoContent, token);
    }

    private async Task<VerifiedIce> VerifyLocal(IceDescription ice, CancellationToken token)
    {
        if (!ice.IsValid(AllowLoopback)) throw new IOException("有効な UDP 接続候補を取得できませんでした。");
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
        if (allowed.Length == 0)
            throw new IOException("UDP 接続候補の IP を合流サーバーで確認できませんでした。STUN と合流サーバーへの経路を確認してください。");
        return new(ice with { Candidates = allowed }, observed.Select(p => p.Token).ToArray());
    }

    internal async Task Close(Registration room)
    {
        using var response = await Post("close", room, HttpStatusCode.NoContent, CancellationToken.None);
    }

    private async Task<HttpResponseMessage> Post<T>(string action, T value, HttpStatusCode expected, CancellationToken token,
        bool wait = false, HttpClient? client = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(wait ? TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(8));
        HttpResponseMessage response;
        try { response = await (client ?? http).PostAsJsonAsync($"v2/rooms/{action}", value, Json, deadline.Token); }
        catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException && !token.IsCancellationRequested)
        { throw new IOException("合流サーバーに接続できませんでした。HTTPS の接続と設定を確認してください。"); }
        if (response.StatusCode == expected) return response;
        var status = response.StatusCode;
        response.Dispose();
        throw new IOException(status switch
        {
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
