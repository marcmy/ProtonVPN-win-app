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

using FluentAssertions;
using NSubstitute;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.AppServiceLogs;
using ProtonVPN.Logging.Events;
using ProtonVPN.Service.Start;

namespace ProtonVPN.Service.Tests.Start;

[TestClass]
public class GlobalExceptionHandlerTest
{
    [TestMethod]
    public void NonFatalException_ShouldUseNonFatalLogger_AndNotRaiseFatalEvent()
    {
        TestGlobalExceptionHandler handler = CreateTestHandler();
        InvalidOperationException exception = new("nonfatal");
        int fatalEventCount = 0;
        handler.OnFatalException += (_, _) => fatalEventCount++;

        handler.Log("nonfatal-handler", exception, isFatal: false);

        handler.NonFatalLogs.Should().ContainSingle();
        handler.NonFatalLogs[0].Handler.Should().Be("nonfatal-handler");
        handler.NonFatalLogs[0].Exception.Should().BeSameAs(exception);
        handler.FatalLogs.Should().BeEmpty();
        fatalEventCount.Should().Be(0);
    }

    [TestMethod]
    public void FatalException_ShouldUseFatalLogger_AndRaiseFatalEventOnce()
    {
        TestGlobalExceptionHandler handler = CreateTestHandler();
        InvalidOperationException exception = new("fatal");
        int fatalEventCount = 0;
        handler.OnFatalException += (_, _) => fatalEventCount++;

        handler.Log("fatal-handler", exception, isFatal: true);

        handler.FatalLogs.Should().ContainSingle();
        handler.FatalLogs[0].Handler.Should().Be("fatal-handler");
        handler.FatalLogs[0].Exception.Should().BeSameAs(exception);
        handler.NonFatalLogs.Should().BeEmpty();
        fatalEventCount.Should().Be(1);
    }

    [TestMethod]
    public void FatalEventException_ShouldBeContained_AndLoggedAsNonFatal()
    {
        TestGlobalExceptionHandler handler = CreateTestHandler();
        InvalidOperationException originalException = new("fatal");
        InvalidOperationException delegateException = new("cleanup failed");
        handler.OnFatalException += (_, _) => throw delegateException;

        Action action = () => handler.Log("fatal-handler", originalException, isFatal: true);

        action.Should().NotThrow();
        handler.FatalLogs.Should().ContainSingle();
        handler.NonFatalLogs.Should().ContainSingle();
        handler.NonFatalLogs[0].Handler.Should().StartWith(
            "Fatal exception delegate threw exception following 'fatal-handler' handler");
        handler.NonFatalLogs[0].Exception.Should().BeSameAs(delegateException);
    }

    [TestMethod]
    public void ServiceHandler_ShouldUseServiceCrashAndServiceLogEvents()
    {
        ILogger logger = Substitute.For<ILogger>();
        TestableServiceGlobalExceptionHandler handler = new();
        handler.SetLogger(logger);
        InvalidOperationException nonFatalException = new("nonfatal");
        InvalidOperationException fatalException = new("fatal");

        handler.Log("nonfatal-handler", nonFatalException, isFatal: false);
        handler.Log("fatal-handler", fatalException, isFatal: true);

        logger.Received(1).Error<AppServiceLog>(
            "nonfatal-handler",
            nonFatalException,
            0,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>());
        logger.Received(1).Fatal<AppServiceCrashLog>(
            "fatal-handler",
            fatalException,
            0,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>());
    }

    private static TestGlobalExceptionHandler CreateTestHandler()
    {
        TestGlobalExceptionHandler handler = new();
        handler.SetLogger(Substitute.For<ILogger>());
        return handler;
    }

    private sealed class TestGlobalExceptionHandler : GlobalExceptionHandlerBase
    {
        public List<LogCall> FatalLogs { get; } = [];
        public List<LogCall> NonFatalLogs { get; } = [];

        public void Log(string handler, Exception exception, bool isFatal)
        {
            TryLogException(handler, exception, isFatal);
        }

        protected override void LogFatalException(ILogger logger, string handler, Exception exception)
        {
            FatalLogs.Add(new LogCall(handler, exception));
        }

        protected override void LogNonFatalException(ILogger logger, string handler, Exception exception)
        {
            NonFatalLogs.Add(new LogCall(handler, exception));
        }
    }

    private sealed class TestableServiceGlobalExceptionHandler : ServiceGlobalExceptionHandler
    {
        public void Log(string handler, Exception exception, bool isFatal)
        {
            TryLogException(handler, exception, isFatal);
        }
    }

    private sealed record LogCall(string Handler, Exception Exception);
}
