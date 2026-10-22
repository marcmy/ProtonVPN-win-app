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


using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Vpn.Common;

namespace ProtonVPN.Vpn.Tests.Common;

[TestClass]
public class SecureConfigFileWriterTest
{
    [TestMethod]
    public void Descriptor_ProtectsSecretsWithSystemOnlyRead_WithoutInheritedAccess()
    {
        // Parse the production descriptor only: no file, key, elevation, or live service is touched.
        FileSecurity security = SecureConfigFileWriter.CreateFileSecurity();
        Assert.IsTrue(security.AreAccessRulesProtected);
        Assert.AreEqual(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), security.GetOwner(typeof(SecurityIdentifier)));
        FileSystemAccessRule[] rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        Assert.AreEqual(2, rules.Length);
        Assert.IsTrue(rules.All(rule => !rule.IsInherited && rule.AccessControlType == AccessControlType.Allow));
        Assert.AreEqual(FileSystemRights.FullControl,
            rules.Single(rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null))).FileSystemRights);
        Assert.AreEqual(FileSystemRights.Delete,
            rules.Single(rule => rule.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null))).FileSystemRights);
    }
}
