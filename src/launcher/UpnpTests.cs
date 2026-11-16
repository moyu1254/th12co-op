using System.Net;
using System.Net.Http;
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

        router.Entry = ("existing", 0);
        Expect(await UpnpPortMapping.OpenAsync(gateway, token) == null && router.Adds == 0 && router.Deletes == 0,
            "existing mapping left intact");
        router.Entry = null;
        var mapping = await UpnpPortMapping.OpenAsync(gateway, token) ?? throw new Exception("UPnP finite lease missing");
        await mapping.DisposeAsync();
        await mapping.DisposeAsync();
        Expect(router.Entry == null && router.Deletes == 1, "owned finite lease deleted once");

        mapping = await UpnpPortMapping.OpenAsync(gateway, token) ?? throw new Exception("UPnP lease missing");
        router.Entry = ("replacement", 3600);
        await mapping.DisposeAsync();
        Expect(router.Deletes == 1 && router.Entry?.Description == "replacement", "replacement mapping left intact");

        router.Entry = null;
        router.ReturnPermanent = true;
        Expect(await UpnpPortMapping.OpenAsync(gateway, token) == null && router.Entry == null && router.Deletes == 2,
            "permanent lease rejected and cleaned up");
        router.ReturnPermanent = false;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(token);
        router.AfterAdd = cancelled.Cancel;
        await ExpectThrowsAsync<OperationCanceledException>(() => UpnpPortMapping.OpenAsync(gateway, cancelled.Token));
        Expect(router.Entry == null && router.Deletes == 3, "mapping installed before cancellation cleaned up");
        passed.Add("UPnP simulated IGD: finite TCP 3012 lease, cancellation cleanup, existing/replaced mappings preserved, off-gateway URLs rejected");
    }

    private sealed class TestIgd : HttpMessageHandler
    {
        internal const string Service = "urn:schemas-upnp-org:service:WANIPConnection:2";
        public (string Description, int Duration)? Entry;
        public int Adds, Deletes;
        public bool ReturnPermanent;
        public Action? AfterAdd;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var soap = UpnpGateway.Soap;
            var call = XDocument.Parse(await request.Content!.ReadAsStringAsync(token)).Root!.Element(soap + "Body")!.Elements().Single();
            var action = call.Name.LocalName;
            Expect(request.RequestUri == new Uri("http://192.0.2.1/control") &&
                request.Headers.GetValues("SOAPAction").Single() == $"\"{Service}#{action}\"" &&
                call.Name.NamespaceName == Service && call.Element("NewExternalPort")?.Value == "3012" &&
                call.Element("NewProtocol")?.Value == "TCP" && call.Element("NewRemoteHost")?.Value == "", "UPnP request scope");
            var response = new XElement(XName.Get(action + "Response", Service));
            var status = HttpStatusCode.OK;
            switch (action)
            {
                case "GetSpecificPortMappingEntry" when Entry is { } entry:
                    response.Add(new XElement("NewInternalPort", 3012), new XElement("NewInternalClient", "192.0.2.11"),
                        new XElement("NewEnabled", 1), new XElement("NewPortMappingDescription", entry.Description),
                        new XElement("NewLeaseDuration", entry.Duration));
                    break;
                case "GetSpecificPortMappingEntry":
                    status = HttpStatusCode.InternalServerError;
                    response = new XElement(soap + "Fault", new XElement("detail", new XElement(UpnpGateway.Control + "UPnPError",
                        new XElement(UpnpGateway.Control + "errorCode", 714))));
                    break;
                case "AddPortMapping":
                    Expect(Entry == null && call.Element("NewLeaseDuration")?.Value == "660" &&
                        call.Element("NewInternalPort")?.Value == "3012" && call.Element("NewInternalClient")?.Value == "192.0.2.11",
                        "UPnP finite owned lease");
                    Adds++;
                    Entry = (call.Element("NewPortMappingDescription")!.Value, ReturnPermanent ? 0 : 660);
                    AfterAdd?.Invoke();
                    token.ThrowIfCancellationRequested();
                    break;
                case "DeletePortMapping": Deletes++; Entry = null; break;
                default: throw new Exception("Unexpected UPnP action");
            }
            var envelope = new XElement(soap + "Envelope", new XElement(soap + "Body", response));
            return new HttpResponseMessage(status) { Content = new StringContent(envelope.ToString(), Encoding.UTF8, "text/xml") };
        }
    }
}
