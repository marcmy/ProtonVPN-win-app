#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.NetworkFilter;
using ProtonVPN.Service.Firewall;
using Action = ProtonVPN.NetworkFilter.Action;
using CoreNetworkAddress = ProtonVPN.Common.Core.Networking.NetworkAddress;

namespace ProtonVPN.Service.Tests.Firewall;

[TestClass]
public class PermittedRemoteAddressTest
{
    private const string EXISTING_ADDRESS = "192.0.2.10";
    private const string NEW_ADDRESS_1 = "198.51.100.10";
    private const string NEW_ADDRESS_2 = "203.0.113.10";

    [TestMethod]
    public void Add_WhenReconciliationSucceeds_CommitsNewSetAndRemovesStaleFilters()
    {
        TestPermittedRemoteAddress permittedRemoteAddress = new();
        permittedRemoteAddress.Add([EXISTING_ADDRESS], Action.HardPermit);

        permittedRemoteAddress.Add([NEW_ADDRESS_1, NEW_ADDRESS_2], Action.HardPermit);

        CollectionAssert.AreEquivalent(
            new[] { NEW_ADDRESS_1, NEW_ADDRESS_2 },
            permittedRemoteAddress.ActiveAddresses.ToArray());
        Assert.AreEqual(2, permittedRemoteAddress.CommitCount);
        Assert.AreEqual(0, permittedRemoteAddress.AbortCount);
    }

    [TestMethod]
    public void Add_WhenFirstNewAddressFails_PreservesPreviousKnownGoodSet()
    {
        TestPermittedRemoteAddress permittedRemoteAddress = new();
        permittedRemoteAddress.Add([EXISTING_ADDRESS], Action.HardPermit);
        permittedRemoteAddress.FailingAddress = NEW_ADDRESS_1;

        permittedRemoteAddress.Add([NEW_ADDRESS_1], Action.HardPermit);

        CollectionAssert.AreEquivalent(
            new[] { EXISTING_ADDRESS },
            permittedRemoteAddress.ActiveAddresses.ToArray());
        Assert.AreEqual(1, permittedRemoteAddress.CommitCount);
        Assert.AreEqual(1, permittedRemoteAddress.AbortCount);
    }

    [TestMethod]
    public void Add_WhenLaterNewAddressFails_RollsBackEarlierNewFiltersAndPreservesStaleFilters()
    {
        TestPermittedRemoteAddress permittedRemoteAddress = new();
        permittedRemoteAddress.Add([EXISTING_ADDRESS], Action.HardPermit);
        permittedRemoteAddress.FailingAddress = NEW_ADDRESS_2;

        permittedRemoteAddress.Add([NEW_ADDRESS_1, NEW_ADDRESS_2], Action.HardPermit);

        CollectionAssert.AreEquivalent(
            new[] { EXISTING_ADDRESS },
            permittedRemoteAddress.ActiveAddresses.ToArray());
        Assert.AreEqual(1, permittedRemoteAddress.CommitCount);
        Assert.AreEqual(1, permittedRemoteAddress.AbortCount);
    }

    [TestMethod]
    public void Add_AfterFailedReconciliation_CanReconcileCleanlyOnNextAttempt()
    {
        TestPermittedRemoteAddress permittedRemoteAddress = new();
        permittedRemoteAddress.Add([EXISTING_ADDRESS], Action.HardPermit);
        permittedRemoteAddress.FailingAddress = NEW_ADDRESS_2;
        permittedRemoteAddress.Add([NEW_ADDRESS_1, NEW_ADDRESS_2], Action.HardPermit);
        permittedRemoteAddress.FailingAddress = null;

        permittedRemoteAddress.Add([NEW_ADDRESS_1, NEW_ADDRESS_2], Action.HardPermit);

        CollectionAssert.AreEquivalent(
            new[] { NEW_ADDRESS_1, NEW_ADDRESS_2 },
            permittedRemoteAddress.ActiveAddresses.ToArray());
        Assert.AreEqual(2, permittedRemoteAddress.CommitCount);
        Assert.AreEqual(1, permittedRemoteAddress.AbortCount);
    }

    [TestMethod]
    public void Add_WhenUnexpectedCreationErrorOccurs_AbortsTransactionAndPropagatesError()
    {
        TestPermittedRemoteAddress permittedRemoteAddress = new();
        permittedRemoteAddress.Add([EXISTING_ADDRESS], Action.HardPermit);
        permittedRemoteAddress.ThrowingAddress = NEW_ADDRESS_2;

        Assert.ThrowsExactly<InvalidOperationException>(
            () => permittedRemoteAddress.Add([NEW_ADDRESS_1, NEW_ADDRESS_2], Action.HardPermit));

        CollectionAssert.AreEquivalent(
            new[] { EXISTING_ADDRESS },
            permittedRemoteAddress.ActiveAddresses.ToArray());
        Assert.AreEqual(1, permittedRemoteAddress.CommitCount);
        Assert.AreEqual(1, permittedRemoteAddress.AbortCount);
    }

    private sealed class TestPermittedRemoteAddress : PermittedRemoteAddress
    {
        private readonly Dictionary<Guid, string> _addressByGuid = [];
        private readonly HashSet<Guid> _activeGuids = [];
        private readonly List<Guid> _createdGuids = [];
        private readonly List<Guid> _removedGuids = [];
        private bool _isTransactionActive;

        public string? FailingAddress { get; set; }
        public string? ThrowingAddress { get; set; }
        public int CommitCount { get; private set; }
        public int AbortCount { get; private set; }

        public IEnumerable<string> ActiveAddresses =>
            _activeGuids.Select(guid => _addressByGuid[guid]).Distinct(StringComparer.OrdinalIgnoreCase);

        public TestPermittedRemoteAddress()
            : base(null!, null!, null!)
        {
        }

        protected override void StartTransaction()
        {
            Assert.IsFalse(_isTransactionActive);
            _isTransactionActive = true;
            _createdGuids.Clear();
            _removedGuids.Clear();
        }

        protected override void CommitTransaction()
        {
            Assert.IsTrue(_isTransactionActive);
            _activeGuids.ExceptWith(_removedGuids);
            _activeGuids.UnionWith(_createdGuids);
            _isTransactionActive = false;
            CommitCount++;
        }

        protected override void AbortTransaction()
        {
            Assert.IsTrue(_isTransactionActive);
            _createdGuids.Clear();
            _removedGuids.Clear();
            _isTransactionActive = false;
            AbortCount++;
        }

        protected override bool TryCreateFilters(
            CoreNetworkAddress networkAddress,
            Action action,
            out List<Guid> guids)
        {
            Assert.IsTrue(_isTransactionActive);
            string address = networkAddress.ToString();

            if (string.Equals(address, ThrowingAddress, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("simulated WFP filter creation failure");
            }

            if (string.Equals(address, FailingAddress, StringComparison.OrdinalIgnoreCase))
            {
                guids = [];
                return false;
            }

            Guid guid = Guid.NewGuid();
            _addressByGuid[guid] = address;
            _createdGuids.Add(guid);
            guids = [guid];
            return true;
        }

        protected override void RemoveGuids(List<Guid> guids)
        {
            if (_isTransactionActive)
            {
                _removedGuids.AddRange(guids);
            }
            else
            {
                _activeGuids.ExceptWith(guids);
            }
        }
    }
}
