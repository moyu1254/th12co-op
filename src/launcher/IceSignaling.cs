using System.Net;
using System.Net.Sockets;

#if RENDEZVOUS_SERVER
namespace TH12Coop.Rendezvous;
#else
namespace TH12Coop;
#endif

// Only ICE credentials and checked numeric endpoints cross the signaling boundary. Never feed SDP,
// DNS names, related addresses, or arbitrary ICE-server URLs to the transport library.
internal sealed record IceEndpoint(string Address, int Port);
internal sealed record IceDescription(string Ufrag, string Password, IceEndpoint[] Candidates)
{
    internal const int MaxCandidates = 4;
    // An explicit negative result is a signaling message, never an ICE description
    // to hand to the agent. No address, credential or proof is needed for it.
    internal static readonly IceDescription NoDirect = new("", "", []);
    internal bool IsNoDirect => Ufrag == "" && Password == "" && Candidates is { Length: 0 };
    internal bool IsValidSignal(bool allowLoopback) => IsNoDirect || IsValid(allowLoopback);

    internal bool IsValid(bool allowLoopback) =>
        IsIceString(Ufrag, 4) && IsIceString(Password, 22) &&
        Candidates is { Length: > 0 and <= MaxCandidates } &&
        Candidates.All(c => c != null && c.Port is >= 1024 and <= 65535 &&
            IPAddress.TryParse(c.Address, out var ip) && ip.ToString() == c.Address && AddressAllowed(ip, allowLoopback)) &&
        Candidates.Distinct().Count() == Candidates.Length;

    private static bool IsIceString(string? s, int min) => s != null && s.Length >= min && s.Length <= 256 &&
        s.All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/');

    internal static bool AddressAllowed(IPAddress address, bool loopbackService)
    {
        if (address.IsIPv4MappedToIPv6) return false;
        if (IPAddress.IsLoopback(address)) return loopbackService;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.ScopeId == 0 && GlobalV6.Contains(address) && !ExcludedV6.Any(net => net.Contains(address));
        return address.AddressFamily == AddressFamily.InterNetwork && !ExcludedV4.Any(net => net.Contains(address));
    }

    private static readonly IPNetwork GlobalV6 = IPNetwork.Parse("2000::/3");
    private static readonly IPNetwork[] ExcludedV6 = Networks("2001::/23", "2001:db8::/32", "2002::/16", "3fff::/20");
    private static readonly IPNetwork[] ExcludedV4 = Networks("0.0.0.0/8", "10.0.0.0/8", "100.64.0.0/10",
        "127.0.0.0/8", "169.254.0.0/16", "172.16.0.0/12", "192.0.0.0/24", "192.0.2.0/24", "192.88.99.0/24",
        "192.168.0.0/16", "198.18.0.0/15", "198.51.100.0/24", "203.0.113.0/24", "224.0.0.0/4", "240.0.0.0/4");
    private static IPNetwork[] Networks(params string[] ranges) => Array.ConvertAll(ranges, IPNetwork.Parse);
}

internal sealed record AddressProof(string Address, string Token);
internal sealed record VerifiedIce(IceDescription Description, string[] Proofs);
