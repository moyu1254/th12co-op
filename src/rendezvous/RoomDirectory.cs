using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace TH12Coop.Rendezvous;

internal sealed record Registration(string Number, string OwnerToken);
internal sealed record Claim(string Invite, string GuestToken);
internal enum Operation { Create, Join, Close, Proof, Offer, Answer, Receive }

internal sealed class RoomDirectory(TimeProvider clock)
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan AnswerRetention = TimeSpan.FromSeconds(90);
    private readonly object gate = new();
    private readonly Dictionary<string, Reservation> numbers = new();
    private readonly AdmissionWindow creates = new(4, 8, 12, 48);
    private readonly AdmissionWindow joins = new(6, 12, 24, 120);
    private readonly AdmissionWindow closes = new(12, 24, 36, 96);

    private readonly AdmissionWindow proofRequests = new(24, 48, 72, 384);
    // Include every short poll of a full-length room in its source and global budget.
    private readonly AdmissionWindow offerRequests = new(160, 320, 480, 1920);
    private readonly AdmissionWindow answerRequests = new(12, 24, 36, 96);
    private readonly AdmissionWindow receiveRequests = new(320, 640, 1280, 3200);
    private readonly Dictionary<string, (string Address, long At)> proofs = new();

    private sealed class Mailbox
    {
        internal IceDescription? Value;
        internal TaskCompletionSource<IceDescription?>? Waiter;

        internal void Deliver(IceDescription value)
        {
            // Completing a poll is not a delivery receipt. Keep the immutable value
            // until the next protocol step, or until its absolute retention deadline.
            Value = value;
            Waiter?.TrySetResult(value);
        }
    }

    private sealed class Entry(string invite, byte[] owner)
    {
        internal string? Invite = invite;
        internal readonly byte[] Owner = owner;
        internal byte[]? Guest;
        internal long? AnsweredAt;
        internal readonly Mailbox Offer = new(), Answer = new();
    }
    private sealed record Reservation(long AdmittedAt, Entry? Entry);
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

    internal bool Admit(Operation op, IPAddress source, out long admittedAt)
    {
        var window = op switch
        {
            Operation.Create => creates,
            Operation.Join => joins,
            Operation.Close => closes,
            Operation.Proof => proofRequests,
            Operation.Offer => offerRequests,
            Operation.Answer => answerRequests,
            Operation.Receive => receiveRequests,
            _ => throw new ArgumentOutOfRangeException(nameof(op))
        };
        lock (gate)
        {
            admittedAt = clock.GetTimestamp();
            return window.TryAdmit(Network.From(source), admittedAt, clock);
        }
    }

    internal static bool ValidNumber(string? value) => value is { Length: 6 } && value.All(char.IsAsciiDigit);
    internal static bool ValidToken(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitUpper);
    internal static bool ValidInvite(string? value) =>
        value is { Length: 92 } && value.StartsWith("T12L6.", StringComparison.Ordinal)
        && value.AsSpan(6).IndexOfAnyExcept("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") < 0
        && "AQgw".Contains(value[^1]); // 64 bytes: only the high two bits of the final character carry data.

    internal AddressProof? IssueProof(IPAddress source, long admittedAt)
    {
        source = Normalize(source);
        if (!IceDescription.AddressAllowed(source, loopbackService: true)) return null;
        lock (gate)
        {
            var now = clock.GetTimestamp();
            RemoveExpired(now);
            if (clock.GetElapsedTime(admittedAt, now) >= Lifetime) return null;
            var proof = new AddressProof(source.ToString(), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            proofs.Add(proof.Token, (proof.Address, admittedAt));
            return proof;
        }
    }

    // Each published address must have been seen on a direct TLS connection by this directory.
    // Independent proofs for IPv4 and IPv6 permit dual-stack candidate sets.
    private bool Verified(VerifiedIce? ice, IPAddress source)
    {
        if (ice?.Description?.IsValid(IPAddress.IsLoopback(Normalize(source))) != true ||
            ice.Proofs is not { Length: > 0 and <= 2 }) return false;
        return ice.Description.Candidates.All(c => ice.Proofs.Any(p => ValidToken(p) &&
            proofs.TryGetValue(p, out var proof) && proof.Address == c.Address));
    }

    internal Registration? Create(string invite, long admittedAt)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            RemoveExpired(now);
            if (clock.GetElapsedTime(admittedAt, now) >= Lifetime) return null;
            string number;
            do { number = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture); }
            while (numbers.ContainsKey(number));
            var owner = RandomNumberGenerator.GetBytes(32);
            numbers.Add(number, new(admittedAt, new(invite, owner)));
            return new(number, Convert.ToHexString(owner));
        }
    }

    internal Claim? Join(string number, VerifiedIce offer, IPAddress source)
    {
        lock (gate)
        {
            RemoveExpired(clock.GetTimestamp());
            if (!numbers.TryGetValue(number, out var room) || room.Entry is not { Invite: { } invite } entry ||
                !Verified(offer, source)) return null;
            entry.Invite = null;
            entry.Guest = RandomNumberGenerator.GetBytes(32);
            entry.Offer.Deliver(offer.Description);
            return new(invite, Convert.ToHexString(entry.Guest));
        }
    }

    internal bool Answer(string number, byte[] owner, VerifiedIce answer, IPAddress source)
    {
        lock (gate)
        {
            var now = clock.GetTimestamp();
            RemoveExpired(now);
            if (!numbers.TryGetValue(number, out var room) || room.Entry is not { } entry ||
                !CryptographicOperations.FixedTimeEquals(entry.Owner, owner) || !Verified(answer, source)) return false;
            if (entry.Answer.Value is { } previous)
                return previous.Ufrag == answer.Description.Ufrag && previous.Password == answer.Description.Password &&
                       previous.Candidates.SequenceEqual(answer.Description.Candidates);
            if (entry.Offer.Value == null) return false;
            entry.Offer.Value = null; // Publishing the answer acknowledges the offer.
            entry.AnsweredAt = now;
            entry.Answer.Deliver(answer.Description);
            return true;
        }
    }

    internal readonly record struct Poll(bool Exists, IceDescription? Description);

    internal async Task<Poll> Wait(string number, byte[] token, bool host, CancellationToken cancellation)
    {
        TaskCompletionSource<IceDescription?> waiter;
        Mailbox box;
        lock (gate)
        {
            RemoveExpired(clock.GetTimestamp());
            if (!numbers.TryGetValue(number, out var room) || room.Entry is not { } entry) return default;
            var expected = host ? entry.Owner : entry.Guest;
            if (expected == null || !CryptographicOperations.FixedTimeEquals(expected, token) ||
                host && entry.AnsweredAt.HasValue) return default;
            box = host ? entry.Offer : entry.Answer;
            if (box.Value is { } value) return new(true, value);
            if (box.Waiter != null) return new(true, null);
            box.Waiter = waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            var value = await waiter.Task.WaitAsync(TimeSpan.FromSeconds(host ? 20 : 2), clock, cancellation);
            return new(value != null, value);
        }
        catch (TimeoutException) { return new(true, null); }
        finally { lock (gate) { if (box.Waiter == waiter) box.Waiter = null; } }
    }

    internal bool Close(string number, byte[] owner)
    {
        lock (gate)
        {
            RemoveExpired(clock.GetTimestamp());
            if (!numbers.TryGetValue(number, out var room) || room.Entry is not { } entry ||
                !CryptographicOperations.FixedTimeEquals(entry.Owner, owner)) return false;
            Retire(number, room);
            return true;
        }
    }

    private void Retire(string number, Reservation room)
    {
        if (room.Entry is { } entry)
        {
            entry.Offer.Waiter?.TrySetResult(null);
            entry.Answer.Waiter?.TrySetResult(null);
            CryptographicOperations.ZeroMemory(entry.Owner);
            if (entry.Guest != null) CryptographicOperations.ZeroMemory(entry.Guest);
        }
        // Never reuse a number before its original create admission expires.
        numbers[number] = room with { Entry = null };
    }

    internal void RemoveExpired()
    {
        lock (gate) RemoveExpired(clock.GetTimestamp());
    }

    private void RemoveExpired(long now)
    {
        foreach (var (number, reservation) in numbers)
        {
            if (clock.GetElapsedTime(reservation.AdmittedAt, now) >= Lifetime)
            {
                Retire(number, reservation);
                numbers.Remove(number);
            }
            else if (reservation.Entry?.AnsweredAt is { } at && clock.GetElapsedTime(at, now) >= AnswerRetention)
                Retire(number, reservation);
        }
        foreach (var (proof, value) in proofs)
            if (clock.GetElapsedTime(value.At, now) >= Lifetime) proofs.Remove(proof);
        proofRequests.Expire(now, clock);
        offerRequests.Expire(now, clock);
        answerRequests.Expire(now, clock);
        receiveRequests.Expire(now, clock);
        creates.Expire(now, clock);
        joins.Expire(now, clock);
        closes.Expire(now, clock);
    }
}
