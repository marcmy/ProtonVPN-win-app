using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ProtonVPN.NetworkFilter
{
    // Only the OS/session boundary is replaced. Sublayer and its service callers
    // are linked directly from production source in this test project.
    public sealed class Session
    {
        private static long _nextHandle;
        public IntPtr Handle { get; } = new(Interlocked.Increment(ref _nextHandle));
    }

    public sealed class IpFilter(Session session, Guid providerId)
    {
        public Session Session { get; } = session;
        public Guid ProviderId { get; } = providerId;
    }

    internal sealed record InertFilter(
        IntPtr Session, Guid Provider, Guid Sublayer, DisplayData Display,
        Action Action, Layer Layer, uint Weight, bool Persistent,
        string? App = null, bool DnsExcluded = false, NetworkAddress? Address = null);

    internal static partial class IpFilterNative
    {
        private static readonly ConcurrentDictionary<Guid, InertFilter> _filters = new();
        internal static Func<InertFilter, Exception?>? CreationFailure { get; set; }

        private static Guid Create(InertFilter filter, Guid id)
        {
            if (CreationFailure?.Invoke(filter) is Exception exception)
            {
                throw exception;
            }

            if (id == Guid.Empty) { id = Guid.NewGuid(); }
            if (!_filters.TryAdd(id, filter)) { throw new NetworkFilterException(0x80320009); }
            return id;
        }

        internal static InertFilter GetFilter(Guid id) => _filters[id];

        public static List<Guid> GetSublayerFilters(IntPtr session, Guid provider, Guid sublayer) =>
            _filters.Where(pair => pair.Value.Session == session
                && pair.Value.Provider == provider && pair.Value.Sublayer == sublayer)
                .Select(pair => pair.Key).ToList();

        public static void DestroyFilter(IntPtr session, Guid id)
        {
            if (!_filters.TryGetValue(id, out InertFilter? filter) || filter.Session != session
                || !_filters.TryRemove(id, out _))
            {
                throw new FilterNotFoundException(0x80320003);
            }
        }

        public static void DestroySublayerFilters(IntPtr session, Guid provider, Guid sublayer)
        {
            foreach (Guid id in GetSublayerFilters(session, provider, sublayer))
            {
                DestroyFilter(session, id);
            }
        }

        public static void DestroySublayerFiltersByName(IntPtr session, Guid provider, Guid sublayer, string name)
        {
            foreach (Guid id in GetSublayerFilters(session, provider, sublayer))
            {
                if (_filters[id].Display.Name == name) { DestroyFilter(session, id); }
            }
        }
    }
}

namespace ProtonVPN.Service.Firewall
{
    public sealed class IpFilter(ProtonVPN.NetworkFilter.Sublayer sublayer)
    {
        public ProtonVPN.NetworkFilter.Sublayer DynamicSublayer { get; } = sublayer;
    }
}
