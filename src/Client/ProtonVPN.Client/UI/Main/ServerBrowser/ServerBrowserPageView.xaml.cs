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

using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ProtonVPN.Client.Core.Bases;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace ProtonVPN.Client.UI.Main.ServerBrowser;

public sealed partial class ServerBrowserPageView : IContextAware
{
    private const double WIDE_LAYOUT_WIDTH = 786;
    private const double EXTRA_COLUMN_WIDTH = 450;
    private const double DETAILS_WIDTH = 328;

    private readonly Dictionary<ListViewItem, ServerBrowserRow> _realized = [];
    private readonly DispatcherTimer _sortTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _expiryTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private ScrollViewer? _listScroll;
    private bool _isScrolling;
    private bool _pointerDown;
    private bool _returningToSearch;
    private bool _viewportUpdateQueued;
    private ListAnchor? _savedAnchor;
    private int _restoreSequence;

    public ServerBrowserPageViewModel ViewModel { get; }

    public ServerBrowserPageView()
    {
        ViewModel = App.GetService<ServerBrowserPageViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _sortTimer.Tick += OnSortTimerTick;
        _expiryTimer.Tick += (_, _) => ViewModel.RefreshExpiredMeasurements();
        ServerList.AddHandler(PointerPressedEvent, new PointerEventHandler(OnListPointerPressed), true);
        ServerList.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnListPointerReleased), true);
        ServerList.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnListPointerReleased), true);
        ServerList.AddHandler(PointerCanceledEvent, new PointerEventHandler(OnListPointerReleased), true);
        ServerList.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnListPointerWheelChanged), true);
    }

    public object GetContext() => ViewModel;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        ViewModel.ResultsChanging += OnResultsChanging;
        ViewModel.ResultsChanged += OnResultsChanged;
        ViewModel.DiscoveryRequested += OnDiscoveryRequested;
        ViewModel.LiveProjectionRequested += OnLiveProjectionRequested;
        ViewModel.StatusAnnouncementRequested += OnStatusAnnouncementRequested;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        BindScrollViewer();
        ViewModel.Activate();
        _expiryTimer.Start();
        UpdateLayoutForSelection();
        bool restoreFocus = _savedAnchor?.FocusedId is string id && ViewModel.FindShownRow(id) is not null;
        RestoreAnchor(_savedAnchor, restoreFocus, allowOriginalFocus: true);
        QueueViewportUpdate();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && (!restoreFocus || ListPane.Visibility == Visibility.Collapsed))
            {
                (ListPane.Visibility == Visibility.Visible ? (Control)ServerSearch : BackButton).Focus(FocusState.Programmatic);
            }
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _savedAnchor = CaptureAnchor() ?? _savedAnchor;
        _restoreSequence++;
        if (_listScroll is not null)
        {
            _listScroll.ViewChanged -= OnListViewChanged;
            _listScroll = null;
        }

        _sortTimer.Stop();
        _expiryTimer.Stop();
        _isScrolling = false;
        _pointerDown = false;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.StatusAnnouncementRequested -= OnStatusAnnouncementRequested;
        ViewModel.LiveProjectionRequested -= OnLiveProjectionRequested;
        ViewModel.DiscoveryRequested -= OnDiscoveryRequested;
        ViewModel.ResultsChanged -= OnResultsChanged;
        ViewModel.ResultsChanging -= OnResultsChanging;
        ViewModel.Deactivate();
    }

    private async void OnBackClicked(object sender, RoutedEventArgs args) => await ViewModel.BackAsync();

    private async void OnBackInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsLoaded)
        {
            args.Handled = true;
            await ViewModel.BackAsync();
        }
    }

    private async void OnFindInvoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsLoaded)
        {
            args.Handled = true;
            if (ListPane.Visibility == Visibility.Collapsed)
            {
                _returningToSearch = true;
                try
                {
                    await ViewModel.BackAsync();
                }
                finally
                {
                    _returningToSearch = false;
                }
            }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (IsLoaded)
                {
                    ServerSearch.Focus(FocusState.Programmatic);
                    ServerSearch.SelectAll();
                }
            });
        }
    }

    private void OnServerInvoked(object sender, ItemClickEventArgs args)
    {
        RecordInteraction();
        if (args.ClickedItem is ServerBrowserRow row)
        {
            ViewModel.Select(row);
        }
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs args)
    {
        RecordInteraction();
        if (args.Key is VirtualKey.Enter && FindFocusedRow() is ServerBrowserRow row)
        {
            args.Handled = true;
            ViewModel.Select(row);
        }
    }

    private void OnListPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        _pointerDown = true;
        RecordInteraction();
    }

    private void OnListPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        _pointerDown = false;
        RecordInteraction();
    }

    private void OnListPointerWheelChanged(object sender, PointerRoutedEventArgs args) => RecordInteraction();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(ViewModel.IsDetailsOpen))
        {
            return;
        }

        bool wasListVisible = ListPane.Visibility == Visibility.Visible;
        if (wasListVisible)
        {
            _savedAnchor = CaptureAnchor() ?? _savedAnchor;
        }
        UpdateLayoutForSelection();
        if (ListPane.Visibility == Visibility.Collapsed)
        {
            BackButton.Focus(FocusState.Programmatic);
        }
        else if (!wasListVisible)
        {
            RestoreAnchor(_savedAnchor, restoreFocus: !_returningToSearch, fallbackId: ViewModel.SelectedRow?.Id,
                allowOriginalFocus: true);
        }
        QueueViewportUpdate();
    }

    private void OnBrowserSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool wasListVisible = ListPane.Visibility == Visibility.Visible;
        if (wasListVisible)
        {
            _savedAnchor = CaptureAnchor() ?? _savedAnchor;
        }
        UpdateLayoutForSelection();
        if (!wasListVisible && ListPane.Visibility == Visibility.Visible)
        {
            RestoreAnchor(_savedAnchor, restoreFocus: false);
        }
        else if (ListPane.Visibility == Visibility.Collapsed && FindFocusedRow() is not null)
        {
            BackButton.Focus(FocusState.Programmatic);
        }
        QueueViewportUpdate();
    }

    private void UpdateLayoutForSelection()
    {
        bool showDetails = ViewModel.IsDetailsOpen;
        bool useSidePane = BrowserLayout.ActualWidth >= WIDE_LAYOUT_WIDTH;
        ListPane.Visibility = !showDetails || useSidePane ? Visibility.Visible : Visibility.Collapsed;
        DetailsPane.Visibility = showDetails ? Visibility.Visible : Visibility.Collapsed;
        DetailsColumn.Width = new GridLength(showDetails && useSidePane ? DETAILS_WIDTH : 0);
        Grid.SetColumn(DetailsPane, useSidePane ? 1 : 0);
        Grid.SetColumnSpan(DetailsPane, useSidePane ? 1 : 2);
        DetailsContent.Padding = useSidePane ? new Thickness(16, 8, 0, 8) : new Thickness(0, 8, 0, 8);
    }

    private void OnColumnsSizeChanged(object sender, SizeChangedEventArgs args)
    {
        Grid grid = (Grid)sender;
        bool showExtra = ListPane.ActualWidth >= EXTRA_COLUMN_WIDTH;
        grid.ColumnDefinitions[2].Width = new GridLength(showExtra ? 100 : 0);
        grid.ColumnDefinitions[3].Width = new GridLength(showExtra ? 72 : 0);
        foreach (FrameworkElement child in grid.Children.OfType<FrameworkElement>())
        {
            if (Grid.GetColumn(child) >= 2)
            {
                child.Visibility = showExtra ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer is ListViewItem container)
        {
            if (args.InRecycleQueue)
            {
                _realized.Remove(container);
            }
            else if (args.Item is ServerBrowserRow row)
            {
                _realized[container] = row;
            }
            QueueViewportUpdate();
        }
    }

    private void OnDiscoveryRequested(object? sender, EventArgs args)
    {
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (IsLoaded)
            {
                BindScrollViewer();
                ViewModel.StartDiscovery(
                    GetVisibleRows().OrderBy(item => item.Top).Select(item => item.Row).ToArray(),
                    ListPane.Visibility == Visibility.Visible);
            }
        });
    }

    private void BindScrollViewer()
    {
        if (_listScroll is null && FindScrollViewer(ServerList) is ScrollViewer scrollViewer)
        {
            _listScroll = scrollViewer;
            _listScroll.ViewChanged += OnListViewChanged;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject element)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(element, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }
            if (FindScrollViewer(child) is ScrollViewer descendant)
            {
                return descendant;
            }
        }
        return null;
    }

    private void OnListViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        _isScrolling = args.IsIntermediate;
        RecordInteraction();
        QueueViewportUpdate();
        if (!_isScrolling)
        {
            _savedAnchor = CaptureAnchor() ?? _savedAnchor;
        }
    }

    private void QueueViewportUpdate()
    {
        if (_viewportUpdateQueued)
        {
            return;
        }

        _viewportUpdateQueued = DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _viewportUpdateQueued = false;
            if (IsLoaded)
            {
                BindScrollViewer();
                ViewModel.SetVisibleRows(GetVisibleRows().Select(item => item.Row).ToArray());
            }
        });
    }

    private IEnumerable<(ServerBrowserRow Row, double Top)> GetVisibleRows()
    {
        if (_listScroll is null || ListPane.Visibility != Visibility.Visible)
        {
            yield break;
        }

        foreach ((ListViewItem container, ServerBrowserRow row) in _realized)
        {
            if (container.IsLoaded && container.ActualHeight > 0 && ServerList.ItemFromContainer(container) == row)
            {
                double top = container.TransformToVisual(_listScroll).TransformPoint(new Point(0, 0)).Y;
                if (top + container.ActualHeight > 0 && top < _listScroll.ViewportHeight)
                {
                    yield return (row, top);
                }
            }
        }
    }

    private ServerBrowserRow? FindFocusedRow()
    {
        if (XamlRoot is not XamlRoot root)
        {
            return null;
        }

        for (DependencyObject? element = FocusManager.GetFocusedElement(root) as DependencyObject;
             element is not null && element != ServerList;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is ListViewItem item)
            {
                return ServerList.ItemFromContainer(item) as ServerBrowserRow;
            }
        }
        return null;
    }

    private ListAnchor? CaptureAnchor()
    {
        if (ListPane.Visibility != Visibility.Visible)
        {
            return null;
        }

        var (row, top) = GetVisibleRows().OrderBy(item => item.Top).FirstOrDefault();
        return row is null ? null : new ListAnchor(row.Id, top, FindFocusedRow()?.Id);
    }

    private void OnResultsChanging(object? sender, EventArgs args)
    {
        _savedAnchor = CaptureAnchor() ?? _savedAnchor;
    }

    private void OnResultsChanged(object? sender, EventArgs args)
    {
        RestoreAnchor(_savedAnchor, restoreFocus: _savedAnchor?.FocusedId is not null);
        QueueViewportUpdate();
    }

    private void RestoreAnchor(ListAnchor? anchor, bool restoreFocus, string? fallbackId = null, bool allowOriginalFocus = false)
    {
        int sequence = ++_restoreSequence;
        object? originalFocus = allowOriginalFocus && XamlRoot is XamlRoot root ? FocusManager.GetFocusedElement(root) : null;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!CanRestoreAnchor(sequence))
            {
                return;
            }

            string? focusId = restoreFocus ? anchor?.FocusedId ?? fallbackId : null;
            ServerBrowserRow? focusRow = focusId is null ? null : ViewModel.FindShownRow(focusId);
            ServerBrowserRow? topRow = anchor is null ? null : ViewModel.FindShownRow(anchor.Id);
            topRow ??= focusRow;
            if (topRow is not null)
            {
                ServerList.ScrollIntoView(topRow, ScrollIntoViewAlignment.Leading);
            }
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (!CanRestoreAnchor(sequence))
                {
                    return;
                }

                if (topRow is not null && anchor?.Id == topRow.Id && _listScroll is ScrollViewer scroller
                    && ServerList.ContainerFromItem(topRow) is ListViewItem item)
                {
                    double top = item.TransformToVisual(scroller).TransformPoint(new Point(0, 0)).Y;
                    double offset = Math.Clamp(scroller.VerticalOffset + top - anchor.Top, 0, scroller.ScrollableHeight);
                    scroller.ChangeView(null, offset, null, disableAnimation: true);
                }

                if (!restoreFocus || !CanRestoreKeyboardFocus(originalFocus))
                {
                    return;
                }

                if (focusRow is not null && ServerList.ContainerFromItem(focusRow) is ListViewItem focused)
                {
                    focused.Focus(FocusState.Keyboard);
                }
                else if (focusRow is not null)
                {
                    // A live reorder can move the focused row beyond the realized viewport.
                    ServerList.ScrollIntoView(focusRow);
                    DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                    {
                        if (CanRestoreAnchor(sequence) && CanRestoreKeyboardFocus(originalFocus))
                        {
                            (ServerList.ContainerFromItem(focusRow) as Control ?? ServerList).Focus(FocusState.Keyboard);
                        }
                    });
                }
                else
                {
                    ServerList.Focus(FocusState.Keyboard);
                }
            });
        });
    }

    private bool CanRestoreAnchor(int sequence) =>
        IsLoaded && sequence == _restoreSequence && ListPane.Visibility == Visibility.Visible;

    private bool CanRestoreKeyboardFocus(object? originalFocus)
    {
        object? currentFocus = XamlRoot is XamlRoot root ? FocusManager.GetFocusedElement(root) : null;
        if (currentFocus is null || ReferenceEquals(currentFocus, originalFocus))
        {
            return true;
        }

        for (DependencyObject? element = currentFocus as DependencyObject;
             element is not null;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element == ServerList)
            {
                return true;
            }
        }
        return false;
    }

    private void OnLiveProjectionRequested(object? sender, EventArgs args)
    {
        if (!_sortTimer.IsEnabled)
        {
            _sortTimer.Start();
        }
    }

    private void RecordInteraction()
    {
        _sortTimer.Stop();
        _sortTimer.Start();
    }

    private void OnSortTimerTick(object? sender, object args)
    {
        _sortTimer.Stop();
        if (!_isScrolling && !_pointerDown)
        {
            ViewModel.ApplyPendingProjection();
        }
    }

    private void OnStatusAnnouncementRequested(object? sender, string text)
    {
        FrameworkElementAutomationPeer.CreatePeerForElement(ResultsStatus)?.RaiseNotificationEvent(
            AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, text, "ServerBrowserResults");
    }

    private sealed record ListAnchor(string Id, double Top, string? FocusedId);
}
