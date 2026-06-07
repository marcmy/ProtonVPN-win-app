/*
 * Copyright (c) 2024 Proton AG
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

using CommunityToolkit.Mvvm.Input;
using ProtonVPN.Client.Commands;
using ProtonVPN.Client.Core.Bases;
using ProtonVPN.Client.Core.Bases.ViewModels;
using ProtonVPN.Client.Logic.Updates.Contracts;

namespace ProtonVPN.Client.UI.Update;

public partial class UpdateViewModel : ViewModelBase
{
    private readonly IUpdatesManager _updatesManager;

    public IAsyncRelayCommand UpdateCommand { get; }

    public IRelayCommand SkipUpdateCommand { get; }

    public bool CanSkipUpdate => _updatesManager.CanSkipCurrentUpdate;

    public UpdateViewModel(
        IUpdateClientCommand updateClientCommand,
        IUpdatesManager updatesManager,
        IViewModelHelper viewModelHelper)
        : base(viewModelHelper)
    {
        _updatesManager = updatesManager;

        UpdateCommand = updateClientCommand.Command;
        SkipUpdateCommand = new RelayCommand(SkipUpdate, () => CanSkipUpdate);
    }

    public void InvalidateUpdateCommands()
    {
        OnPropertyChanged(nameof(CanSkipUpdate));
        SkipUpdateCommand.NotifyCanExecuteChanged();
    }

    private void SkipUpdate()
    {
        _updatesManager.SkipCurrentUpdate();
        InvalidateUpdateCommands();
    }
}
