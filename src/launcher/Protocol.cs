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
        try { Invite = "T12L7." + Convert.ToBase64String(invite).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
        finally { CryptographicOperations.ZeroMemory(invite); }
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
        if (code.Length != 92 || !code.StartsWith("T12L7.", StringComparison.Ordinal) ||
            code.AsSpan(6).ContainsAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") ||
            !"AQgw".Contains(code[^1])) throw new ArgumentException("このバージョンのルームコードを入力してください。");
        var encoded = code[6..].Replace('-', '+').Replace('_', '/');
        try
        {
            var data = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            try
            {
                if (data.Length != 64) throw new FormatException();
                return (data[..32], data[32..]);
            }
            finally { CryptographicOperations.ZeroMemory(data); }
        }
        catch (FormatException) { throw new ArgumentException("ルームコードが正しくありません。ホストからコード全体をコピーしてください。"); }
    }

    public void Dispose() { CryptographicOperations.ZeroMemory(Capability); Certificate.Dispose(); }
}

// TLS 1.3 owns encryption, directional record keys, replay protection and forward
// secrecy. The invite pins the exact ephemeral certificate and separately grants
// guest access. Numbered rooms also compare the connected peers out of band.
internal sealed class SecureChannel : IDisposable
{
    internal const int MaximumPayload = 4096;
    internal const ushort ProtocolVersion = 7;
    private readonly Stream stream;
    private readonly Action release;
    private readonly Func<IOException?>? pathFailure;
    private readonly SemaphoreSlim writer = new(1, 1);
    private int disposed;
    public byte[] Session { get; }
    internal string? VerificationCode { get; }

    private SecureChannel(Stream stream, byte[] session, string? verificationCode, Action release, Func<IOException?>? pathFailure)
    { this.stream = stream; Session = session; VerificationCode = verificationCode; this.release = release; this.pathFailure = pathFailure; }

    public static async Task<SecureChannel> Connect(string host, int port, string invite, CancellationToken token, bool numbered = false)
    {
        var parsed = RoomIdentity.Parse(invite);
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(host, port, deadline.Token);
            return await ConnectTls(client.GetStream(), parsed.CertificateHash, parsed.Capability, numbered, deadline.Token);
        }
        catch (Exception error)
        {
            client.Dispose();
            if (error is OperationCanceledException && !token.IsCancellationRequested)
                throw new TimeoutException("相手への接続が10秒以内に完了しませんでした。", error);
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(parsed.Capability); }
    }

    public static async Task<SecureChannel> Accept(TcpClient client, RoomIdentity identity, CancellationToken token, bool numbered = false)
    {
        try
        {
            client.NoDelay = true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(4));
            return await AcceptTls(client.GetStream(), identity, numbered, deadline.Token);
        }
        catch (Exception error)
        {
            client.Dispose();
            if (error is OperationCanceledException && !token.IsCancellationRequested)
                throw new TimeoutException("接続相手の認証が4秒以内に完了しませんでした。", error);
            throw;
        }
    }

    // Both a TcpClient NetworkStream (which owns its socket) and a relay stream
    // are owned by the SslStream for the lifetime of the authenticated channel.
    internal static async Task<SecureChannel> ConnectTls(Stream transport, byte[] pin, byte[] capability,
        bool numbered, CancellationToken token)
    {
        var tls = new SslStream(transport, false, (_, cert, _, _) => cert != null &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.GetRawCertData()), pin));
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "th12coop.invalid", EnabledSslProtocols = SslProtocols.Tls13, AllowTlsResume = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck, EncryptionPolicy = EncryptionPolicy.RequireEncryption
            }, token);
            return await AuthenticateGuest(tls, pin, capability, numbered, token, FinishTls(tls, transport));
        }
        catch { tls.Dispose(); throw; }
    }

    internal static async Task<SecureChannel> AcceptTls(Stream transport, RoomIdentity identity, bool numbered, CancellationToken token)
    {
        var tls = new SslStream(transport, false);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity.Certificate, ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls13, AllowTlsResume = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck, EncryptionPolicy = EncryptionPolicy.RequireEncryption
            }, token);
            return await AuthenticateHost(tls, identity, numbered, token, FinishTls(tls, transport));
        }
        catch { tls.Dispose(); throw; }
    }

    private static Action FinishTls(SslStream tls, Stream transport) =>
        transport is RelayStream relay ? () => relay.FinishTls(tls) : tls.Dispose;

    // The transport supplies an ordered encrypted stream. The guest has already pinned
    // the host certificate. The caller owns the stream until authentication succeeds.
    internal static async Task<SecureChannel> AuthenticateGuest(Stream stream, byte[] pin, byte[] capability, bool numbered,
        CancellationToken token, Action release, Func<IOException?>? pathFailure = null)
    {
        var hello = new byte[40];
        "T12L"u8.CopyTo(hello);
        BinaryPrimitives.WriteUInt16LittleEndian(hello.AsSpan(4), ProtocolVersion);
        hello[6] = 2; // Invited guest.
        hello[7] = numbered ? (byte)1 : (byte)0;
        capability.CopyTo(hello, 8);
        try { await stream.WriteAsync(hello, token); }
        finally { CryptographicOperations.ZeroMemory(hello); }
        // Commit the guest's random contribution and authenticated TLS pin before the
        // host chooses its session challenge. A substituted pin cannot forward this commitment.
        byte[] nonce = numbered ? RandomNumberGenerator.GetBytes(32) : [];
        if (numbered) await stream.WriteAsync(CommitNonce(pin, nonce), token);
        var acknowledgement = new byte[24];
        await stream.ReadExactlyAsync(acknowledgement, token);
        ValidateHello(acknowledgement, 1, numbered);
        var session = acknowledgement[8..24];
        if (numbered) await stream.WriteAsync(nonce, token);
        return new SecureChannel(stream, session, numbered ? CompareCode(pin, session, nonce) : null, release, pathFailure);
    }

    internal static async Task<SecureChannel> AuthenticateHost(Stream stream, RoomIdentity identity, bool numbered,
        CancellationToken token, Action release, Func<IOException?>? pathFailure = null)
    {
        var hello = new byte[40];
        await stream.ReadExactlyAsync(hello, token);
        try
        {
            ValidateHello(hello, 2, numbered);
            if (!CryptographicOperations.FixedTimeEquals(hello.AsSpan(8, 32), identity.Capability))
                throw new AuthenticationException("ルームコードが一致しません。");
        }
        finally { CryptographicOperations.ZeroMemory(hello); }
        byte[] commitment = numbered ? new byte[32] : [];
        if (numbered) await stream.ReadExactlyAsync(commitment, token);
        var session = RandomNumberGenerator.GetBytes(16);
        var acknowledgement = new byte[24];
        "T12L"u8.CopyTo(acknowledgement);
        BinaryPrimitives.WriteUInt16LittleEndian(acknowledgement.AsSpan(4), ProtocolVersion);
        acknowledgement[6] = 1; // Host.
        acknowledgement[7] = numbered ? (byte)1 : (byte)0;
        session.CopyTo(acknowledgement, 8);
        await stream.WriteAsync(acknowledgement, token);
        string? code = null;
        if (numbered)
        {
            var nonce = new byte[32];
            await stream.ReadExactlyAsync(nonce, token);
            var pin = SHA256.HashData(identity.Certificate.RawData);
            if (!CryptographicOperations.FixedTimeEquals(commitment, CommitNonce(pin, nonce)))
                throw new AuthenticationException("相手の照合情報が一致しません。");
            code = CompareCode(pin, session, nonce);
        }
        return new SecureChannel(stream, session, code, release, pathFailure);
    }

    private static byte[] CommitNonce(byte[] pin, byte[] nonce) =>
        SHA256.HashData([.."TH12L7 guest commitment"u8, ..pin, ..nonce]);

    private static string CompareCode(byte[] pin, byte[] session, byte[] nonce)
    {
        var digest = SHA256.HashData([.."TH12L7 peer comparison"u8, ..pin, ..session, ..nonce]);
        return Convert.ToHexString(digest.AsSpan(0, 4)).Insert(4, "-");
    }

    private static void ValidateHello(byte[] hello, byte role, bool numbered)
    {
        if (!hello.AsSpan(0, 4).SequenceEqual("T12L"u8) || BinaryPrimitives.ReadUInt16LittleEndian(hello.AsSpan(4)) != ProtocolVersion ||
            hello[6] != role || hello[7] != (numbered ? 1 : 0)) throw new AuthenticationException("通信バージョン・接続方式または参加役割が一致しません。");
    }

    public async Task Send(byte[] payload, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
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
        catch (Exception) when (!token.IsCancellationRequested && pathFailure?.Invoke() is { } failure) { throw failure; }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("相手へのデータ送信が5秒以内に完了しませんでした。", error); }
        finally { writer.Release(); }
    }

    public async Task<byte[]> Receive(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
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
        catch (Exception) when (!token.IsCancellationRequested && pathFailure?.Invoke() is { } failure) { throw failure; }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("相手からのデータ受信が40秒以内に完了しませんでした。接続を確認してください。", error); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        release();
        // Ongoing writer cancellation owns its release; disposing this gate here
        // would race that release and hide the actual transport failure.
    }
}
