/*
 * Copyright (c) 2026 Proton AG
 *
 * This file is part of ProtonVPN.
 *
 * ProtonVPN is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * ProtonVPN is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN.  If not, see <https://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProtonVPN.Common.Legacy;
using ProtonVPN.Common.Legacy.PortForwarding;
using ProtonVPN.Common.Legacy.Vpn;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.ConnectionLogs;
using ProtonVPN.Service.Settings;
using ProtonVPN.Vpn.Common;
using ProtonVPN.Vpn.PortMapping;

namespace ProtonVPN.Service.PortMapping;

internal sealed class PortForwardingForAppsRouter : IDisposable
{
    private const string MulticastAddress = "239.255.255.250";
    private const int SsdpPort = 1900;
    private const int HttpPort = 48991;
    private const string UrlPrefixPath = "protonvpn-port-forwarding";
    private const string RouterUuid = "uuid:8f3f0b78-6b5d-4f2a-b041-protonvpn-pfapps";

    private readonly object _sync = new();
    private readonly ILogger _logger;
    private readonly IServiceSettings _serviceSettings;
    private readonly IPortMappingProtocolClient _portMappingProtocolClient;

    private VpnState _vpnState = VpnState.Default;
    private PortForwardingState _portForwardingState = PortForwardingState.Default;
    private CancellationTokenSource _cancellationTokenSource;
    private UdpClient _ssdpClient;
    private HttpListener _httpListener;

    public PortForwardingForAppsRouter(
        ILogger logger,
        IServiceSettings serviceSettings,
        IPortMappingProtocolClient portMappingProtocolClient)
    {
        _logger = logger;
        _serviceSettings = serviceSettings;
        _portMappingProtocolClient = portMappingProtocolClient;

        _serviceSettings.SettingsChanged += OnSettingsChanged;
        _portMappingProtocolClient.StateChanged += OnPortMappingStateChanged;
    }

    public void SetVpnState(VpnState vpnState)
    {
        lock (_sync)
        {
            _vpnState = vpnState ?? VpnState.Default;
        }

        ReconcileState();
    }

    public async Task StopAsync()
    {
        await StopRouterAsync();
    }

    private void OnSettingsChanged(object sender, ProtonVPN.ProcessCommunication.Contracts.Entities.Settings.MainSettingsIpcEntity e)
    {
        ReconcileState();
    }

    private void OnPortMappingStateChanged(object sender, EventArgs<PortForwardingState> e)
    {
        lock (_sync)
        {
            _portForwardingState = e.Data ?? PortForwardingState.Default;
        }

        ReconcileState();
    }

    private void ReconcileState()
    {
        if (ShouldRun())
        {
            StartRouterIfNeeded();
        }
        else
        {
            _ = StopRouterAsync();
        }
    }

    private bool ShouldRun()
    {
        lock (_sync)
        {
            return _serviceSettings.IsPortForwardingForAppsEnabled &&
                   _vpnState.Status == VpnStatus.Connected &&
                   _vpnState.PortForwarding &&
                   IPAddress.TryParse(_vpnState.LocalIp, out _) &&
                   _portForwardingState.Status == PortMappingStatus.SleepingUntilRefresh &&
                   _portForwardingState.MappedPort?.MappedPort?.ExternalPort > 0;
        }
    }

    private int CurrentForwardedPort
    {
        get
        {
            lock (_sync)
            {
                return _portForwardingState.MappedPort?.MappedPort?.ExternalPort ?? 0;
            }
        }
    }

    private string LocalIp
    {
        get
        {
            lock (_sync)
            {
                return _vpnState.LocalIp;
            }
        }
    }

    private void StartRouterIfNeeded()
    {
        lock (_sync)
        {
            if (_cancellationTokenSource is not null)
            {
                return;
            }

            _cancellationTokenSource = new();
        }

        try
        {
            _logger.Info<ConnectionLog>($"Starting UPnP/NAT-PMP app port forwarding router. LocalIp={LocalIp}, ForwardedPort={CurrentForwardedPort}.");
            _ = Task.Run(() => RunSsdpResponderAsync(_cancellationTokenSource.Token));
            _ = Task.Run(() => RunHttpServerAsync(_cancellationTokenSource.Token));
        }
        catch (Exception e)
        {
            _logger.Error<ConnectionLog>("Failed to start UPnP/NAT-PMP app port forwarding router.", e);
            _ = StopRouterAsync();
        }
    }

    private async Task StopRouterAsync()
    {
        CancellationTokenSource cancellationTokenSource;
        lock (_sync)
        {
            cancellationTokenSource = _cancellationTokenSource;
            _cancellationTokenSource = null;
        }

        if (cancellationTokenSource is null)
        {
            return;
        }

        try
        {
            cancellationTokenSource.Cancel();
            _ssdpClient?.Close();
            _ssdpClient?.Dispose();
            _ssdpClient = null;

            if (_httpListener is not null)
            {
                _httpListener.Stop();
                _httpListener.Close();
                _httpListener = null;
            }

            _logger.Info<ConnectionLog>("Stopped UPnP/NAT-PMP app port forwarding router.");
        }
        catch (Exception e)
        {
            _logger.Error<ConnectionLog>("Failed to stop UPnP/NAT-PMP app port forwarding router cleanly.", e);
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }

        await Task.CompletedTask;
    }

    private async Task RunSsdpResponderAsync(CancellationToken cancellationToken)
    {
        try
        {
            using UdpClient client = new(AddressFamily.InterNetwork);
            _ssdpClient = client;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            client.Client.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));
            client.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));

            while (!cancellationToken.IsCancellationRequested)
            {
                UdpReceiveResult receiveResult = await client.ReceiveAsync(cancellationToken);
                string request = Encoding.ASCII.GetString(receiveResult.Buffer);

                if (IsSsdpSearch(request))
                {
                    foreach (string response in CreateSsdpResponses(request))
                    {
                        byte[] bytes = Encoding.ASCII.GetBytes(response);
                        await client.SendAsync(bytes, bytes.Length, receiveResult.RemoteEndPoint);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception e)
        {
            _logger.Error<ConnectionLog>("UPnP app port forwarding SSDP responder failed.", e);
            await StopRouterAsync();
        }
    }

    private bool IsSsdpSearch(string request)
    {
        return request.Contains("M-SEARCH", StringComparison.OrdinalIgnoreCase) &&
               request.Contains("ssdp:discover", StringComparison.OrdinalIgnoreCase) &&
               (request.Contains("ssdp:all", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("upnp:rootdevice", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("InternetGatewayDevice", StringComparison.OrdinalIgnoreCase) ||
                request.Contains("WANIPConnection", StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> CreateSsdpResponses(string request)
    {
        string[] searchTargets =
        [
            "upnp:rootdevice",
            "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
        ];

        foreach (string searchTarget in searchTargets)
        {
            yield return "HTTP/1.1 200 OK\r\n" +
                         "CACHE-CONTROL: max-age=60\r\n" +
                         "EXT:\r\n" +
                         $"LOCATION: http://{LocalIp}:{HttpPort}/{UrlPrefixPath}/rootDesc.xml\r\n" +
                         "SERVER: Windows NT/10.0 UPnP/1.1 ProtonVPN/1.0\r\n" +
                         $"ST: {searchTarget}\r\n" +
                         $"USN: {RouterUuid}::{searchTarget}\r\n" +
                         "\r\n";
        }
    }

    private async Task RunHttpServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            HttpListener listener = new();
            _httpListener = listener;
            listener.Prefixes.Add($"http://+:{HttpPort}/{UrlPrefixPath}/");
            listener.Start();

            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context = await listener.GetContextAsync();
                _ = Task.Run(() => HandleHttpRequestAsync(context), cancellationToken);
            }
        }
        catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception e)
        {
            _logger.Error<ConnectionLog>("UPnP app port forwarding HTTP listener failed.", e);
            await StopRouterAsync();
        }
    }

    private async Task HandleHttpRequestAsync(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (path.EndsWith("/rootDesc.xml", StringComparison.OrdinalIgnoreCase))
            {
                await WriteXmlAsync(context, CreateRootDescriptionXml());
            }
            else if (path.EndsWith("/wanipconnSCPD.xml", StringComparison.OrdinalIgnoreCase))
            {
                await WriteXmlAsync(context, CreateWanIpConnectionScpdXml());
            }
            else if (path.EndsWith("/control", StringComparison.OrdinalIgnoreCase))
            {
                await HandleSoapControlAsync(context);
            }
            else
            {
                context.Response.StatusCode = 404;
                context.Response.Close();
            }
        }
        catch (Exception e)
        {
            _logger.Error<ConnectionLog>("UPnP app port forwarding HTTP request failed.", e);
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
            }
        }
    }

    private async Task HandleSoapControlAsync(HttpListenerContext context)
    {
        using StreamReader reader = new(context.Request.InputStream, context.Request.ContentEncoding);
        string body = await reader.ReadToEndAsync();

        string response;
        if (body.Contains("GetExternalIPAddress", StringComparison.OrdinalIgnoreCase))
        {
            response = CreateSoapResponse("GetExternalIPAddressResponse", "<NewExternalIPAddress>0.0.0.0</NewExternalIPAddress>");
        }
        else if (body.Contains("AddAnyPortMapping", StringComparison.OrdinalIgnoreCase))
        {
            int port = CurrentForwardedPort;
            response = CreateSoapResponse("AddAnyPortMappingResponse", $"<NewReservedPort>{port}</NewReservedPort>");
        }
        else if (body.Contains("AddPortMapping", StringComparison.OrdinalIgnoreCase))
        {
            int requestedExternalPort = ExtractInt(body, "NewExternalPort");
            int currentForwardedPort = CurrentForwardedPort;
            if (requestedExternalPort == currentForwardedPort)
            {
                response = CreateSoapResponse("AddPortMappingResponse", string.Empty);
            }
            else
            {
                await WriteSoapErrorAsync(context, 718, $"Proton assigned external port {currentForwardedPort}, not requested port {requestedExternalPort}.");
                return;
            }
        }
        else if (body.Contains("DeletePortMapping", StringComparison.OrdinalIgnoreCase))
        {
            response = CreateSoapResponse("DeletePortMappingResponse", string.Empty);
        }
        else if (body.Contains("GetSpecificPortMappingEntry", StringComparison.OrdinalIgnoreCase))
        {
            int currentForwardedPort = CurrentForwardedPort;
            response = CreateSoapResponse("GetSpecificPortMappingEntryResponse",
                $"<NewInternalPort>{currentForwardedPort}</NewInternalPort>" +
                $"<NewInternalClient>{LocalIp}</NewInternalClient>" +
                "<NewEnabled>1</NewEnabled>" +
                "<NewPortMappingDescription>Proton VPN port forwarding</NewPortMappingDescription>" +
                "<NewLeaseDuration>60</NewLeaseDuration>");
        }
        else if (body.Contains("GetGenericPortMappingEntry", StringComparison.OrdinalIgnoreCase))
        {
            int currentForwardedPort = CurrentForwardedPort;
            response = CreateSoapResponse("GetGenericPortMappingEntryResponse",
                $"<NewRemoteHost></NewRemoteHost>" +
                $"<NewExternalPort>{currentForwardedPort}</NewExternalPort>" +
                "<NewProtocol>TCP</NewProtocol>" +
                $"<NewInternalPort>{currentForwardedPort}</NewInternalPort>" +
                $"<NewInternalClient>{LocalIp}</NewInternalClient>" +
                "<NewEnabled>1</NewEnabled>" +
                "<NewPortMappingDescription>Proton VPN port forwarding</NewPortMappingDescription>" +
                "<NewLeaseDuration>60</NewLeaseDuration>");
        }
        else
        {
            await WriteSoapErrorAsync(context, 401, "Invalid action");
            return;
        }

        await WriteXmlAsync(context, response);
    }

    private static int ExtractInt(string xml, string elementName)
    {
        string start = $"<{elementName}>";
        string end = $"</{elementName}>";
        int startIndex = xml.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
        {
            return 0;
        }

        startIndex += start.Length;
        int endIndex = xml.IndexOf(end, startIndex, StringComparison.OrdinalIgnoreCase);
        if (endIndex < startIndex)
        {
            return 0;
        }

        return int.TryParse(xml[startIndex..endIndex], out int value) ? value : 0;
    }

    private async Task WriteXmlAsync(HttpListenerContext context, string xml)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(xml);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/xml; charset=\"utf-8\"";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task WriteSoapErrorAsync(HttpListenerContext context, int errorCode, string description)
    {
        string xml = "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body><s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>" +
            "<UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\">" +
            $"<errorCode>{errorCode}</errorCode><errorDescription>{WebUtility.HtmlEncode(description)}</errorDescription>" +
            "</UPnPError></detail></s:Fault></s:Body></s:Envelope>";

        context.Response.StatusCode = 500;
        await WriteXmlAsync(context, xml);
    }

    private static string CreateSoapResponse(string actionName, string contents)
    {
        return "<?xml version=\"1.0\"?>" +
            "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
            "<s:Body>" +
            $"<u:{actionName} xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:2\">" +
            contents +
            $"</u:{actionName}>" +
            "</s:Body></s:Envelope>";
    }

    private string CreateRootDescriptionXml()
    {
        return "<?xml version=\"1.0\"?>" +
            "<root xmlns=\"urn:schemas-upnp-org:device-1-0\">" +
            "<specVersion><major>1</major><minor>1</minor></specVersion>" +
            $"<URLBase>http://{LocalIp}:{HttpPort}/{UrlPrefixPath}/</URLBase>" +
            "<device>" +
            "<deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:2</deviceType>" +
            "<friendlyName>Proton VPN Port Forwarding</friendlyName>" +
            "<manufacturer>Proton VPN</manufacturer>" +
            "<modelName>Proton VPN Virtual Router</modelName>" +
            "<UDN>" + RouterUuid + "</UDN>" +
            "<serviceList>" +
            "<service>" +
            "<serviceType>urn:schemas-upnp-org:service:WANIPConnection:2</serviceType>" +
            "<serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>" +
            $"<SCPDURL>/{UrlPrefixPath}/wanipconnSCPD.xml</SCPDURL>" +
            $"<controlURL>/{UrlPrefixPath}/control</controlURL>" +
            $"<eventSubURL>/{UrlPrefixPath}/event</eventSubURL>" +
            "</service>" +
            "</serviceList>" +
            "</device>" +
            "</root>";
    }

    private static string CreateWanIpConnectionScpdXml()
    {
        return "<?xml version=\"1.0\"?>" +
            "<scpd xmlns=\"urn:schemas-upnp-org:service-1-0\">" +
            "<specVersion><major>1</major><minor>1</minor></specVersion>" +
            "<actionList>" +
            "<action><name>GetExternalIPAddress</name></action>" +
            "<action><name>AddPortMapping</name></action>" +
            "<action><name>AddAnyPortMapping</name></action>" +
            "<action><name>DeletePortMapping</name></action>" +
            "<action><name>GetSpecificPortMappingEntry</name></action>" +
            "<action><name>GetGenericPortMappingEntry</name></action>" +
            "</actionList>" +
            "<serviceStateTable>" +
            "<stateVariable sendEvents=\"no\"><name>ExternalIPAddress</name><dataType>string</dataType></stateVariable>" +
            "<stateVariable sendEvents=\"no\"><name>PortMappingNumberOfEntries</name><dataType>ui2</dataType></stateVariable>" +
            "<stateVariable sendEvents=\"no\"><name>ConnectionStatus</name><dataType>string</dataType></stateVariable>" +
            "</serviceStateTable>" +
            "</scpd>";
    }

    public void Dispose()
    {
        _serviceSettings.SettingsChanged -= OnSettingsChanged;
        _portMappingProtocolClient.StateChanged -= OnPortMappingStateChanged;
        _ = StopRouterAsync();
    }
}
