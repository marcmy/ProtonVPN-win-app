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
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.SplitTunnelLogs;

namespace ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

public sealed class SplitTunnelDomainPoller : ISplitTunnelDomainPoller, IDisposable
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(15);

    private readonly ISystemDnsCacheReader _dnsCacheReader;
    private readonly DomainResolvedAddressTracker _tracker;
    private readonly ILogger _logger;
    private readonly object _sync = new();

    private DomainRule[] _rules = [];
    private CancellationTokenSource? _pollingCancellation;
    private string[] _lastPublishedAddresses = [];
    private int _generation;

    public event EventHandler<string[]>? AddressesChanged;

    public SplitTunnelDomainPoller(
        ISystemDnsCacheReader dnsCacheReader,
        ILogger logger)
        : this(dnsCacheReader, new DomainResolvedAddressTracker(), logger)
    {
    }

    internal SplitTunnelDomainPoller(ISystemDnsCacheReader dnsCacheReader)
        : this(dnsCacheReader, new DomainResolvedAddressTracker(), new NullLogger())
    {
    }

    internal SplitTunnelDomainPoller(
        ISystemDnsCacheReader dnsCacheReader,
        DomainResolvedAddressTracker tracker)
        : this(dnsCacheReader, tracker, new NullLogger())
    {
    }

    private SplitTunnelDomainPoller(
        ISystemDnsCacheReader dnsCacheReader,
        DomainResolvedAddressTracker tracker,
        ILogger logger)
    {
        _dnsCacheReader = dnsCacheReader;
        _tracker = tracker;
        _logger = logger;
    }

    public void ReplaceRules(string[] rawRules)
    {
        DomainRule[] rules = (rawRules ?? [])
            .Select(rawRule => DomainRule.TryCreate(rawRule, out DomainRule? rule) ? rule : null)
            .Where(rule => rule is not null)
            .Select(rule => rule!)
            .Distinct()
            .OrderBy(rule => rule.Domain, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] activeAddresses;
        lock (_sync)
        {
            _rules = rules;
            _generation++;
            _tracker.RetainOwners(rules.Select(rule => rule.Domain));
            activeAddresses = _tracker.GetActiveIpv4Addresses();
        }

        PublishIfChanged(activeAddresses);
        if (rules.Length == 0)
        {
            StopPollingLoop(clearRules: false);
        }
    }

    public void Start()
    {
        EnsurePollingStarted();
    }

    public async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        DomainRule[] rules;
        int generation;
        lock (_sync)
        {
            rules = _rules;
            generation = _generation;
        }

        if (rules.Length == 0)
        {
            PublishIfChanged([]);
            return;
        }

        IReadOnlyCollection<SystemDnsCacheEntry> entries =
            await _dnsCacheReader.ReadAsync(cancellationToken);

        string[] activeAddresses;
        lock (_sync)
        {
            if (generation != _generation)
            {
                return;
            }

            foreach (SystemDnsCacheEntry entry in entries)
            {
                if (entry.IpAddress.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                foreach (DomainRule matchingRule in rules.Where(rule => rule.IsMatch(entry.Hostname)))
                {
                    _tracker.AddOrRefresh(
                        matchingRule.Domain,
                        entry.IpAddress,
                        entry.TimeToLiveSeconds);
                }
            }

            activeAddresses = _tracker.GetActiveIpv4Addresses();
        }

        PublishIfChanged(activeAddresses);
    }

    public void Stop()
    {
        StopPollingLoop(clearRules: true);
    }

    private void EnsurePollingStarted()
    {
        CancellationToken cancellationToken;
        lock (_sync)
        {
            if (_pollingCancellation is not null || _rules.Length == 0)
            {
                return;
            }

            _pollingCancellation = new CancellationTokenSource();
            cancellationToken = _pollingCancellation.Token;
        }

        _ = Task.Run(() => PollLoopAsync(cancellationToken));
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await PollOnceAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.Warn<SplitTunnelLog>(
                        "Failed to apply a domain split tunneling DNS-cache update.",
                        exception);
                }

                await Task.Delay(_pollInterval, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void StopPollingLoop(bool clearRules)
    {
        CancellationTokenSource? cancellation;
        string[] activeAddresses;
        lock (_sync)
        {
            cancellation = _pollingCancellation;
            _pollingCancellation = null;
            _generation++;

            if (clearRules)
            {
                _rules = [];
                _tracker.Clear();
            }

            activeAddresses = _tracker.GetActiveIpv4Addresses();
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
        PublishIfChanged(activeAddresses);
    }

    private void PublishIfChanged(string[] addresses)
    {
        string[] normalized = addresses
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(address => address, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        EventHandler<string[]>? handler;

        lock (_sync)
        {
            if (_lastPublishedAddresses.SequenceEqual(normalized, StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            _lastPublishedAddresses = normalized;
            handler = AddressesChanged;
        }

        try
        {
            handler?.Invoke(this, normalized);
        }
        catch
        {
            lock (_sync)
            {
                if (_lastPublishedAddresses.SequenceEqual(
                        normalized,
                        StringComparer.OrdinalIgnoreCase))
                {
                    _lastPublishedAddresses = [];
                }
            }

            throw;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
