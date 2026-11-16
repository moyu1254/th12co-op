using System.Globalization;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace TH12Coop.Rendezvous;

internal static class Program
{
    private const int ConnectionLimit = 64;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 6,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    private sealed record Empty();
    private sealed record Create(string Invite);
    private sealed record Join(string Number, VerifiedIce Offer);
    private sealed record Owner(string Number, string OwnerToken);
    private sealed record Guest(string Number, string GuestToken);
    private sealed record Answer(string Number, string OwnerToken, VerifiedIce Ice);
    private sealed record Route(string Number, string Token, bool Host, bool Direct);

    private static async Task<int> Main(string[] args)
    {
        if (args is ["--self-test"]) return await ServerTests.Run();
        var pfx = Environment.GetEnvironmentVariable("TH12_RENDEZVOUS_PFX");
        var configuredPort = Environment.GetEnvironmentVariable("TH12_RENDEZVOUS_PORT") ?? "8443";
        if (args.Length != 0 || string.IsNullOrEmpty(pfx) ||
            !int.TryParse(configuredPort, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            Console.Error.WriteLine("Set TH12_RENDEZVOUS_PFX and optionally TH12_RENDEZVOUS_PORT (default 8443).");
            return 2;
        }
        var certs = X509CertificateLoader.LoadPkcs12CollectionFromFile(pfx,
            Environment.GetEnvironmentVariable("TH12_RENDEZVOUS_PFX_PASSWORD"), X509KeyStorageFlags.UserKeySet);
        try
        {
            var leaf = certs.Single(cert => cert.HasPrivateKey);
            var chain = new X509Certificate2Collection(certs.Where(cert => cert != leaf).ToArray());
            await using var app = Build(leaf, chain, null, port, TimeProvider.System);
            await app.RunAsync();
            return 0;
        }
        finally { foreach (var cert in certs) cert.Dispose(); }
    }

    internal static WebApplication Build(X509Certificate2 leaf, X509Certificate2Collection chain,
        IPAddress? listenAddress, int port, TimeProvider clock, int pollLimit = RoomDirectory.MaxWaitingPolls,
        RelayLimits? relayLimits = null)
    {
        if (pollLimit is < 1 or > RoomDirectory.MaxWaitingPolls) throw new ArgumentOutOfRangeException(nameof(pollLimit));
        var limits = relayLimits ?? RelayLimits.Default;
        limits.Validate();
        // Configure only this socket. In particular, environment settings must not enable HTTP,
        // forwarded headers, request logging or the developer exception page.
        var builder = WebApplication.CreateEmptyBuilder(new() { EnvironmentName = Environments.Production });
        builder.Host.UseConsoleLifetime();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = ConnectionLimit;
            kestrel.Limits.MaxConcurrentUpgradedConnections = 2 * limits.Pairs;
            kestrel.Limits.MaxRequestBodySize = 4096;
            kestrel.Limits.MaxRequestLineSize = 1024;
            kestrel.Limits.MaxRequestHeadersTotalSize = 4096;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            kestrel.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
            void Secure(ListenOptions socket)
            {
                socket.Protocols = HttpProtocols.Http1;
                socket.UseHttps(tls =>
                {
                    tls.ServerCertificate = leaf;
                    tls.ServerCertificateChain = chain;
                    tls.SslProtocols = SslProtocols.Tls13;
                    tls.HandshakeTimeout = TimeSpan.FromSeconds(5);
                });
            }
            if (listenAddress == null) kestrel.ListenAnyIP(port, Secure);
            else kestrel.Listen(listenAddress, port, Secure);
        });
        var app = builder.Build();
        var directory = new RoomDirectory(clock, pollLimit, limits);
        var timer = clock.CreateTimer(_ => directory.RemoveExpired(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        app.Lifetime.ApplicationStopped.Register(timer.Dispose);
        app.UseWebSockets();
        app.Run(context => Dispatch(context, directory, app.Lifetime.ApplicationStopping));
        return app;
    }

    internal static async Task Dispatch(HttpContext context, RoomDirectory directory, CancellationToken stopping = default)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!request.IsHttps || request.QueryString.HasValue)
        { response.StatusCode = 404; return; }
        if (request.Path.Value is "/v3/relay/host" or "/v3/relay/guest")
        {
            await OpenRelay(context, directory, request.Path.Value == "/v3/relay/host", stopping);
            return;
        }
        if (!HttpMethods.IsPost(request.Method))
        { response.StatusCode = 404; return; }
        Operation? op = request.Path.Value switch
        {
            "/v3/rooms/create" => Operation.Create,
            "/v3/rooms/join" => Operation.Join,
            "/v3/rooms/close" => Operation.Close,
            "/v3/rooms/proof" => Operation.Proof,
            "/v3/rooms/offer" => Operation.Offer,
            "/v3/rooms/answer" => Operation.Answer,
            "/v3/rooms/receive" => Operation.Receive,
            "/v3/rooms/route" => Operation.Route,
            _ => null
        };
        if (op == null || context.Connection.RemoteIpAddress is not { } source) { response.StatusCode = 404; return; }
        bool poll = op is Operation.Offer or Operation.Receive;
        if (poll) response.Headers.Connection = "close";
        if (!request.HasJsonContentType()) { response.StatusCode = 415; return; }
        long admittedAt = 0;
        bool allowed = poll
            ? directory.HasPollBudget(op == Operation.Offer, source)
            : directory.Admit(op.Value, source, out admittedAt);
        if (!allowed)
        { response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            switch (op)
            {
                case Operation.Create:
                    var create = await JsonSerializer.DeserializeAsync<Create>(request.Body, Json, deadline.Token);
                    if (create == null || !RoomDirectory.ValidInvite(create.Invite)) break;
                    var registration = directory.Create(create.Invite, admittedAt);
                    if (registration == null) { response.StatusCode = StatusCodes.Status408RequestTimeout; return; }
                    await response.WriteAsJsonAsync(registration, Json, deadline.Token);
                    return;
                case Operation.Join:
                    var join = await JsonSerializer.DeserializeAsync<Join>(request.Body, Json, deadline.Token);
                    if (join == null || !RoomDirectory.ValidNumber(join.Number)) break;
                    var candidate = directory.Join(join.Number, join.Offer, source);
                    if (candidate == null) { response.StatusCode = 404; return; }
                    await response.WriteAsJsonAsync(candidate, Json, deadline.Token);
                    return;
                case Operation.Proof:
                    if (await JsonSerializer.DeserializeAsync<Empty>(request.Body, Json, deadline.Token) == null) break;
                    var proof = directory.IssueProof(source, admittedAt);
                    if (proof == null) break;
                    await response.WriteAsJsonAsync(proof, Json, deadline.Token);
                    return;
                case Operation.Offer:
                case Operation.Receive:
                    await PollCandidates(context, directory, op.Value, source, deadline.Token);
                    return;
                case Operation.Answer:
                    var answer = await JsonSerializer.DeserializeAsync<Answer>(request.Body, Json, deadline.Token);
                    if (answer == null || !RoomDirectory.ValidNumber(answer.Number) || !RoomDirectory.ValidToken(answer.OwnerToken)) break;
                    if (!directory.Answer(answer.Number, answer.OwnerToken, answer.Ice, source))
                    { response.StatusCode = 404; return; }
                    response.StatusCode = 204;
                    return;
                case Operation.Route:
                    var route = await JsonSerializer.DeserializeAsync<Route>(request.Body, Json, deadline.Token);
                    if (route == null || !RoomDirectory.ValidNumber(route.Number) || !RoomDirectory.ValidToken(route.Token)) break;
                    var choice = directory.SelectRoute(route.Number, route.Token, route.Host, route.Direct);
                    if (choice == null) { response.StatusCode = 404; return; }
                    await response.WriteAsJsonAsync(choice, Json, deadline.Token);
                    return;
                case Operation.Close:
                    var close = await JsonSerializer.DeserializeAsync<Owner>(request.Body, Json, deadline.Token);
                    if (close == null || !RoomDirectory.ValidNumber(close.Number) || !RoomDirectory.ValidToken(close.OwnerToken)) break;
                    directory.Close(close.Number, close.OwnerToken);
                    response.StatusCode = 204;
                    return;
            }
        }
        catch (JsonException) { /* Invalid body. Never reflect it or the parse error. */ }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error) { response.StatusCode = error.StatusCode; return; }
        catch (OperationCanceledException) { context.Abort(); return; }
        response.StatusCode = 400;
    }

    private static async Task OpenRelay(HttpContext context, RoomDirectory directory, bool host, CancellationToken stopping)
    {
        var request = context.Request;
        if (!HttpMethods.IsGet(request.Method) || !context.WebSockets.IsWebSocketRequest ||
            context.Connection.RemoteIpAddress is not { } source)
        { context.Response.StatusCode = 404; return; }
        if (!directory.Admit(Operation.Relay, source, out _))
        { context.Response.StatusCode = 429; return; }

        var number = request.Headers["X-TH12-Room"].ToString();
        var authorization = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        var credential = authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase) ? authorization[bearer.Length..] : null;
        if (!RoomDirectory.ValidNumber(number) || !RoomDirectory.ValidToken(credential))
        { context.Response.StatusCode = 404; return; }
        var (status, pair) = directory.ReserveRelay(number, credential, host, source);
        if (pair == null) { context.Response.StatusCode = (int)status; return; }
        await pair.Serve(context, host, stopping);
    }

    private static async Task PollCandidates(HttpContext context, RoomDirectory directory, Operation op,
        IPAddress source, CancellationToken readDeadline)
    {
        string? number = null, key = null;
        int invalidStatus = StatusCodes.Status400BadRequest;
        bool host = op == Operation.Offer;
        try
        {
            if (host)
            {
                var owner = await JsonSerializer.DeserializeAsync<Owner>(context.Request.Body, Json, readDeadline);
                if (owner != null) (number, key) = (owner.Number, owner.OwnerToken);
            }
            else
            {
                var guest = await JsonSerializer.DeserializeAsync<Guest>(context.Request.Body, Json, readDeadline);
                if (guest != null) (number, key) = (guest.Number, guest.GuestToken);
            }
        }
        catch (JsonException) { }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error) { invalidStatus = error.StatusCode; }
        catch (Exception error) when (error is OperationCanceledException or IOException)
        {
            directory.Admit(op, source, out _);
            throw;
        }
        if (!RoomDirectory.ValidNumber(number) || !RoomDirectory.ValidToken(key))
        {
            context.Response.StatusCode = directory.Admit(op, source, out _) ? invalidStatus : StatusCodes.Status429TooManyRequests;
            return;
        }

        var result = await directory.Wait(number, key, host, source, context.RequestAborted);
        context.Response.StatusCode = (int)result.Status;
        if (result.Description == null) return;
        using var write = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        write.CancelAfter(TimeSpan.FromSeconds(5));
        await context.Response.WriteAsJsonAsync(result.Description, Json, write.Token);
    }
}
