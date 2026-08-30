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

#nullable enable annotations

using System.Diagnostics;
using System.Runtime.CompilerServices;
using ProtonVPN.Logging.Contracts;
using TaskExtensions = ProtonVPN.Common.Core.Extensions.TaskExtensions;

namespace ProtonVPN.Logging.Events;

public abstract class GlobalExceptionHandlerBase
{
    protected ILogger? Logger { get; private set; }

    public event Action<Exception>? OnFatalException;

    public void Initialize()
    {
        EventLogger.Initialize();
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        TaskExtensions.SetDefaultExceptionHandler(ex =>
            TryLogException("Fire-and-forget task exception", ex, isFatal: false));
    }

    public void SetLogger(ILogger logger)
    {
        Logger = logger;
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs eventArgs)
    {
        string terminatingText = eventArgs.IsTerminating ? "(Terminating)" : string.Empty;
        Exception exception = NormalizeExceptionObject(eventArgs.ExceptionObject);
        TryLogException($"AppDomain unhandled exception {terminatingText}", exception, eventArgs.IsTerminating);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
    {
        TryLogException("Unobserved task exception", eventArgs.Exception, isFatal: false);
        eventArgs.SetObserved();
    }

    protected void TryLogException(string handler, Exception? exception, bool isFatal)
    {
        if (exception is null)
        {
            return;
        }

        AggregateException? flattenedAggregate = exception is AggregateException aggregate
            ? aggregate.Flatten()
            : null;
        Exception diagnosticException = flattenedAggregate?.InnerExceptions.Count == 1
            ? flattenedAggregate.InnerExceptions[0]
            : exception;

        TryWriteEventLog(handler, exception, diagnosticException, flattenedAggregate);
        TryWriteFileLog(handler, diagnosticException, isFatal);

        if (isFatal)
        {
            TryInvokeOnFatalException(diagnosticException);
        }
    }

    private void TryInvokeOnFatalException(Exception exception)
    {
        try
        {
            OnFatalException?.Invoke(exception);
        }
        catch (Exception subscriberException)
        {
            TryLogException("OnFatalException subscriber threw", subscriberException, isFatal: false);
        }
    }

    private static void TryWriteEventLog(
        string handler,
        Exception exception,
        Exception diagnosticException,
        AggregateException? flattenedAggregate)
    {
        try
        {
            string flattenedDetails = flattenedAggregate is not null
                ? FormatAggregateException(flattenedAggregate)
                : string.Empty;

            string message =
                $"Proton VPN Windows error event log{Environment.NewLine}" +
                $"Date: {DateTimeOffset.UtcNow:o}{Environment.NewLine}" +
                $"Handler: {handler}{Environment.NewLine}" +
                Environment.NewLine +
                $"Exception HResult: 0x{diagnosticException.HResult:X8}{Environment.NewLine}" +
                $"Exception type: {diagnosticException.GetType().FullName}{Environment.NewLine}" +
                $"Exception message: {diagnosticException.Message}{Environment.NewLine}" +
                flattenedDetails +
                Environment.NewLine +
                $"Full exception: {exception}";

            EventLogger.Log(EventLogEntryType.Error, message);
        }
        catch
        {
        }
    }

    private void TryWriteFileLog(string handler, Exception exception, bool isFatal)
    {
        try
        {
            if (isFatal)
            {
                LogFatal(handler, exception);
            }
            else
            {
                LogError(handler, exception);
            }
        }
        catch
        {
        }
    }

    protected abstract void LogFatal(string handler, Exception exception);

    protected abstract void LogError(string handler, Exception exception);

    private static string FormatAggregateException(AggregateException flattened)
    {
        if (flattened.InnerExceptions.Count <= 1)
        {
            return string.Empty;
        }

        string details = string.Join(
            Environment.NewLine,
            flattened.InnerExceptions.Select((exception, index) =>
                $"  [{index + 1}] {exception.GetType().FullName}: {exception.Message}"));

        return $"Inner exceptions ({flattened.InnerExceptions.Count}):{Environment.NewLine}" +
               details + Environment.NewLine;
    }

    private static Exception NormalizeExceptionObject(object? exceptionObject)
    {
        return exceptionObject switch
        {
            RuntimeWrappedException wrappedException => new Exception(
                $"Non-Exception object thrown: " +
                $"{wrappedException.WrappedException?.GetType().FullName} — {wrappedException.WrappedException}",
                wrappedException),

            Exception exception => exception,

            object thrownObject => new Exception(
                $"Non-Exception object thrown: " +
                $"{thrownObject.GetType().FullName} — {thrownObject}"),

            null => new Exception("Non-Exception object thrown: <null>")
        };
    }
}
