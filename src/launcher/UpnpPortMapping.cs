using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TH12Coop;

internal sealed class UpnpPortMapping : IAsyncDisposable
{
    internal const int LeaseSeconds = 660;
    internal readonly record struct PortKey(ProtocolType Protocol, int Number)
    {
        public string WireProtocol => Protocol switch
        {
            ProtocolType.Tcp => "TCP",
            ProtocolType.Udp => "UDP",
            _ => throw new ArgumentOutOfRangeException(nameof(Protocol))
        };
    }
    internal static readonly PortKey TcpPort = new(ProtocolType.Tcp, 3012);
    private readonly UpnpGateway gateway;
    private readonly PortKey port;
    private readonly string description = "TH12-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    private readonly CancellationTokenSource stop = new();
    private Task renewals = Task.CompletedTask;
    private int closed;
    public IPAddress? ExternalAddress { get; private set; }

    private UpnpPortMapping(UpnpGateway gateway, PortKey port) => (this.gateway, this.port) = (gateway, port);

    public static Task<UpnpPortMapping?> TryOpenUdpAsync(IPEndPoint boundEndpoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var address = boundEndpoint.Address;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.Equals(IPAddress.IPv6Any)) address = IPAddress.Any; // dual-stack socket
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
            return Task.FromResult<UpnpPortMapping?>(null);
        if (boundEndpoint.Port < 1024) throw new ArgumentOutOfRangeException(nameof(boundEndpoint));
        return TryOpenAsync(address, new PortKey(ProtocolType.Udp, boundEndpoint.Port), token);
    }

    private static async Task<UpnpPortMapping?> TryOpenAsync(IPAddress address, PortKey port, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var gateway = await UpnpGateway.DiscoverAsync(address, deadline.Token);
            if (gateway != null) return await OpenAsync(gateway, port, deadline.Token);
        }
        catch (Exception e) when (Unavailable(e)) { }
        token.ThrowIfCancellationRequested();
        return null;
    }

    private static bool Unavailable(Exception error) => error is IOException or HttpRequestException or SocketException
        or OperationCanceledException or NetworkInformationException or XmlException;

    internal static async Task<UpnpPortMapping?> OpenAsync(UpnpGateway gateway, PortKey port, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (await gateway.LookupAsync(port, token) != null) return null;
        var mapping = new UpnpPortMapping(gateway, port);
        bool keep = false;
        try
        {
            if (!await mapping.WriteLeaseAsync(token)) return null;
            if (port.Protocol == ProtocolType.Udp)
            {
                try { mapping.ExternalAddress = await gateway.ExternalAddressAsync(token); }
                catch (Exception e) when (Unavailable(e) && !token.IsCancellationRequested) { }
            }
            token.ThrowIfCancellationRequested();
            if (port.Protocol == ProtocolType.Udp) mapping.renewals = mapping.MaintainAsync();
            keep = true;
            return mapping;
        }
        finally
        {
            // A cancelled/lost Add reply can still have installed the mapping.
            if (!keep) await mapping.DisposeAsync();
        }
    }

    private bool Owns(XElement? entry) => entry != null &&
        entry.Element("NewInternalPort")?.Value == port.Number.ToString(CultureInfo.InvariantCulture) &&
        entry.Element("NewInternalClient")?.Value == gateway.LocalAddress.ToString() &&
        entry.Element("NewPortMappingDescription")?.Value == description;

    private async Task<bool> WriteLeaseAsync(CancellationToken token)
    {
        await gateway.AddAsync(port, description, token);
        var entry = await gateway.LookupAsync(port, token);
        return Owns(entry) && entry!.Element("NewEnabled")?.Value is ("1" or "true")
            && int.TryParse(entry.Element("NewLeaseDuration")?.Value, CultureInfo.InvariantCulture, out var duration)
            && duration is >= 600 and <= LeaseSeconds;
    }

    internal async Task<bool> RenewAsync(CancellationToken token)
    {
        if (!Owns(await gateway.LookupAsync(port, token))) return false;
        if (await WriteLeaseAsync(token)) return true;
        await RemoveOwnedAsync();
        return false;
    }

    private async Task MaintainAsync()
    {
        var interval = TimeSpan.FromMinutes(5);
        try
        {
            while (true)
            {
                await Task.Delay(interval, stop.Token).ConfigureAwait(false);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    if (!await RenewAsync(deadline.Token)) return;
                    interval = TimeSpan.FromMinutes(5);
                }
                catch (Exception e) when (Unavailable(e))
                {
                    // A lost reply is ambiguous. Re-check ownership on the next attempt.
                    interval = TimeSpan.FromMinutes(1);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        stop.Cancel();
        await renewals.ConfigureAwait(false);
        stop.Dispose();
        await RemoveOwnedAsync().ConfigureAwait(false);
    }

    private async Task RemoveOwnedAsync()
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            if (Owns(await gateway.LookupAsync(port, cleanup.Token))) await gateway.DeleteAsync(port, cleanup.Token);
        }
        catch (Exception e) when (Unavailable(e)) { } // The finite lease covers offline routers and process crashes.
    }
}

internal sealed class UpnpGateway(HttpClient client, Uri control, string service, IPAddress localAddress)
{
    private const string ServicePrefix = "urn:schemas-upnp-org:service:";
    private static readonly XNamespace Device = "urn:schemas-upnp-org:device-1-0";
    internal static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    internal static readonly XNamespace Control = "urn:schemas-upnp-org:control-1-0";
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseProxy = false, UseCookies = false, MaxResponseHeadersLength = 16
    }) { MaxResponseContentBufferSize = 64 * 1024 };

    internal IPAddress LocalAddress => localAddress;

    internal static async Task<UpnpGateway?> DiscoverAsync(IPAddress boundAddress, CancellationToken token)
    {
        var gateways = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Select(adapter => adapter.GetIPProperties())
            .Where(properties => boundAddress.Equals(IPAddress.Any) ||
                properties.UnicastAddresses.Any(local => local.Address.Equals(boundAddress)))
            .SelectMany(properties => properties.GatewayAddresses)
            .Select(gateway => gateway.Address)
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !ip.Equals(IPAddress.Any))
            .ToHashSet();
        if (gateways.Count == 0) return null;

        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.Bind(new IPEndPoint(boundAddress, 0));
        if (!boundAddress.Equals(IPAddress.Any))
            socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, boundAddress.GetAddressBytes());
        socket.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
        var search = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n");
        await socket.SendAsync(search, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), token);
        for (int i = 0; i < 16; i++)
        {
            var reply = await socket.ReceiveAsync(token);
            var gateway = reply.RemoteEndPoint.Address;
            if (!gateways.Contains(gateway) || reply.Buffer.Length > 4096) continue;
            var lines = Encoding.ASCII.GetString(reply.Buffer).Split("\r\n");
            if (!lines[0].StartsWith("HTTP/1.1 200 ", StringComparison.OrdinalIgnoreCase)) continue;
            var location = lines.FirstOrDefault(line => line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase));
            if (!Uri.TryCreate(location?[9..].Trim(), UriKind.Absolute, out var url) || !OnGateway(url, gateway)) continue;
            using var response = await Http.GetAsync(url, token);
            response.EnsureSuccessStatusCode();
            var device = await ReadXml(response, token);
            // Ask the routing table for our IPv4 address on the path to this gateway.
            using var route = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            route.Bind(new IPEndPoint(boundAddress, 0));
            route.Connect(new IPEndPoint(gateway, 1900));
            var local = ((IPEndPoint)route.LocalEndPoint!).Address;
            return FromDescription(Http, url, gateway, local, device);
        }
        return null;
    }

    private static bool OnGateway(Uri url, IPAddress gateway) => url.Scheme == Uri.UriSchemeHttp &&
        url.UserInfo.Length == 0 && url.Fragment.Length == 0 && IPAddress.TryParse(url.Host, out var ip) && ip.Equals(gateway);

    internal static UpnpGateway? FromDescription(HttpClient http, Uri url, IPAddress gateway, IPAddress local, XDocument device)
    {
        var baseUrl = url;
        var advertised = device.Root?.Element(Device + "URLBase")?.Value;
        if (advertised != null && !Uri.TryCreate(advertised, UriKind.Absolute, out baseUrl)) return null;
        if (!OnGateway(baseUrl, gateway)) return null;
        foreach (var item in device.Descendants(Device + "service"))
        {
            var type = item.Element(Device + "serviceType")?.Value;
            if (type is not (ServicePrefix + "WANIPConnection:1" or ServicePrefix + "WANIPConnection:2" or ServicePrefix + "WANPPPConnection:1")) continue;
            var path = item.Element(Device + "controlURL")?.Value;
            if (!string.IsNullOrWhiteSpace(path) && Uri.TryCreate(baseUrl, path, out var endpoint) && OnGateway(endpoint, gateway))
                return new UpnpGateway(http, endpoint, type, local);
        }
        return null;
    }

    private static async Task<XDocument> ReadXml(HttpResponseMessage response, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 65536 });
        return XDocument.Load(reader);
    }

    internal async Task<IPAddress?> ExternalAddressAsync(CancellationToken token)
    {
        var reply = await InvokeAsync("GetExternalIPAddress", null, token);
        return IPAddress.TryParse(reply?.Element("NewExternalIPAddress")?.Value, out var ip)
            && ip.AddressFamily == AddressFamily.InterNetwork && !ip.Equals(IPAddress.Any) ? ip : null;
    }

    internal Task<XElement?> LookupAsync(UpnpPortMapping.PortKey port, CancellationToken token) => InvokeAsync("GetSpecificPortMappingEntry", port, token);
    internal Task DeleteAsync(UpnpPortMapping.PortKey port, CancellationToken token) => InvokeAsync("DeletePortMapping", port, token);
    internal Task AddAsync(UpnpPortMapping.PortKey port, string description, CancellationToken token) => InvokeAsync("AddPortMapping", port, token,
        new XElement("NewInternalPort", port.Number), new XElement("NewInternalClient", localAddress),
        new XElement("NewEnabled", 1), new XElement("NewPortMappingDescription", description),
        new XElement("NewLeaseDuration", UpnpPortMapping.LeaseSeconds));

    private async Task<XElement?> InvokeAsync(string action, UpnpPortMapping.PortKey? port, CancellationToken token, params XElement[] fields)
    {
        XNamespace ns = service;
        var call = new XElement(ns + action, new XAttribute(XNamespace.Xmlns + "u", service));
        if (port is { } key)
            call.Add(new XElement("NewRemoteHost"), new XElement("NewExternalPort", key.Number), new XElement("NewProtocol", key.WireProtocol));
        call.Add(fields);
        var envelope = new XElement(Soap + "Envelope", new XAttribute(XNamespace.Xmlns + "s", Soap),
            new XAttribute(Soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"), new XElement(Soap + "Body", call));
        using var request = new HttpRequestMessage(HttpMethod.Post, control)
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("SOAPAction", $"\"{service}#{action}\"");
        using var response = await client.SendAsync(request, token);
        var body = (await ReadXml(response, token)).Element(Soap + "Envelope")?.Element(Soap + "Body");
        if (action == "GetSpecificPortMappingEntry" && response.StatusCode == HttpStatusCode.InternalServerError &&
            body?.Element(Soap + "Fault")?.Element("detail")?.Element(Control + "UPnPError")?.Element(Control + "errorCode")?.Value == "714")
            return null;
        response.EnsureSuccessStatusCode();
        return body?.Element(ns + (action + "Response")) ?? throw new IOException("UPnP の応答が不正です。");
    }
}
