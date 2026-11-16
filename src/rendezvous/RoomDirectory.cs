using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace TH12Coop.Rendezvous;

internal sealed record Registration(string Number, string OwnerToken);
internal sealed record Candidate(string Invite, string Address, int Port);
internal enum Operation { Create, Join, Close }

internal sealed class RoomDirectory(TimeProvider clock)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    internal const int Port = 3012;
    private const int MaxReservations = 32;
    private readonly object gate = new();
    private readonly Dictionary<string, Reservation> numbers = new();
    private readonly AdmissionWindow creates = new(4, 8, 12, 48);
    private readonly AdmissionWindow joins = new(6, 12, 24, 120);
    private readonly AdmissionWindow closes = new(12, 24, 36, 96);

    private sealed record Entry(Candidate Candidate, byte[] OwnerToken);
    private sealed record Reservation(long Created, Entry? Entry);
    internal static IPAddress Normalize(IPAddress source) => source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source;

    private readonly record struct Network(bool IsIPv6, ulong Bits)
    {
        internal static Network From(IPAddress address)
        {
            address = Normalize(address);
            Span<byte> bytes = stackalloc byte[16];
            address.TryWriteBytes(bytes, out _);
            return address.AddressFamily == AddressFamily.InterNetworkV6
                ? new(true, BinaryPrimitives.ReadUInt64BigEndian(bytes))  // /64
                : new(false, BinaryPrimitives.ReadUInt32BigEndian(bytes)); // /32
        }
    }

    private sealed class AdmissionWindow(int sourceLimit, int ipv6Limit56, int ipv6Limit48, int globalLimit)
    {
        // Only admitted requests occupy space. The global operation limit bounds both storage
        // and the scan, even when every request presents a previously unseen source address.
        private readonly Queue<(long At, Network Network)> accepted = new();

        internal void Expire(long now, TimeProvider clock)
        {
            while (accepted.TryPeek(out var first) && clock.GetElapsedTime(first.At, now) >= Lifetime)
                accepted.Dequeue();
        }

        internal bool TryAdmit(Network source, long now, TimeProvider clock)
        {
            Expire(now, clock);
            if (accepted.Count >= globalLimit) return false;
            int sameSource = 0, same56 = 0, same48 = 0;
            foreach (var (_, other) in accepted)
            {
                if (source == other && ++sameSource >= sourceLimit) return false;
                if (!source.IsIPv6 || !other.IsIPv6) continue;
                if ((source.Bits >> 8) == (other.Bits >> 8) && ++same56 >= ipv6Limit56) return false;
                if ((source.Bits >> 16) == (other.Bits >> 16) && ++same48 >= ipv6Limit48) return false;
            }
            accepted.Enqueue((now, source));
            return true;
        }
    }

    internal bool Admit(Operation op, IPAddress source)
    {
        var window = op switch
        {
            Operation.Create => creates,
            Operation.Join => joins,
            Operation.Close => closes,
            _ => throw new ArgumentOutOfRangeException(nameof(op))
        };
        lock (gate) return window.TryAdmit(Network.From(source), clock.GetTimestamp(), clock);
    }

    internal static bool ValidNumber(string? value) => value is { Length: 6 } && value.All(char.IsAsciiDigit);
    internal static bool ValidToken(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitUpper);
    internal static bool ValidInvite(string? value) =>
        value is { Length: 92 } && value.StartsWith("T12L5.", StringComparison.Ordinal)
        && value.AsSpan(6).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0
        && "AQgw".Contains(value[^1]); // 64 bytes: only the high two bits of the final character carry data.

    internal Registration? Create(string invite, IPAddress source)
    {
        lock (gate)
        {
            RemoveExpired();
            if (numbers.Count >= MaxReservations) return null;
            string number;
            do { number = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture); }
            while (numbers.ContainsKey(number));
            var token = RandomNumberGenerator.GetBytes(32);
            var entry = new Entry(new(invite, Normalize(source).ToString(), Port), token);
            numbers.Add(number, new(clock.GetTimestamp(), entry));
            return new(number, Convert.ToHexString(token));
        }
    }

    internal Candidate? Take(string number, byte[]? ownerToken = null)
    {
        lock (gate)
        {
            RemoveExpired();
            if (!numbers.TryGetValue(number, out var reservation) || reservation.Entry is not { } entry) return null;
            if (ownerToken != null && !CryptographicOperations.FixedTimeEquals(entry.OwnerToken, ownerToken)) return null;
            // Retire the number until its original expiry so a stale invitation cannot claim a new room.
            numbers[number] = reservation with { Entry = null };
            CryptographicOperations.ZeroMemory(entry.OwnerToken);
            return entry.Candidate;
        }
    }

    internal void RemoveExpired()
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            foreach (var (number, reservation) in numbers)
            {
                if (clock.GetElapsedTime(reservation.Created, now) < Lifetime) continue;
                if (reservation.Entry is { } entry) CryptographicOperations.ZeroMemory(entry.OwnerToken);
                numbers.Remove(number);
            }
            creates.Expire(now, clock);
            joins.Expire(now, clock);
            closes.Expire(now, clock);
        }
    }
}
