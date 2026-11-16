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
    private const int MaxSources = 256;
    private readonly object gate = new();
    private readonly Dictionary<string, Reservation> numbers = new();
    private readonly Dictionary<IPAddress, Budget> sources = new();
    private Budget global = new(clock.GetTimestamp());

    private sealed record Entry(Candidate Candidate, byte[] OwnerToken);
    private sealed record Reservation(long Created, Entry? Entry);
    private sealed class Budget(long started)
    {
        internal readonly long Started = started;
        internal readonly int[] Uses = new int[3];
    }

    internal static IPAddress Normalize(IPAddress source) => source.IsIPv4MappedToIPv6 ? source.MapToIPv4() : source;

    private static IPAddress Network(IPAddress source)
    {
        source = Normalize(source);
        if (source.AddressFamily != AddressFamily.InterNetworkV6) return source;
        var bytes = source.GetAddressBytes();
        Array.Clear(bytes, 8, 8); // Privacy addresses on the same /64 share a budget.
        return new IPAddress(bytes);
    }

    internal bool Admit(Operation op, IPAddress source)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            if (clock.GetElapsedTime(global.Started, now) >= Lifetime) global = new(now);
            var (globalLimit, sourceLimit) = op switch
            {
                Operation.Create => (48, 4),
                Operation.Join => (120, 6),
                Operation.Close => (96, 12),
                _ => throw new ArgumentOutOfRangeException(nameof(op))
            };
            var index = (int)op;
            if (global.Uses[index] >= globalLimit) return false;
            var key = Network(source);
            if (!sources.TryGetValue(key, out var budget) || clock.GetElapsedTime(budget.Started, now) >= Lifetime)
            {
                if (budget is null && sources.Count >= MaxSources) return false;
                sources[key] = budget = new(now);
            }
            if (budget.Uses[index] >= sourceLimit) return false;
            ++budget.Uses[index];
            ++global.Uses[index];
            return true;
        }
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
            foreach (var (source, budget) in sources)
                if (clock.GetElapsedTime(budget.Started, now) >= Lifetime) sources.Remove(source);
        }
    }
}
