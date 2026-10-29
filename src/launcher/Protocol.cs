using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TH12Coop;

internal sealed class RoomIdentity : IDisposable
{
    internal X509Certificate2 Certificate { get; }
    internal byte[] Capability { get; }
    public string Invite { get; }

    private RoomIdentity(X509Certificate2 certificate, byte[] capability)
    {
        Certificate = certificate;
        Capability = capability;
        var invite = SHA256.HashData(certificate.RawData).Concat(capability).ToArray();
        Invite = "T12L3." + Convert.ToBase64String(invite).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static RoomIdentity Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=TH12 CO-OP", key, HashAlgorithmName.SHA256);
        using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Schannel cannot acquire an EphemeralKeySet credential. UserKeySet
        // without PersistKeySet creates a temporary user-protected key container
        // that certificate disposal removes; no certificate is trusted/installed.
        var pfx = temporary.Export(X509ContentType.Pfx);
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
            return new RoomIdentity(certificate, RandomNumberGenerator.GetBytes(32));
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    public static (byte[] CertificateHash, byte[] Capability) Parse(string code)
    {
        code = code.Trim();
        if (!code.StartsWith("T12L3.", StringComparison.Ordinal)) throw new ArgumentException("このバージョンのルームコードを入力してください。");
        var encoded = code[6..].Replace('-', '+').Replace('_', '/');
        try
        {
            var data = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            if (data.Length != 64) throw new FormatException();
            return (data[..32], data[32..]);
        }
        catch (FormatException) { throw new ArgumentException("ルームコードが正しくありません。ホストからコード全体をコピーしてください。"); }
    }

    public void Dispose() { CryptographicOperations.ZeroMemory(Capability); Certificate.Dispose(); }
}

// TLS 1.3 owns encryption, directional record keys, replay protection and forward
// secrecy. The invite pins the exact ephemeral certificate and separately grants
// guest access; neither platform certificate trust nor a public discovery server
// can substitute for that invite. Application records have an independent bound.
internal sealed class SecureChannel : IDisposable
{
    internal const int MaximumPayload = 4096;
    internal const ushort ProtocolVersion = 3;
    private readonly TcpClient client;
    private readonly SslStream stream;
    private readonly SemaphoreSlim writer = new(1, 1);
    private int disposed;
    public byte[] Session { get; }

    private SecureChannel(TcpClient client, SslStream stream, byte[] session)
    { this.client = client; this.stream = stream; Session = session; }

    public static async Task<SecureChannel> Connect(string host, int port, string invite, CancellationToken token)
    {
        var parsed = RoomIdentity.Parse(invite);
        var client = new TcpClient { NoDelay = true };
        SslStream? tls = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(host, port, deadline.Token);
            tls = new SslStream(client.GetStream(), false, (_, certificate, _, _) =>
                certificate != null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.GetRawCertData()), parsed.CertificateHash));
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "th12coop.invalid",
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            }, deadline.Token);
            var hello = new byte[40];
            "T12L"u8.CopyTo(hello);
            BinaryPrimitives.WriteUInt16LittleEndian(hello.AsSpan(4), ProtocolVersion);
            hello[6] = 2; // Invited guest.
            parsed.Capability.CopyTo(hello, 8);
            await tls.WriteAsync(hello, deadline.Token);
            var acknowledgement = new byte[24];
            await tls.ReadExactlyAsync(acknowledgement, deadline.Token);
            ValidateHello(acknowledgement, 1);
            return new SecureChannel(client, tls, acknowledgement[8..24]);
        }
        catch (Exception error)
        {
            tls?.Dispose(); client.Dispose();
            if (error is OperationCanceledException && !token.IsCancellationRequested)
                throw new TimeoutException("相手への接続が10秒以内に完了しませんでした。", error);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(parsed.Capability); }
    }

    public static async Task<SecureChannel> Accept(TcpClient client, RoomIdentity identity, CancellationToken token)
    {
        client.NoDelay = true;
        var tls = new SslStream(client.GetStream(), false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity.Certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                EncryptionPolicy = EncryptionPolicy.RequireEncryption,
            }, deadline.Token);
            var hello = new byte[40];
            await tls.ReadExactlyAsync(hello, deadline.Token);
            ValidateHello(hello, 2);
            if (!CryptographicOperations.FixedTimeEquals(hello.AsSpan(8, 32), identity.Capability))
                throw new AuthenticationException("ルームコードが一致しません。");
            var session = RandomNumberGenerator.GetBytes(16);
            var acknowledgement = new byte[24];
            "T12L"u8.CopyTo(acknowledgement);
            BinaryPrimitives.WriteUInt16LittleEndian(acknowledgement.AsSpan(4), ProtocolVersion);
            acknowledgement[6] = 1; // Host.
            session.CopyTo(acknowledgement, 8);
            await tls.WriteAsync(acknowledgement, deadline.Token);
            return new SecureChannel(client, tls, session);
        }
        catch (Exception error)
        {
            tls.Dispose(); client.Dispose();
            if (error is OperationCanceledException && !token.IsCancellationRequested)
                throw new TimeoutException("接続相手の認証が4秒以内に完了しませんでした。", error);
            throw;
        }
    }

    private static void ValidateHello(byte[] hello, byte role)
    {
        if (!hello.AsSpan(0, 4).SequenceEqual("T12L"u8) || BinaryPrimitives.ReadUInt16LittleEndian(hello.AsSpan(4)) != ProtocolVersion ||
            hello[6] != role || hello[7] != 0) throw new AuthenticationException("通信バージョンまたは参加役割が一致しません。");
    }

    public async Task Send(byte[] payload, CancellationToken token)
    {
        if (payload.Length is < 1 or > MaximumPayload) throw new IOException("通信データの長さが不正です。");
        await writer.WaitAsync(token);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            var record = new byte[4 + payload.Length];
            BinaryPrimitives.WriteInt32LittleEndian(record, payload.Length);
            payload.CopyTo(record, 4);
            await stream.WriteAsync(record, deadline.Token);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("相手へのデータ送信が5秒以内に完了しませんでした。", error); }
        finally { writer.Release(); }
    }

    public async Task<byte[]> Receive(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        // Native resource loading is bounded at 30 seconds. Keep transport
        // alive through that work plus delivery/cleanup margin.
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header, deadline.Token);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 1 or > MaximumPayload) throw new IOException("通信データの長さが不正です。");
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, deadline.Token);
            return payload;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("相手からのデータ受信が40秒以内に完了しませんでした。接続を確認してください。", error); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        client.Dispose();
        stream.Dispose();
        // Ongoing writer cancellation owns its release; disposing this gate here
        // would race that release and hide the actual transport failure.
    }
}
