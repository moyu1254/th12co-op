using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace TH12Coop;

internal static partial class SelfTests
{
    private static async Task UpnpTests(List<string> passed, CancellationToken token)
    {
        using var router = new TestIgd();
        using var http = new HttpClient(router);
        var address = IPAddress.Parse("192.0.2.1");
        var url = new Uri("http://192.0.2.1/igd.xml");
        var local = IPAddress.Parse("192.0.2.11");
        UpnpGateway? Gateway(string path, string? urlBase = null)
        {
            XNamespace ns = "urn:schemas-upnp-org:device-1-0";
            var doc = new XDocument(new XElement(ns + "root", urlBase == null ? null : new XElement(ns + "URLBase", urlBase),
                new XElement(ns + "service", new XElement(ns + "serviceType", TestIgd.Service), new XElement(ns + "controlURL", path))));
            return UpnpGateway.FromDescription(http, url, address, local, doc);
        }
        Expect(Gateway("http://example.invalid/control") == null && Gateway("/control", "http://192.0.2.2/") == null
            && Gateway("http://user@192.0.2.1/control") == null, "UPnP control URI stays on the discovered gateway");
        var gateway = Gateway("/control") ?? throw new Exception("UPnP service was not found");

        var tcp = UpnpPortMapping.TcpPort;
        router.Entries[("TCP", 3012)] = ("existing", 0);
        Expect(await UpnpPortMapping.OpenAsync(gateway, tcp, token) == null && router.Adds == 0 && router.Deletes == 0,
            "existing mapping left intact");
        router.Entries.Clear();
        var mapping = await UpnpPortMapping.OpenAsync(gateway, tcp, token) ?? throw new Exception("UPnP finite lease missing");
        await mapping.DisposeAsync();
        await mapping.DisposeAsync();
        Expect(router.Entries.Count == 0 && router.Deletes == 1, "owned finite lease deleted once");

        mapping = await UpnpPortMapping.OpenAsync(gateway, tcp, token) ?? throw new Exception("UPnP lease missing");
        router.Entries[("TCP", 3012)] = ("replacement", 3600);
        await mapping.DisposeAsync();
        Expect(router.Deletes == 1 && router.Entries[("TCP", 3012)].Description == "replacement", "replacement mapping left intact");

        router.Entries.Clear();
        router.ReturnPermanent = true;
        Expect(await UpnpPortMapping.OpenAsync(gateway, tcp, token) == null && router.Entries.Count == 0 && router.Deletes == 2,
            "permanent lease rejected and cleaned up");
        router.ReturnPermanent = false;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        router.AfterAdd = cancelled.Cancel;
        await ExpectThrowsAsync<OperationCanceledException>(() => UpnpPortMapping.OpenAsync(gateway, tcp, cancelled.Token));
        Expect(router.Entries.Count == 0 && router.Deletes == 3, "mapping installed before cancellation cleaned up");
        router.AfterAdd = null;

        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var endpoint = (IPEndPoint)socket.Client.LocalEndPoint!;
        var udp = new UpnpPortMapping.PortKey(ProtocolType.Udp, endpoint.Port);
        var udpKey = ("UDP", endpoint.Port);
        var tcpKey = ("TCP", endpoint.Port);
        router.Entries[tcpKey] = ("existing TCP lease", 0);
        mapping = await UpnpPortMapping.OpenAsync(gateway, udp, token) ?? throw new Exception("UPnP UDP lease missing");
        Expect(mapping.ExternalAddress?.Equals(IPAddress.Parse("203.0.113.5")) == true, "IGD external address available for mapped candidate");
        var name = router.Entries[udpKey].Description;
        router.Entries[udpKey] = (name, 340);
        Expect(await mapping.RenewAsync(token) && router.Entries[udpKey] == (name, 660), "renewed owned UDP lease at bound socket port");
        var adds = router.Adds;
        router.Entries[udpKey] = ("another UDP lease", 3600);
        Expect(!await mapping.RenewAsync(token) && router.Adds == adds, "replacement UDP lease not renewed");
        await mapping.DisposeAsync();
        Expect(router.Entries[udpKey].Description == "another UDP lease" && router.Entries[tcpKey].Description == "existing TCP lease",
            "same-port TCP and replacement UDP mappings preserved");
        router.Entries.Remove(udpKey);
        router.ExternalAddress = "0.0.0.0";
        mapping = await UpnpPortMapping.OpenAsync(gateway, udp, token) ?? throw new Exception("UPnP UDP lease missing");
        Expect(mapping.ExternalAddress == null, "unavailable external address omitted");
        router.ReturnPermanent = true;
        Expect(!await mapping.RenewAsync(token) && !router.Entries.ContainsKey(udpKey), "permanent UDP renewal removed");
        await mapping.DisposeAsync();
        Expect(await UpnpPortMapping.TryOpenUdpAsync(endpoint, token) == null, "loopback sockets skip gateway discovery");
        passed.Add("UPnP simulated IGD: finite TCP and bound UDP port leases, UDP renewal, existing/replaced mappings preserved, cancellation cleanup");
    }

    private sealed class TestIgd : HttpMessageHandler
    {
        internal const string Service = "urn:schemas-upnp-org:service:WANIPConnection:2";
        public readonly Dictionary<(string Protocol, int Port), (string Description, int Duration)> Entries = [];
        public int Adds, Deletes;
        public bool ReturnPermanent;
        public Action? AfterAdd;
        public string ExternalAddress = "203.0.113.5";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var soap = UpnpGateway.Soap;
            var call = XDocument.Parse(await request.Content!.ReadAsStringAsync(token)).Root!.Element(soap + "Body")!.Elements().Single();
            var action = call.Name.LocalName;
            var protocol = call.Element("NewProtocol")?.Value ?? "";
            var port = (int?)call.Element("NewExternalPort") ?? 0;
            var key = (protocol, port);
            Expect(request.RequestUri == new Uri("http://192.0.2.1/control") &&
                request.Headers.GetValues("SOAPAction").Single() == $"\"{Service}#{action}\"" &&
                call.Name.NamespaceName == Service, "UPnP request service");
            Expect(action == "GetExternalIPAddress" ? !call.HasElements : (protocol == "UDP" || (protocol == "TCP" && port == 3012))
                && call.Element("NewRemoteHost")?.Value == "", "UPnP request scope");
            var response = new XElement(XName.Get(action + "Response", Service));
            var status = HttpStatusCode.OK;
            switch (action)
            {
                case "GetExternalIPAddress":
                    response.Add(new XElement("NewExternalIPAddress", ExternalAddress));
                    break;
                case "GetSpecificPortMappingEntry" when Entries.TryGetValue(key, out var entry):
                    response.Add(new XElement("NewInternalPort", port), new XElement("NewInternalClient", "192.0.2.11"),
                        new XElement("NewEnabled", 1), new XElement("NewPortMappingDescription", entry.Description),
                        new XElement("NewLeaseDuration", entry.Duration));
                    break;
                case "GetSpecificPortMappingEntry":
                    status = HttpStatusCode.InternalServerError;
                    response = new XElement(soap + "Fault", new XElement("detail", new XElement(UpnpGateway.Control + "UPnPError",
                        new XElement(UpnpGateway.Control + "errorCode", 714))));
                    break;
                case "AddPortMapping":
                    var description = call.Element("NewPortMappingDescription")!.Value;
                    Expect((!Entries.TryGetValue(key, out var existing) || existing.Description == description) &&
                        call.Element("NewLeaseDuration")?.Value == "660" && (int?)call.Element("NewInternalPort") == port &&
                        call.Element("NewInternalClient")?.Value == "192.0.2.11",
                        "UPnP finite owned lease");
                    Adds++;
                    Entries[key] = (description, ReturnPermanent ? 0 : 660);
                    AfterAdd?.Invoke();
                    token.ThrowIfCancellationRequested();
                    break;
                case "DeletePortMapping": Deletes++; Entries.Remove(key); break;
                default: throw new Exception("Unexpected UPnP action");
            }
            var envelope = new XElement(soap + "Envelope", new XElement(soap + "Body", response));
            return new HttpResponseMessage(status) { Content = new StringContent(envelope.ToString(), Encoding.UTF8, "text/xml") };
        }
    }
}
