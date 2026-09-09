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

using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.ProcessCommunication.Service;

namespace ProtonVPN.Service.Tests.ProcessCommunication;

[TestClass]
public class GrpcServerPipeSecurityTest
{
    [TestMethod]
    public void CreatePipeSecurity_ShouldGrantAuthenticatedUsersOnlyClientRights()
    {
        PipeAccessRule accessRule = GetAccessRule(WellKnownSidType.AuthenticatedUserSid);

        accessRule.AccessControlType.Should().Be(AccessControlType.Allow);
        (accessRule.PipeAccessRights & PipeAccessRights.ReadWrite).Should().Be(PipeAccessRights.ReadWrite);
        (accessRule.PipeAccessRights & PipeAccessRights.CreateNewInstance).Should().Be(0);
    }

    [TestMethod]
    public void CreatePipeSecurity_ShouldReserveServerInstanceCreationForLocalSystem()
    {
        PipeAccessRule accessRule = GetAccessRule(WellKnownSidType.LocalSystemSid);

        accessRule.AccessControlType.Should().Be(AccessControlType.Allow);
        (accessRule.PipeAccessRights & PipeAccessRights.ReadWrite).Should().Be(PipeAccessRights.ReadWrite);
        (accessRule.PipeAccessRights & PipeAccessRights.CreateNewInstance).Should().Be(PipeAccessRights.CreateNewInstance);
    }

    private static PipeAccessRule GetAccessRule(WellKnownSidType sidType)
    {
        GrpcServer grpcServer = new(null!, null!, null!, null!, null!, null!, null!, null!);
        MethodInfo createPipeSecurityMethod = typeof(GrpcServer).GetMethod(
            "CreatePipeSecurity",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        PipeSecurity pipeSecurity = (PipeSecurity)createPipeSecurityMethod.Invoke(grpcServer, null)!;
        SecurityIdentifier sid = new(sidType, null);

        return pipeSecurity.GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .Single(rule => rule.IdentityReference.Equals(sid));
    }
}
