/*
 * Copyright (c) 2026 Proton AG
 * This file is part of ProtonVPN.
 * ProtonVPN is free software: you can redistribute it and/or modify it under the terms of the
 * GNU General Public License as published by the Free Software Foundation, either version 3
 * of the License, or (at your option) any later version.
 * ProtonVPN is distributed WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See <https://www.gnu.org/licenses/>.
 */
using System.Runtime.Serialization;

namespace ProtonVPN.ProcessCommunication.Contracts.Entities.Settings;

[DataContract]
public class FolderScanStatusIpcEntity
{
    [DataMember(Order = 1)] public bool IsActive { get; set; }
    [DataMember(Order = 2)] public bool IsScanning { get; set; }
    [DataMember(Order = 3)] public long Entries { get; set; }
    [DataMember(Order = 4)] public int Executables { get; set; }
    [DataMember(Order = 5)] public string Error { get; set; } = string.Empty;
    [DataMember(Order = 6)] public string[] RulePaths { get; set; } = [];
}
