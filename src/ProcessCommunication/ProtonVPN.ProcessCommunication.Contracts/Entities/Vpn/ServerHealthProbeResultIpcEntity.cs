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

#nullable enable

using System.Runtime.Serialization;

namespace ProtonVPN.ProcessCommunication.Contracts.Entities.Vpn;

[DataContract]
public class ServerHealthProbeResultIpcEntity
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

    [DataMember(Order = 7, IsRequired = false)]
    public string? Error { get; set; }

    // The wrapper distinguishes a legacy result from a current result with no sends.
    [DataMember(Name = nameof(Samples), Order = 8, EmitDefaultValue = false)]
    private SampleCollection? SampleData { get; set; }

    public ServerHealthProbeSampleIpcEntity[]? Samples
    {
        get => SampleData is { } data ? data.Items ?? [] : null;
        set => SampleData = value is null ? null : new() { Items = value };
    }

    [DataMember(Order = 9, EmitDefaultValue = false)]
    public string? RouteContext { get; set; }

    [DataContract]
    private sealed class SampleCollection
    {
        [DataMember(Order = 1)]
        public ServerHealthProbeSampleIpcEntity[]? Items { get; set; }
    }
}
