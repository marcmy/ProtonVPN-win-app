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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProtonVPN.Client.Common.Dispatching;
using ProtonVPN.Client.Contracts.Messages;
using ProtonVPN.Client.Core.Bases;
using ProtonVPN.Client.Core.Bases.ViewModels;
using ProtonVPN.Client.EventMessaging.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts.Enums;
using ProtonVPN.Client.Logic.Connection.Contracts.Messages;
using ProtonVPN.Client.Logic.Connection.Contracts.Statistics;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.Settings.Contracts.Messages;
using ProtonVPN.Client.Settings.Contracts.Observers;
using ProtonVPN.Client.UI.Main.Home.Upsell;
using ProtonVPN.Common.Core.Extensions;

namespace ProtonVPN.Client.UI.Main.Home.Card.Feedback;

public partial class ConnectionFeedbackComponentViewModel : ActivatableViewModelBase,
    IEventMessageReceiver<ConnectionStatusChangedMessage>,
    IEventMessageReceiver<MainWindowFocusedMessage>,
    IEventMessageReceiver<TrayAppWindowFocusedMessage>,
    IEventMessageReceiver<FeatureFlagsChangedMessage>,
    IEventMessageReceiver<SettingChangedMessage>
{
    private const int SUBMIT_FEEDBACK_ANIMATION_DURATION_MS = 1000;
    private const int PAUSE_ANIMATION_DURATION_MS = 300;
    private const int DISMISS_FEEDBACK_ANIMATION_DURATION_MS = 500;
    private const int DEFAULT_AUTO_DISMISS_DURATION_MS = 10000;

    private readonly IConnectionManager _connectionManager;
    private readonly IConnectionStatisticsFeedback _connectionStatisticsFeedback;
    private readonly ISettings _settings;
    private readonly IFeatureFlagsObserver _featureFlagsObserver;
    private readonly IConnectionCardUpsellBannerModerator _connectionCardUpsellBannerModerator;
    private readonly IDispatcherTimer _autoDismissTimer;

    private bool _hasReceivedAppFocus;
    private bool _isFeedbackInitialized;
    private int _feedbackSessionId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReportPoorConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReportGoodConnectionCommand))]
    [NotifyPropertyChangedFor(nameof(IsConnectionFeedbackVisible))]
    private bool _isFeedbackSent;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReportPoorConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReportGoodConnectionCommand))]
    private bool _isSendingFeedback;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReportPoorConnectionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReportGoodConnectionCommand))]
    private bool _isDismissingFeedback;

    public bool IsConnectionFeedbackVisible => !IsFeedbackSent
                                            && IsElligible
                                            && _connectionManager.IsConnected
                                            && _hasReceivedAppFocus;

    public bool IsElligible => _settings.IsShareStatisticsEnabled
                            && _featureFlagsObserver.ConnectionFeedback.IsEnabled;

    public ConnectionFeedbackComponentViewModel(
        IConnectionManager connectionManager,
        IConnectionStatisticsFeedback connectionStatisticsFeedback,
        ISettings settings,
        IFeatureFlagsObserver featureFlagsObserver,
        IConnectionCardUpsellBannerModerator connectionCardUpsellBannerModerator,
        IViewModelHelper viewModelHelper)
        : base(viewModelHelper)
    {
        _connectionManager = connectionManager;
        _connectionStatisticsFeedback = connectionStatisticsFeedback;
        _settings = settings;
        _featureFlagsObserver = featureFlagsObserver;
        _connectionCardUpsellBannerModerator = connectionCardUpsellBannerModerator;

        _autoDismissTimer = UIThreadDispatcher.GetTimer(GetAutoDismissFeedbackDelay());
        _autoDismissTimer.Tick += OnAutoDismissTimerTick;
    }

    [RelayCommand(CanExecute = nameof(CanReportConnection))]
    private async Task ReportPoorConnectionAsync()
    {
        int feedbackSessionId = _feedbackSessionId;

        try
        {
            StopAutoDismissTimer();
            IsSendingFeedback = true;

            _connectionStatisticsFeedback.SubmitNegativeFeedback();

            await Task.Delay(SUBMIT_FEEDBACK_ANIMATION_DURATION_MS + PAUSE_ANIMATION_DURATION_MS);
        }
        finally
        {
            CompleteFeedbackSubmission(feedbackSessionId);
        }
    }

    [RelayCommand(CanExecute = nameof(CanReportConnection))]
    private async Task ReportGoodConnectionAsync()
    {
        int feedbackSessionId = _feedbackSessionId;

        try
        {
            StopAutoDismissTimer();
            IsSendingFeedback = true;

            _connectionStatisticsFeedback.SubmitPositiveFeedback();

            await Task.Delay(SUBMIT_FEEDBACK_ANIMATION_DURATION_MS + PAUSE_ANIMATION_DURATION_MS);
        }
        finally
        {
            CompleteFeedbackSubmission(feedbackSessionId);
        }
    }

    private void CompleteFeedbackSubmission(int feedbackSessionId)
    {
        // A disconnect invalidates delayed submit-animation work from the previous connection session.
        if (feedbackSessionId == _feedbackSessionId)
        {
            IsFeedbackSent = true;
            IsSendingFeedback = false;
        }
    }

    private bool CanReportConnection()
    {
        return !IsSendingFeedback
            && !IsDismissingFeedback
            && IsConnectionFeedbackVisible;
    }

    public void Receive(ConnectionStatusChangedMessage message)
    {
        ExecuteOnUIThread(() =>
        {
            // Reset feedback state when the connection drops.
            if (message.ConnectionStatus != ConnectionStatus.Connected)
            {
                _feedbackSessionId++;
                _hasReceivedAppFocus = false;
                _isFeedbackInitialized = false;
                IsFeedbackSent = false;
                IsSendingFeedback = false;
                IsDismissingFeedback = false;
            }

            SynchronizeFeedbackState();
        });
    }

    public void Receive(MainWindowFocusedMessage message)
    {
        ExecuteOnUIThread(OnAppFocused);
    }

    public void Receive(TrayAppWindowFocusedMessage message)
    {
        ExecuteOnUIThread(OnAppFocused);
    }

    public void Receive(FeatureFlagsChangedMessage message)
    {
        if (message.Changes.Any(f => f.Name == nameof(IFeatureFlagsObserver.ConnectionFeedback)))
        {
            ExecuteOnUIThread(() =>
            {
                // Restart an active countdown so the new payload applies immediately.
                StopAutoDismissTimer();
                _autoDismissTimer.Interval = GetAutoDismissFeedbackDelay();
                SynchronizeFeedbackState();
            });
        }
    }

    public void Receive(SettingChangedMessage message)
    {
        if (message.PropertyName == nameof(ISettings.IsShareStatisticsEnabled))
        {
            ExecuteOnUIThread(SynchronizeFeedbackState);
        }
    }

    private void OnAppFocused()
    {
        if (_connectionManager.IsConnected && !_connectionCardUpsellBannerModerator.IsBannerVisible)
        {
            _hasReceivedAppFocus = true;
        }

        SynchronizeFeedbackState();
    }

    private void SynchronizeFeedbackState()
    {
        NotifyFeedbackStateChanged();

        if (!IsConnectionFeedbackVisible || IsSendingFeedback || IsDismissingFeedback)
        {
            StopAutoDismissTimer();
            return;
        }

        if (!_isFeedbackInitialized)
        {
            // Initialize feedback state once for the current connection session to 'ignore'.
            // This ensures that if the user doesn't provide feedback, the session will be categorized as 'ignore' in the statistics, instead of 'unknown'.
            _connectionStatisticsFeedback.InitializeFeedback();
            _isFeedbackInitialized = true;
        }

        StartAutoDismissTimer();
    }

    private void StartAutoDismissTimer()
    {
        if (!_autoDismissTimer.IsEnabled)
        {
            _autoDismissTimer.Start();
        }
    }

    private void StopAutoDismissTimer()
    {
        if (_autoDismissTimer.IsEnabled)
        {
            _autoDismissTimer.Stop();
        }
    }

    private void OnAutoDismissTimerTick(object? sender, object e)
    {
        StopAutoDismissTimer();

        if (CanReportConnection())
        {
            int feedbackSessionId = _feedbackSessionId;
            DismissFeedbackAsync(feedbackSessionId).FireAndForget();
        }
    }

    private async Task DismissFeedbackAsync(int feedbackSessionId)
    {
        try
        {
            IsDismissingFeedback = true;
            await Task.Delay(DISMISS_FEEDBACK_ANIMATION_DURATION_MS);
        }
        finally
        {
            // A disconnect invalidates delayed work from the previous connection session.
            if (feedbackSessionId == _feedbackSessionId)
            {
                IsFeedbackSent = true;
                IsDismissingFeedback = false;
            }
        }
    }

    private void NotifyFeedbackStateChanged()
    {
        OnPropertyChanged(nameof(IsElligible));
        OnPropertyChanged(nameof(IsConnectionFeedbackVisible));
        ReportPoorConnectionCommand.NotifyCanExecuteChanged();
        ReportGoodConnectionCommand.NotifyCanExecuteChanged();
    }

    private TimeSpan GetAutoDismissFeedbackDelay()
    {
        return int.TryParse(_featureFlagsObserver.ConnectionFeedback.Payload, out int timeoutSeconds)
            ? TimeSpan.FromSeconds(timeoutSeconds)
            : TimeSpan.FromMilliseconds(DEFAULT_AUTO_DISMISS_DURATION_MS);
    }
}
