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
    internal const int GamePort = 3012;
    private const string Variable = "TH12COOP_RENDEZVOUS_URL";
    private readonly HttpClient http;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 2, AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal sealed record Registration(string Number, string OwnerToken);
    internal sealed record Candidate(string Invite, string Address, int Port);

    public static RendezvousClient Configured() => new(Environment.GetEnvironmentVariable(Variable));

    internal RendezvousClient(string? origin, HttpMessageHandler? testHandler = null)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps ||
            url.UserInfo.Length != 0 || url.AbsolutePath != "/" || url.Query.Length != 0 || url.Fragment.Length != 0 || url.Port == 0)
            throw new IOException($"合流サーバーの HTTPS アドレスを {Variable} に設定してください。");
        http = new HttpClient(testHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            MaxResponseHeadersLength = 8,
            SslOptions = new() { EnabledSslProtocols = SslProtocols.Tls13 }
        })
        {
            BaseAddress = url, Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 1024
        };
    }

    internal static bool IsNumber(string value) => value.Length == 6 && value.All(char.IsAsciiDigit);

    public async Task<Registration> Create(string invite, CancellationToken token)
    {
        using var response = await Post("create", new { invite, port = GamePort }, HttpStatusCode.OK, token);
        var room = await Read<Registration>(response, token);
        if (!IsNumber(room.Number) || room.OwnerToken.Length != 64 || !room.OwnerToken.All(char.IsAsciiHexDigitUpper))
            throw InvalidResponse();
        return room;
    }

    public async Task<Candidate> Join(string number, CancellationToken token)
    {
        if (!IsNumber(number)) throw new IOException("部屋番号は半角数字6桁で入力してください。");
        using var response = await Post("join", new { number }, HttpStatusCode.OK, token);
        var peer = await Read<Candidate>(response, token);
        if (peer.Port != GamePort || !IPAddress.TryParse(peer.Address, out var ip) ||
            !IsAllowedCandidate(ip, http.BaseAddress!.IsLoopback)) throw InvalidResponse();
        try { CryptographicOperations.ZeroMemory(RoomIdentity.Parse(peer.Invite).Capability); }
        catch (ArgumentException) { throw InvalidResponse(); }
        return peer with { Address = ip.ToString() };
    }

    internal static bool IsAllowedCandidate(IPAddress address, bool loopbackService)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return loopbackService;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.ScopeId == 0 && GlobalV6.Contains(address) && !ExcludedV6.Any(net => net.Contains(address));
        return address.AddressFamily == AddressFamily.InterNetwork && !ExcludedV4.Any(net => net.Contains(address));
    }

    // Limit the server-selected destination to a numeric global unicast address and GamePort.
    // Manual invitations are the separate opt-in path to private LAN/VPN addresses.
    private static readonly IPNetwork GlobalV6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] ExcludedV6 = Networks("2001::/23", "2001:db8::/32", "2002::/16", "3fff::/20");
    private static readonly IPNetwork[] ExcludedV4 = Networks("0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10",
        "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24",
        "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4");
    private static IPNetwork[] Networks(params string[] ranges) => Array.ConvertAll(ranges, IPNetwork.Parse);

    internal async Task Close(Registration room)
    {
        using var response = await Post("close", room, HttpStatusCode.NoContent, CancellationToken.None);
    }

    private async Task<HttpResponseMessage> Post<T>(string action, T value, HttpStatusCode expected, CancellationToken token)
    {
        HttpResponseMessage response;
        try { response = await http.PostAsJsonAsync($"v1/rooms/{action}", value, Json, token); }
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
    public void Dispose() => http.Dispose();
}
