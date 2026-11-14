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

using System;
using System.Linq;
using Autofac;
using Autofac.Core;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Vpn.Config;
using ProtonVPN.Vpn.SplitTunnel;

namespace ProtonVPN.Vpn.Tests.Config;

[TestClass]
public class VpnModuleTest
{
    [TestMethod]
    public void Load_RegistersSplitTunnelRoutingOnlyThroughItsServiceContract()
    {
        ContainerBuilder builder = new();
        new ProtonVPN.Vpn.Config.Module().Load(builder);
        using IContainer container = builder.Build();

        IComponentRegistration registration = container.ComponentRegistry.Registrations
            .Single(candidate => candidate.Activator.LimitType == typeof(SplitTunnelRouting));
        Type[] registeredServices = registration.Services
            .OfType<TypedService>()
            .Select(service => service.ServiceType)
            .ToArray();

        registeredServices.Should().Contain(typeof(ISplitTunnelRouting));
        registeredServices.Should().NotContain(typeof(IDisposable));
    }
}
