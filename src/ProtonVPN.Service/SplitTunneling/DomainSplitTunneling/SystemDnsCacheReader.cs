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
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.SplitTunnelLogs;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public sealed class SystemDnsCacheReader : ISystemDnsCacheReader
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);
    private const string DNS_CACHE_COMMAND =
        "Get-DnsClientCache | " +
        "Where-Object { ($_.Type -eq 1 -or $_.Type -eq 28) -and $_.Status -eq 0 -and $_.Data } | " +
        "Select-Object Entry,Data,TimeToLive | ConvertTo-Json -Compress";

    private readonly ILogger _logger;

    public SystemDnsCacheReader(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyCollection<SystemDnsCacheEntry>> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            string json = await RunPowerShellAsync(cancellationToken);
            return SystemDnsCacheParser.Parse(json);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.Warn<SplitTunnelLog>(
                "Failed to read the Windows DNS client cache for domain split tunneling.",
                exception);
            return [];
        }
    }

    private static async Task<string> RunPowerShellAsync(CancellationToken cancellationToken)
    {
        string powershellPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        ProcessStartInfo startInfo = new()
        {
            FileName = powershellPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(DNS_CACHE_COMMAND);

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start Windows PowerShell.");
        }

        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("Timed out while reading the Windows DNS client cache.");
        }
        catch
        {
            TryKill(process);
            throw;
        }

        string output = await outputTask;
        string error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Get-DnsClientCache failed with exit code {process.ExitCode}. Error: {error}");
        }

        return output;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
