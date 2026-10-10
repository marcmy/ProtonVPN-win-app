using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.NetworkFilter;
using ProtonVPN.Service.Firewall;
using ProtonVPN.Service.ServerHealth;
using FilterAction = ProtonVPN.NetworkFilter.Action;
using NativeIpFilter = ProtonVPN.NetworkFilter.IpFilter;
using ServiceIpFilter = ProtonVPN.Service.Firewall.IpFilter;

namespace ProtonVPN.NetworkFilter.Tests;

[TestClass]
[DoNotParallelize] // Failure injection belongs to the inert native boundary, not production.
public sealed class SublayerConcurrencyTest
{
    private readonly NativeIpFilter _native = new(new Session(), Guid.NewGuid());
    private Sublayer _sublayer = null!;
    private AppFilter _apps = null!;
    private ServerHealthPermitManager _permits = null!;

    [TestInitialize]
    public void Initialize()
    {
        _sublayer = new(_native, Guid.NewGuid());
        ServiceIpFilter sharedFilter = new(_sublayer);
        _apps = new(Substitute.For<ILogger>(), sharedFilter, new IpLayer());
        _permits = new(sharedFilter, new IpLayer());
        IpFilterNative.CreationFailure = null;
    }

    [TestCleanup]
    public void Cleanup()
    {
        IpFilterNative.CreationFailure = null;
        _sublayer.DestroyAllFilters();
    }

    [TestMethod]
    public void Sublayer_DoesNotMaintainAnUnownedManagedFilterCollection()
    {
        FieldInfo[] fields = typeof(Sublayer).GetFields(BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsFalse(fields.Any(field => typeof(IEnumerable<Guid>).IsAssignableFrom(field.FieldType)),
            "Filter IDs must be owned by native WFP and the actual app/permit leases, not an append-only Sublayer cache.");
    }

    [TestMethod]
    [Timeout(30000)]
    public async Task AppApplyAndParallelProbePermits_AllCreatedFiltersRemainOwnedAndAreRemoved()
    {
        // The app worker is serial, just as under SplitTunnel._stateSync.
        // Independent probe workers share its sublayer without sharing that lock.
        using ManualResetEventSlim start = new(false);
        int permitCount = 0;
        Task appWorker = Task.Run(() =>
        {
            start.Wait();
            for (int i = 0; i < 512; i++)
            {
                _apps.Add([$@"C:\Apps\app-{i}.exe"], [
                    Tuple.Create(Layer.AppAuthConnectV4, FilterAction.HardPermit),
                    Tuple.Create(Layer.AppAuthConnectV6, FilterAction.HardBlock)]);
                if (i % 16 == 0) { _apps.RemoveAll(); }
            }
            _apps.RemoveAll();
        });

        Task[] probes = Enumerable.Range(1, 32).Select(worker => Task.Run(() =>
        {
            start.Wait();
            IPAddress address = IPAddress.Parse($"203.0.113.{worker}");
            for (int i = 0; i < 256; i++)
            {
                using IServerHealthPermitLease? lease = _permits.TryCreate(address);
                Assert.IsNotNull(lease, "A successfully created native permit must be returned to its owner.");
                Interlocked.Increment(ref permitCount);
            }
        })).ToArray();
        start.Set();
        await Task.WhenAll(probes.Append(appWorker));

        Assert.AreEqual(8192, permitCount);
        Assert.AreEqual(0u, _sublayer.GetFilterCount(), "App teardown and lease disposal must leave no orphaned filters.");
    }

    [TestMethod]
    public void AllFilterCreators_ReturnNativeOwnedIds_WithoutManagedTracking()
    {
        MethodInfo[] creators = typeof(Sublayer).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.ReturnType == typeof(Guid) && method.Name != "get_Id").ToArray();
        Assert.AreEqual(18, creators.Length);
        foreach (MethodInfo creator in creators)
        {
            object?[] arguments = creator.GetParameters().Select(parameter =>
                parameter.ParameterType == typeof(DisplayData) ? (object)new DisplayData(creator.Name, "") :
                parameter.ParameterType == typeof(string) ? @"C:\Apps\tool.exe" :
                parameter.ParameterType == typeof(Callout) ? new Callout(Guid.NewGuid()) :
                parameter.ParameterType == typeof(ProviderContext) ? new ProviderContext(Guid.NewGuid()) :
                parameter.ParameterType == typeof(NetworkAddress) ? NetworkAddress.FromIpv4("203.0.113.1", "255.255.255.255") :
                Activator.CreateInstance(parameter.ParameterType)).ToArray();
            Guid id = (Guid)creator.Invoke(_sublayer, arguments)!;
            Assert.AreNotEqual(Guid.Empty, id, creator.Name);
            CollectionAssert.Contains(_sublayer.GetFilters(), id, creator.Name);
            Assert.AreEqual(1u, _sublayer.GetFilterCount(), creator.Name);
            _sublayer.DestroyFilter(id);
            Assert.AreEqual(0u, _sublayer.GetFilterCount(), creator.Name);
        }
    }

    [TestMethod]
    public void CreateAppFilter_PreservesNativeIdentityAndPolicyArguments()
    {
        Guid requestedId = Guid.NewGuid();
        Guid id = _sublayer.CreateAppFilter(new("app", "description"), FilterAction.HardPermit,
            Layer.AppAuthConnectV4, 14, @"C:\Apps\tool.exe", true, true, requestedId);
        InertFilter filter = IpFilterNative.GetFilter(id);

        Assert.AreEqual(requestedId, id);
        Assert.AreEqual(_native.Session.Handle, filter.Session);
        Assert.AreEqual(_native.ProviderId, filter.Provider);
        Assert.AreEqual(_sublayer.Id, filter.Sublayer);
        Assert.AreEqual(FilterAction.HardPermit, filter.Action);
        Assert.AreEqual(Layer.AppAuthConnectV4, filter.Layer);
        Assert.AreEqual(14u, filter.Weight);
        Assert.AreEqual(@"C:\Apps\tool.exe", filter.App);
        Assert.IsTrue(filter.DnsExcluded);
        Assert.IsTrue(filter.Persistent);

        _sublayer.DestroyFilter(id);
        _sublayer.DestroyFilter(id); // Native not-found remains an idempotent teardown.
        Assert.AreEqual(0u, _sublayer.GetFilterCount());
    }

    [TestMethod]
    public void ProbePermit_DisposalRemovesOnlyItsOwnedFilter()
    {
        _apps.Add([@"C:\Apps\keep.exe"], [Tuple.Create(Layer.AppAuthConnectV4, FilterAction.HardPermit)]);
        using (IServerHealthPermitLease? lease = _permits.TryCreate(IPAddress.Parse("203.0.113.10")))
        {
            Assert.IsNotNull(lease);
            Assert.AreEqual(2u, _sublayer.GetFilterCount());
            InertFilter permit = _sublayer.GetFilters().Select(IpFilterNative.GetFilter)
                .Single(filter => filter.Address.HasValue);
            Assert.AreEqual("203.0.113.10", permit.Address!.Value.Address);
            Assert.AreEqual("255.255.255.255", permit.Address.Value.Mask);
            Assert.AreEqual(FilterAction.HardPermit, permit.Action);
            Assert.AreEqual(Layer.AppAuthConnectV4, permit.Layer);
            Assert.IsFalse(permit.Persistent);
            lease.Dispose();
        }
        Assert.AreEqual(1u, _sublayer.GetFilterCount());
        _apps.RemoveAll();
        Assert.AreEqual(0u, _sublayer.GetFilterCount());
    }

    [TestMethod]
    public void ProbePermit_NativeFailureDoesNotRemoveAppFilters_AndCanBeRetried()
    {
        _apps.Add([@"C:\Apps\keep.exe"], [Tuple.Create(Layer.AppAuthConnectV4, FilterAction.HardPermit)]);
        IpFilterNative.CreationFailure = filter => filter.Address.HasValue ? new NetworkFilterException(1) : null;
        Assert.IsNull(_permits.TryCreate(IPAddress.Parse("203.0.113.10")));
        Assert.AreEqual(1u, _sublayer.GetFilterCount());
        IpFilterNative.CreationFailure = null;
        using (IServerHealthPermitLease? lease = _permits.TryCreate(IPAddress.Parse("203.0.113.10")))
        {
            Assert.IsNotNull(lease);
        }
        Assert.AreEqual(1u, _sublayer.GetFilterCount());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AppFilter_PartialNativeFailureCleansUpAndAllowsRetry(bool invalidArgument)
    {
        const string path = @"C:\Apps\retry.exe";
        _apps.Add([@"C:\Apps\keep.exe"], [Tuple.Create(Layer.AppAuthConnectV4, FilterAction.HardPermit)]);
        Guid existingId = _sublayer.GetFilters().Single();
        Tuple<Layer, FilterAction>[] policy = [
            Tuple.Create(Layer.AppAuthConnectV4, FilterAction.HardPermit),
            Tuple.Create(Layer.AppAuthConnectV6, FilterAction.HardBlock)];
        IpFilterNative.CreationFailure = filter => filter.Layer == Layer.AppAuthConnectV6
            ? invalidArgument ? new InvalidArgumentException(1) : new NetworkFilterException(1)
            : null;

        if (invalidArgument) { _apps.Add([path], policy); }
        else { Assert.ThrowsExactly<NetworkFilterException>(() => _apps.Add([path], policy)); }

        CollectionAssert.AreEquivalent(new[] { existingId }, _sublayer.GetFilters(),
            "Rollback must remove the partial path without touching an existing app policy.");
        IpFilterNative.CreationFailure = null;
        _apps.Add([path], policy);
        Assert.AreEqual(3u, _sublayer.GetFilterCount(), "A failed path must not remain marked as already installed.");
        _apps.RemoveAll();
        Assert.AreEqual(0u, _sublayer.GetFilterCount());
    }
}
