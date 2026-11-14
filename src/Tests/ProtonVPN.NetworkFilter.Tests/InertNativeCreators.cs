using System;

namespace ProtonVPN.NetworkFilter;

internal static partial class IpFilterNative
{
    public static Guid CreateAppFilter(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, Guid callout,
        Guid context, string app, bool dnsExcluded, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent,
            App: app, DnsExcluded: dnsExcluded), id);

    public static Guid CreateRemoteNetworkIPFilter(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, Guid callout,
        Guid context, NetworkAddress address, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent,
            Address: address), id);

    public static Guid CreateLayerFilter(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, Guid callout,
        Guid context, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent), id);

    public static Guid CreateRemoteIPv4Filter(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, Guid callout,
        Guid context, string address, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent,
            Address: NetworkAddress.FromIpv4(address, "255.255.255.255")), id);

    public static Guid CreateRemoteUdpPortFilter(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, uint port, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent), id);

    public static Guid CreateRemoteTcpPortFilter(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, uint port, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent), id);

    public static Guid CreateNetInterfaceFilter(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, uint index, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent), id);

    public static Guid CreateLoopbackFilter(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, bool persistent) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent), Guid.Empty);

    public static Guid BlockOutsideDns(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, Guid callout, uint index, uint persistent) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent != 0), Guid.Empty);

    public static Guid BlockOutsideOpenVpn(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, uint weight, string path, string address, uint persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, Action.HardBlock, layer, weight, persistent != 0), id);

    public static Guid PermitRouterSolicitationMessage(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, Guid callout, Guid context, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent), id);

    public static Guid PermitRouterAdvertisementMessage(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, Guid callout, Guid context, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent), id);

    public static Guid PermitNeighborSolicitationMessage(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, Guid callout, Guid context, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent), id);

    public static Guid PermitNeighborAdvertisementMessage(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, Guid callout, Guid context, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent), id);

    public static Guid PermitIcmpRedirectMessage(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, Guid callout, Guid context, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent), id);

    public static Guid PermitOutboundIpv6Dhcp(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, Guid callout, Guid context, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent), id);

    public static Guid PermitInboundIpv6Dhcp(IntPtr session, Guid provider, Guid sublayer,
        DisplayData display, Layer layer, Action action, uint weight, Guid callout, Guid context, bool persistent, Guid id) =>
        Create(new(session, provider, sublayer, display, action, layer, weight, persistent), id);
}
