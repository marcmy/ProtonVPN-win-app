using System.IO;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtoBuf;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;

namespace ProtonVPN.Service.Tests.ServerHealth;

[TestClass]
public class ServerHealthProbeRequestTest
{
    [TestMethod]
    public void QuickMode_RoundTripsThroughTheActualGrpcSerializer()
    {
        using MemoryStream buffer = new();
        Serializer.Serialize(buffer, new ServerHealthProbeRequestIpcEntity
        { Address = "192.0.2.1", QuickFirstResponse = true });
        buffer.Position = 0;
        ServerHealthProbeRequestIpcEntity restored = Serializer.Deserialize<ServerHealthProbeRequestIpcEntity>(buffer);
        Assert.AreEqual("192.0.2.1", restored.Address);
        Assert.IsTrue(restored.QuickFirstResponse);
    }

    [TestMethod]
    public void LegacyRequest_DefaultsToNormalFourSampleMode()
    {
        using MemoryStream buffer = new();
        Serializer.Serialize(buffer, new LegacyRequest { Address = "192.0.2.2" });
        buffer.Position = 0;
        ServerHealthProbeRequestIpcEntity restored = Serializer.Deserialize<ServerHealthProbeRequestIpcEntity>(buffer);
        Assert.AreEqual("192.0.2.2", restored.Address);
        Assert.IsFalse(restored.QuickFirstResponse);
    }

    [TestMethod]
    public void LegacyReceiver_IgnoresOptionalQuickModeField()
    {
        using MemoryStream buffer = new();
        Serializer.Serialize(buffer, new ServerHealthProbeRequestIpcEntity
        { Address = "192.0.2.3", QuickFirstResponse = true });
        buffer.Position = 0;
        Assert.AreEqual("192.0.2.3", Serializer.Deserialize<LegacyRequest>(buffer).Address);
    }

    [DataContract]
    public class LegacyRequest
    {
        [DataMember(Order = 1, IsRequired = true)]
        public string Address { get; set; } = string.Empty;
    }
}
