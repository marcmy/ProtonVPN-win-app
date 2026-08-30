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
    private ILogger? _logger;

    public event EventHandler? OnFatalException;

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
        _logger = logger;
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
        TryWriteEventLog(handler, exception);
        TryWriteFileLog(handler, exception, isFatal);

        if (isFatal)
        {
            TryInvokeFatalEvent(handler, exception);
        }
    }

    private void TryWriteFileLog(string handler, Exception? exception, bool isFatal)
    {
        try
        {
            if (_logger is null)
            {
                return;
            }

            if (isFatal)
            {
                LogFatalException(_logger, handler, exception);
            }
            else
            {
                LogNonFatalException(_logger, handler, exception);
            }
        }
        catch (Exception loggingException)
        {
            TryWriteEventLog("File logging exception", loggingException);
        }
    }

    private void TryInvokeFatalEvent(string handler, Exception? exception)
    {
        try
        {
            OnFatalException?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception fatalDelegateException)
        {
            TryLogException(
                $"Fatal exception delegate threw exception following '{handler}' handler ({exception?.Message})",
                fatalDelegateException,
                isFatal: false);
        }
    }

    protected abstract void LogFatalException(ILogger logger, string handler, Exception? exception);

    protected abstract void LogNonFatalException(ILogger logger, string handler, Exception? exception);

    public static void TryWriteEventLog(string handler, Exception? exception)
    {
        try
        {
            if (exception is null)
            {
                return;
            }

            Exception diagnosticException = exception is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Count == 1
                ? aggregate.Flatten().InnerExceptions[0]
                : exception;

            string flattenedDetails = exception is AggregateException aggregateException
                ? FormatAggregateException(aggregateException)
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

    private static string FormatAggregateException(AggregateException aggregate)
    {
        AggregateException flattened = aggregate.Flatten();
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
