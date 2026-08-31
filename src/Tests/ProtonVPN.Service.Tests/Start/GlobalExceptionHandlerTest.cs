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

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Common.Legacy.OS.Processes;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.AppServiceLogs;
using ProtonVPN.Logging.Events;
using ProtonVPN.Service.Start;
using ProtonVPN.Service.StateMachine;
using ProtonVPN.Vpn.OpenVpn;

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
        handler.OnFatalException += _ => fatalEventCount++;

        handler.Log("nonfatal-handler", exception, isFatal: false);

        handler.ErrorLogs.Should().ContainSingle();
        handler.ErrorLogs[0].Handler.Should().Be("nonfatal-handler");
        handler.ErrorLogs[0].Exception.Should().BeSameAs(exception);
        handler.FatalLogs.Should().BeEmpty();
        fatalEventCount.Should().Be(0);
    }

    [TestMethod]
    public void FatalException_ShouldUseFatalLogger_AndRaiseFatalEventOnce()
    {
        TestGlobalExceptionHandler handler = CreateTestHandler();
        InvalidOperationException exception = new("fatal");
        Exception receivedFatalException = null!;
        int fatalEventCount = 0;
        handler.OnFatalException += ex =>
        {
            receivedFatalException = ex;
            fatalEventCount++;
        };

        handler.Log("fatal-handler", exception, isFatal: true);

        handler.FatalLogs.Should().ContainSingle();
        handler.FatalLogs[0].Handler.Should().Be("fatal-handler");
        handler.FatalLogs[0].Exception.Should().BeSameAs(exception);
        handler.ErrorLogs.Should().BeEmpty();
        fatalEventCount.Should().Be(1);
        receivedFatalException.Should().BeSameAs(exception);
    }

    [TestMethod]
    public void SingleInnerAggregate_ShouldUseInnerExceptionForFileLogAndFatalCallback()
    {
        TestGlobalExceptionHandler handler = CreateTestHandler();
        InvalidOperationException innerException = new("inner");
        AggregateException aggregateException = new(innerException);
        Exception receivedFatalException = null!;
        handler.OnFatalException += ex => receivedFatalException = ex;

        handler.Log("aggregate-handler", aggregateException, isFatal: true);

        handler.FatalLogs.Should().ContainSingle();
        handler.FatalLogs[0].Exception.Should().BeSameAs(innerException);
        receivedFatalException.Should().BeSameAs(innerException);
    }

    [TestMethod]
    public void FatalEventException_ShouldBeContained_AndLoggedAsNonFatal()
    {
        TestGlobalExceptionHandler handler = CreateTestHandler();
        InvalidOperationException originalException = new("fatal");
        InvalidOperationException delegateException = new("cleanup failed");
        handler.OnFatalException += _ => throw delegateException;

        Action action = () => handler.Log("fatal-handler", originalException, isFatal: true);

        action.Should().NotThrow();
        handler.FatalLogs.Should().ContainSingle();
        handler.ErrorLogs.Should().ContainSingle();
        handler.ErrorLogs[0].Handler.Should().Be("OnFatalException subscriber threw");
        handler.ErrorLogs[0].Exception.Should().BeSameAs(delegateException);
    }

    [TestMethod]
    public void AppDomainNonTerminatingException_ShouldBeNonFatal()
    {
        TestGlobalExceptionHandler handler = CreateTestHandler();
        InvalidOperationException exception = new("nonterminating");
        int fatalEventCount = 0;
        handler.OnFatalException += _ => fatalEventCount++;
        UnhandledExceptionEventArgs eventArgs = new(exception, isTerminating: false);

        InvokePrivateHandler(handler, "OnAppDomainUnhandledException", eventArgs);

        handler.ErrorLogs.Should().ContainSingle();
        handler.ErrorLogs[0].Handler.Should().StartWith("AppDomain unhandled exception");
        handler.ErrorLogs[0].Exception.Should().BeSameAs(exception);
        handler.FatalLogs.Should().BeEmpty();
        fatalEventCount.Should().Be(0);
    }

    [TestMethod]
    public void AppDomainTerminatingException_ShouldBeFatal()
    {
        TestGlobalExceptionHandler handler = CreateTestHandler();
        InvalidOperationException exception = new("terminating");
        Exception receivedFatalException = null!;
        handler.OnFatalException += ex => receivedFatalException = ex;
        UnhandledExceptionEventArgs eventArgs = new(exception, isTerminating: true);

        InvokePrivateHandler(handler, "OnAppDomainUnhandledException", eventArgs);

        handler.FatalLogs.Should().ContainSingle();
        handler.FatalLogs[0].Handler.Should().Contain("(Terminating)");
        handler.FatalLogs[0].Exception.Should().BeSameAs(exception);
        receivedFatalException.Should().BeSameAs(exception);
    }

    [TestMethod]
    public void UnobservedTaskException_ShouldBeObserved_AndLoggedAsNonFatal()
    {
        TestGlobalExceptionHandler handler = CreateTestHandler();
        InvalidOperationException exception = new("unobserved");
        UnobservedTaskExceptionEventArgs eventArgs = new(new AggregateException(exception));

        InvokePrivateHandler(handler, "OnUnobservedTaskException", eventArgs);

        eventArgs.Observed.Should().BeTrue();
        handler.ErrorLogs.Should().ContainSingle();
        handler.ErrorLogs[0].Handler.Should().Be("Unobserved task exception");
        handler.ErrorLogs[0].Exception.Should().BeSameAs(exception);
        handler.FatalLogs.Should().BeEmpty();
    }

    [TestMethod]
    public void ServiceHandler_ShouldUseServiceCrashAndServiceLogEvents()
    {
        ILogger logger = Substitute.For<ILogger>();
        ServiceGlobalExceptionHandler handler = new();
        handler.SetLogger(logger);
        InvalidOperationException nonFatalException = new("nonfatal");
        InvalidOperationException fatalException = new("fatal");

        InvokeTryLogException(handler, "nonfatal-handler", nonFatalException, isFatal: false);
        InvokeTryLogException(handler, "fatal-handler", fatalException, isFatal: true);

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

    [TestMethod]
    public void FatalServiceCleanup_ShouldContinue_WhenOneCleanupActionThrows()
    {
        ILogger logger = Substitute.For<ILogger>();
        IVpnConnectionStateMachine stateMachine = Substitute.For<IVpnConnectionStateMachine>();
        IOpenVpnProcess openVpnProcess = Substitute.For<IOpenVpnProcess>();
        IOsProcesses osProcesses = Substitute.For<IOsProcesses>();
        IStaticConfiguration configuration = Substitute.For<IStaticConfiguration>();
        configuration.ClientName.Returns("ProtonVPN.Client");
        stateMachine.When(x => x.Disconnect()).Do(_ => throw new InvalidOperationException("disconnect failed"));

        ContainerBuilder containerBuilder = new();
        containerBuilder.RegisterInstance(logger).As<ILogger>();
        containerBuilder.RegisterInstance(stateMachine).As<IVpnConnectionStateMachine>();
        containerBuilder.RegisterInstance(openVpnProcess).As<IOpenVpnProcess>();
        containerBuilder.RegisterInstance(osProcesses).As<IOsProcesses>();
        containerBuilder.RegisterInstance(configuration).As<IStaticConfiguration>();
        using IContainer container = containerBuilder.Build();

        Bootstrapper bootstrapper = (Bootstrapper)RuntimeHelpers.GetUninitializedObject(typeof(Bootstrapper));
        typeof(Bootstrapper).GetField("_container", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(bootstrapper, container);
        MethodInfo onFatalException = typeof(Bootstrapper).GetMethod(
            "OnFatalException",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        Action action = () => onFatalException.Invoke(bootstrapper, [new InvalidOperationException("fatal")]);

        action.Should().NotThrow();
        openVpnProcess.Received(1).Stop();
        osProcesses.Received(1).KillProcesses("ProtonVPN.Client");
    }

    private static void InvokePrivateHandler(
        GlobalExceptionHandlerBase handler,
        string methodName,
        object eventArgs)
    {
        MethodInfo method = typeof(GlobalExceptionHandlerBase).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(handler, new object[] { null!, eventArgs });
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

    private static TestGlobalExceptionHandler CreateTestHandler()
    {
        TestGlobalExceptionHandler handler = new();
        handler.SetLogger(Substitute.For<ILogger>());
        return handler;
    }

    private sealed class TestGlobalExceptionHandler : GlobalExceptionHandlerBase
    {
        public List<LogCall> FatalLogs { get; } = [];
        public List<LogCall> ErrorLogs { get; } = [];

        public void Log(string handler, Exception exception, bool isFatal)
        {
            TryLogException(handler, exception, isFatal);
        }

        protected override void LogFatal(string handler, Exception exception)
        {
            FatalLogs.Add(new LogCall(handler, exception));
        }

        protected override void LogError(string handler, Exception exception)
        {
            ErrorLogs.Add(new LogCall(handler, exception));
        }
    }

    private sealed record LogCall(string Handler, Exception Exception);
}
