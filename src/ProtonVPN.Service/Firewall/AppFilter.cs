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
using System.Linq;
using ProtonVPN.Logging.Contracts;
using ProtonVPN.Logging.Contracts.Events.SplitTunnelLogs;
using ProtonVPN.NetworkFilter;
using Action = ProtonVPN.NetworkFilter.Action;

namespace ProtonVPN.Service.Firewall;

public class AppFilter : IAppFilter
{
    private readonly ILogger _logger;
    private readonly IpFilter _ipFilter;
    private readonly IpLayer _ipLayer;
    private Dictionary<string, List<Guid>> _list = new(StringComparer.OrdinalIgnoreCase);

    public AppFilter(ILogger logger, IpFilter ipFilter, IpLayer ipLayer)
    {
        _logger = logger;
        _ipLayer = ipLayer;
        _ipFilter = ipFilter;
    }

    public void Add(string[] paths, Tuple<Layer, Action>[] filters)
    {
        foreach (string path in paths)
        {
            Add(path, filters);
        }
    }

    private void Add(string path, Tuple<Layer, Action>[] filters)
    {
        if (_list.ContainsKey(path))
        {
            return;
        }

        try
        {
            _list[path] = CreateFilters(path, filters);
        }
        catch (NonLocalAppPathException)
        {
            Remove(path);
            _logger.Warn<SplitTunnelLog>($"Skipped app filter for path {path} because it is not on a local drive.");
        }
        catch (InvalidArgumentException)
        {
            Remove(path);
            _logger.Error<SplitTunnelLog>($"Failed to create app filter for path {path} due to invalid argument.");
        }
        catch
        {
            // A path is installed only when all of its layer filters succeed.
            // Remove partial filters and the path entry so a later Apply can retry.
            Remove(path);
            throw;
        }
    }

    public void Replace(string[] paths, Tuple<Layer, Action>[] filters)
    {
        Dictionary<string, List<Guid>> previous = _list;
        StartTransaction();
        try
        {
            _list = new(StringComparer.OrdinalIgnoreCase);
            Add(paths, filters);
            foreach (List<Guid> guids in previous.Values) { RemoveGuids(guids); }
            CommitTransaction();
        }
        catch
        {
            try { AbortTransaction(); }
            finally { _list = previous; }
            throw;
        }
    }

    protected virtual List<Guid> CreateFilters(string path, Tuple<Layer, Action>[] filters)
    {
        List<Guid> created = [];
        try
        {
            foreach ((Layer layer, Action action) in filters)
            {
                _ipLayer.Apply(appliedLayer => created.Add(_ipFilter.DynamicSublayer.CreateAppFilter(
                    new DisplayData("ProtonVPN app filter", ""), action, appliedLayer, 14,
                    path, isDnsPortExcluded: action == Action.HardPermit)), [layer]);
            }
            return created;
        }
        catch
        {
            RemoveGuids(created);
            throw;
        }
    }

    protected virtual void StartTransaction() => _ipFilter.DynamicInstance.Session.StartTransaction();
    protected virtual void CommitTransaction() => _ipFilter.DynamicInstance.Session.CommitTransaction();
    protected virtual void AbortTransaction() => _ipFilter.DynamicInstance.Session.AbortTransaction();
    protected virtual void RemoveGuids(List<Guid> guids)
    {
        foreach (Guid guid in guids) { _ipFilter.DynamicSublayer.DestroyFilter(guid); }
    }

    public void Remove(string path)
    {
        if (!_list.TryGetValue(path, out List<Guid>? guids) || guids is null)
        {
            return;
        }

        RemoveGuids(guids);

        _list.Remove(path);
    }

    public void RemoveAll()
    {
        foreach (KeyValuePair<string, List<Guid>> element in _list.ToList())
        {
            Remove(element.Key);
        }
    }
}
