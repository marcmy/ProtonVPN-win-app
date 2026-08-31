/*
 * Copyright (c) 2025 Proton AG
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
using NSubstitute;
using ProtonVPN.Client.Common.UI.Exceptions;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.AppLogs;
using ProtonVPN.Logging.Events;

namespace ProtonVPN.Client.Common.UI.Tests.Exceptions;

[TestClass]
public class ClientGlobalExceptionHandlerTest
{
    [TestMethod]
    public void NonFatalException_ShouldUseAppLog()
    {
        ILogger logger = Substitute.For<ILogger>();
        ClientGlobalExceptionHandler handler = new();
        handler.SetLogger(logger);
        InvalidOperationException exception = new("nonfatal");

        InvokeTryLogException(handler, "client-nonfatal", exception, isFatal: false);

        logger.Received(1).Error<AppLog>(
            "client-nonfatal",
            exception,
            0,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>());
        logger.DidNotReceiveWithAnyArgs().Fatal<AppCrashLog>(default!, default);
    }

    [TestMethod]
    public void FatalException_ShouldUseAppCrashLog()
    {
        ILogger logger = Substitute.For<ILogger>();
        ClientGlobalExceptionHandler handler = new();
        handler.SetLogger(logger);
        InvalidOperationException exception = new("fatal");

        InvokeTryLogException(handler, "client-fatal", exception, isFatal: true);

        logger.Received(1).Fatal<AppCrashLog>(
            "client-fatal",
            exception,
            0,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>());
    }

    private static void InvokeTryLogException(
        GlobalExceptionHandlerBase handler,
        string handlerName,
        Exception exception,
        bool isFatal)
    {
        MethodInfo method = typeof(GlobalExceptionHandlerBase).GetMethod(
            "TryLogException",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(handler, [handlerName, exception, isFatal]);
    }
}
