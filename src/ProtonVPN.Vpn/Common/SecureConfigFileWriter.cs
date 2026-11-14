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
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN. If not, see <https://www.gnu.org/licenses/>.
 */


using System.IO;
using System.Security.AccessControl;

namespace ProtonVPN.Vpn.Common;

public class SecureConfigFileWriter : ISecureConfigFileWriter
{
    // SYSTEM: full control; Administrators: delete only; no inherited access.
    private const string SECURE_SDDL = "O:SYG:SYD:P(A;;FA;;;SY)(A;;SD;;;BA)";

    public void Write(string path, string content)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        using FileStream stream = new FileInfo(path).Create(FileMode.CreateNew,
            FileSystemRights.WriteData, FileShare.None, 4096, FileOptions.None, CreateFileSecurity());
        using StreamWriter writer = new(stream);
        writer.Write(content);
    }

    internal static FileSecurity CreateFileSecurity()
    {
        FileSecurity security = new();
        security.SetSecurityDescriptorSddlForm(SECURE_SDDL);
        return security;
    }
}
