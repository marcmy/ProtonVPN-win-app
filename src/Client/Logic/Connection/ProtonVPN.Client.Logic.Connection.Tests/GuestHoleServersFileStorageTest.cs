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

using System.Reflection;
using ProtonVPN.Client.Logic.Connection.GuestHole;

namespace ProtonVPN.Client.Logic.Connection.Tests;

[TestClass]
public class GuestHoleServersFileStorageTest
{
    private const string EXPECTED_RUNTIME_VALUE = "00112233445566778899AABBCCDDEEFF";

    [TestMethod]
    public void GetBuildVariableValue_ShouldReadConstantFromRuntimeTypeMetadata()
    {
        MethodInfo? method = typeof(GuestHoleServersFileStorage).GetMethod(
            "GetBuildVariableValue",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);

        object? value = method.Invoke(null, [typeof(RuntimeBuildVariables), nameof(RuntimeBuildVariables.GuestHoleKey)]);

        Assert.AreEqual(EXPECTED_RUNTIME_VALUE, value);
    }

    private static class RuntimeBuildVariables
    {
        public const string GuestHoleKey = EXPECTED_RUNTIME_VALUE;
    }
}
