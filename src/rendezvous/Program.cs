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
        IPAddress? listenAddress, int port, TimeProvider clock)
    {
        // Configure only this socket. In particular, environment settings must not enable HTTP,
        // forwarded headers, request logging or the developer exception page.
        var builder = WebApplication.CreateEmptyBuilder(new() { EnvironmentName = Environments.Production });
        builder.Host.UseConsoleLifetime();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = 64;
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
        var directory = new RoomDirectory(clock);
        var timer = clock.CreateTimer(_ => directory.RemoveExpired(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        app.Lifetime.ApplicationStopped.Register(timer.Dispose);
        app.Run(context => Dispatch(context, directory));
        return app;
    }

    private static async Task Dispatch(HttpContext context, RoomDirectory directory)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers.CacheControl = "no-store";
        if (!request.IsHttps || request.QueryString.HasValue || !HttpMethods.IsPost(request.Method))
        { response.StatusCode = 404; return; }
        Operation? op = request.Path.Value switch
        {
            "/v2/rooms/create" => Operation.Create,
            "/v2/rooms/join" => Operation.Join,
            "/v2/rooms/close" => Operation.Close,
            "/v2/rooms/proof" => Operation.Proof,
            "/v2/rooms/offer" => Operation.Offer,
            "/v2/rooms/answer" => Operation.Answer,
            "/v2/rooms/receive" => Operation.Receive,
            _ => null
        };
        if (op == null || context.Connection.RemoteIpAddress is not { } source) { response.StatusCode = 404; return; }
        if (!request.HasJsonContentType()) { response.StatusCode = 415; return; }
        if (!directory.Admit(op.Value, source, out var admittedAt))
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
                    string number, key;
                    var host = op == Operation.Offer;
                    if (host)
                    {
                        var owner = await JsonSerializer.DeserializeAsync<Owner>(request.Body, Json, deadline.Token);
                        if (owner == null) break;
                        (number, key) = (owner.Number, owner.OwnerToken);
                    }
                    else
                    {
                        var guest = await JsonSerializer.DeserializeAsync<Guest>(request.Body, Json, deadline.Token);
                        if (guest == null) break;
                        (number, key) = (guest.Number, guest.GuestToken);
                    }
                    if (!RoomDirectory.ValidNumber(number) || !RoomDirectory.ValidToken(key)) break;
                    // A single authenticated waiter per role. Expiry/close completes the task.
                    var description = await directory.Wait(number, Convert.FromHexString(key), host, context.RequestAborted);
                    if (description == null) { response.StatusCode = 404; return; }
                    using (var write = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
                    {
                        write.CancelAfter(TimeSpan.FromSeconds(5));
                        await response.WriteAsJsonAsync(description, Json, write.Token);
                    }
                    return;
                case Operation.Answer:
                    var answer = await JsonSerializer.DeserializeAsync<Answer>(request.Body, Json, deadline.Token);
                    if (answer == null || !RoomDirectory.ValidNumber(answer.Number) || !RoomDirectory.ValidToken(answer.OwnerToken)) break;
                    if (!directory.Answer(answer.Number, Convert.FromHexString(answer.OwnerToken), answer.Ice, source))
                    { response.StatusCode = 404; return; }
                    response.StatusCode = 204;
                    return;
                case Operation.Close:
                    var close = await JsonSerializer.DeserializeAsync<Owner>(request.Body, Json, deadline.Token);
                    if (close == null || !RoomDirectory.ValidNumber(close.Number) || !RoomDirectory.ValidToken(close.OwnerToken)) break;
                    directory.Close(close.Number, Convert.FromHexString(close.OwnerToken));
                    response.StatusCode = 204;
                    return;
            }
        }
        catch (JsonException) { /* Invalid body. Never reflect it or the parse error. */ }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException error) { response.StatusCode = error.StatusCode; return; }
        catch (OperationCanceledException) { context.Abort(); return; }
        response.StatusCode = 400;
    }
}
