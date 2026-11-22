#nullable enable

using System;
using System.IO;
using System.Runtime.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtoBuf;
using ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;

namespace ProtonVPN.Service.Tests.ServerHealth;

[TestClass]
public class ServerHealthProbeResultTest
{
    [TestMethod]
    public void LegacyResponse_LeavesSamplesAndRouteContextAbsent()
    {
        using MemoryStream buffer = new();
        Serializer.Serialize(buffer, new LegacyResult { AverageLatencyMilliseconds = 31, UsedPhysicalRoute = true });
        buffer.Position = 0;

        ServerHealthProbeResultIpcEntity result = Serializer.Deserialize<ServerHealthProbeResultIpcEntity>(buffer);

        Assert.AreEqual(31d, result.AverageLatencyMilliseconds);
        Assert.IsTrue(result.UsedPhysicalRoute);
        Assert.IsNull(result.Samples);
        Assert.IsNull(result.RouteContext);
    }

    [TestMethod]
    public void CurrentResponse_PreservesEmptySamplesThroughGrpcSerializer()
    {
        using MemoryStream buffer = new();
        Serializer.Serialize(buffer, new ServerHealthProbeResultIpcEntity { Samples = [] });
        buffer.Position = 0;

        ServerHealthProbeResultIpcEntity result = Serializer.Deserialize<ServerHealthProbeResultIpcEntity>(buffer);

        Assert.IsNotNull(result.Samples);
        Assert.AreEqual(0, result.Samples.Length);
    }

    [TestMethod]
    public void CurrentResponse_PreservesTypedSamplesAndLegacyLatency()
    {
        DateTime sentAt = new(2026, 11, 22, 12, 30, 00, DateTimeKind.Utc);
        using MemoryStream buffer = new();
        Serializer.Serialize(buffer, new ServerHealthProbeResultIpcEntity
        {
            AverageLatencyMilliseconds = 31,
            UsedPhysicalRoute = true,
            RouteContext = "route-id",
            Samples =
            [
                new() { Outcome = ServerHealthProbeOutcome.Reply, RoundTripTimeMilliseconds = 31, SentAtUtc = sentAt },
                new() { Outcome = ServerHealthProbeOutcome.Unavailable, SentAtUtc = sentAt.AddMilliseconds(100) },
            ],
        });
        buffer.Position = 0;

        ServerHealthProbeResultIpcEntity result = Serializer.Deserialize<ServerHealthProbeResultIpcEntity>(buffer);

        Assert.AreEqual("route-id", result.RouteContext);
        Assert.AreEqual(2, result.Samples!.Length);
        Assert.AreEqual(ServerHealthProbeOutcome.Reply, result.Samples[0].Outcome);
        Assert.AreEqual(31d, result.Samples[0].RoundTripTimeMilliseconds);
        Assert.AreEqual(sentAt, result.Samples[0].SentAtUtc);
        Assert.AreEqual(ServerHealthProbeOutcome.Unavailable, result.Samples[1].Outcome);
        Assert.IsNull(result.Samples[1].RoundTripTimeMilliseconds);

        buffer.Position = 0;
        LegacyResult legacyResult = Serializer.Deserialize<LegacyResult>(buffer);
        Assert.AreEqual(31d, legacyResult.AverageLatencyMilliseconds);
        Assert.IsTrue(legacyResult.UsedPhysicalRoute);
    }

    [DataContract]
    public class LegacyResult
    {
        [DataMember(Order = 1, IsRequired = true)]
        public double? AverageLatencyMilliseconds { get; set; }

        [DataMember(Order = 2, IsRequired = true)]
        public double PacketLossPercent { get; set; }

        [DataMember(Order = 3, IsRequired = true)]
        public int SuccessfulSamples { get; set; }

        [DataMember(Order = 4, IsRequired = true)]
        public int TotalSamples { get; set; }

        [DataMember(Order = 5, IsRequired = true)]
        public DateTime CheckedAtUtc { get; set; }

        [DataMember(Order = 6, IsRequired = true)]
        public bool UsedPhysicalRoute { get; set; }

        [DataMember(Order = 7)]
        public string? Error { get; set; }
    }
}
