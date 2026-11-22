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
    private bool _syncingFilters;
    private double _listOffsetBeforeDetails;
    private int _scrollRequest;

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
        ViewModel.ResultsChanged += OnResultsChanged;
        ViewModel.FiltersChanged += OnFiltersChanged;
        ViewModel.DiscoveryRequested += OnDiscoveryRequested;
        ViewModel.LiveProjectionRequested += OnLiveProjectionRequested;
        ViewModel.StatusAnnouncementRequested += OnStatusAnnouncementRequested;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        BindScrollViewer();
        ViewModel.Activate();
        SyncFilterControls(refreshOptions: true);
        _expiryTimer.Start();
        UpdateLayoutForSelection();
        QueueViewportUpdate();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
            {
                (ListPane.Visibility == Visibility.Visible ? (Control)ServerSearch : BackButton).Focus(FocusState.Programmatic);
            }
        });
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _scrollRequest++;
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
        ViewModel.FiltersChanged -= OnFiltersChanged;
        ViewModel.ResultsChanged -= OnResultsChanged;
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
        _scrollRequest++;
        RecordInteraction();
    }

    private void OnListPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        _pointerDown = false;
        RecordInteraction();
    }

    private void OnListPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        _scrollRequest++;
        RecordInteraction();
    }

    private void OnListLostFocus(object sender, RoutedEventArgs args) => RecordInteraction();

    private void OnFilterDropDownClosed(object sender, object args) => RecordInteraction();

    private void OnFilterSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingFilters || !IsLoaded || !ViewModel.IsActive)
        {
            return;
        }

        if (ReferenceEquals(sender, FeatureTabs) && FeatureTabs.SelectedIndex >= 0)
        {
            ViewModel.FeatureIndex = FeatureTabs.SelectedIndex;
        }
        else if (ReferenceEquals(sender, SortFilter) && SortFilter.SelectedIndex >= 0)
        {
            ViewModel.SortIndex = SortFilter.SelectedIndex;
        }
        else if (ReferenceEquals(sender, PingFilter) && PingFilter.SelectedIndex >= 0)
        {
            ViewModel.PingLimitIndex = PingFilter.SelectedIndex;
        }
    }

    private void SyncFilterControls(bool refreshOptions = false)
    {
        _syncingFilters = true;
        try
        {
            if (refreshOptions)
            {
                SortFilter.ItemsSource = ViewModel.SortOptions;
                PingFilter.ItemsSource = ViewModel.PingOptions;
            }
            FeatureTabs.SelectedIndex = ViewModel.FeatureIndex;
            SortFilter.SelectedIndex = ViewModel.SortIndex;
            PingFilter.SelectedIndex = ViewModel.PingLimitIndex;
        }
        finally
        {
            _syncingFilters = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ViewModel.SortOptions) or nameof(ViewModel.PingOptions))
        {
            SyncFilterControls(refreshOptions: true);
        }
        if (args.PropertyName != nameof(ViewModel.IsDetailsOpen))
        {
            return;
        }

        bool wasListVisible = ListPane.Visibility == Visibility.Visible;
        if (wasListVisible)
        {
            _listOffsetBeforeDetails = _listScroll?.VerticalOffset ?? 0;
        }
        UpdateLayoutForSelection();
        if (ListPane.Visibility == Visibility.Collapsed)
        {
            BackButton.Focus(FocusState.Programmatic);
        }
        else if (!wasListVisible)
        {
            QueueScrollToOffset(_listOffsetBeforeDetails, focusList: !_returningToSearch);
            RecordInteraction();
        }
        QueueViewportUpdate();
    }

    private void OnBrowserSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool wasListVisible = ListPane.Visibility == Visibility.Visible;
        if (wasListVisible)
        {
            _listOffsetBeforeDetails = _listScroll?.VerticalOffset ?? 0;
        }
        UpdateLayoutForSelection();
        if (!wasListVisible && ListPane.Visibility == Visibility.Visible)
        {
            QueueScrollToOffset(_listOffsetBeforeDetails);
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

    private void OnResultsChanged(object? sender, EventArgs args) => QueueViewportUpdate();

    private void OnFiltersChanged(object? sender, EventArgs args)
    {
        SyncFilterControls();
        _listOffsetBeforeDetails = 0;
        QueueScrollToOffset(0);
    }

    private void QueueScrollToOffset(double offset, bool focusList = false)
    {
        int request = ++_scrollRequest;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!IsLoaded || request != _scrollRequest || ListPane.Visibility != Visibility.Visible)
            {
                return;
            }

            BindScrollViewer();
            if (_listScroll is ScrollViewer scroller)
            {
                scroller.ChangeView(null, Math.Min(offset, scroller.ScrollableHeight), null, disableAnimation: true);
            }
            if (focusList && BackButton.FocusState != FocusState.Unfocused)
            {
                ServerSearch.Focus(FocusState.Programmatic);
            }
        });
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
        // Apply background reordering only at the top, away from active list navigation.
        if (!_isScrolling && !_pointerDown && ListPane.Visibility == Visibility.Visible
            && (_listScroll?.VerticalOffset ?? 0) < 1 && FindFocusedRow() is null
            && !SortFilter.IsDropDownOpen && !PingFilter.IsDropDownOpen)
        {
            ViewModel.ApplyPendingProjection();
        }
    }

    private void OnStatusAnnouncementRequested(object? sender, string text)
    {
        FrameworkElementAutomationPeer.CreatePeerForElement(ResultsStatus)?.RaiseNotificationEvent(
            AutomationNotificationKind.Other, AutomationNotificationProcessing.MostRecent, text, "ServerBrowserResults");
    }
}
