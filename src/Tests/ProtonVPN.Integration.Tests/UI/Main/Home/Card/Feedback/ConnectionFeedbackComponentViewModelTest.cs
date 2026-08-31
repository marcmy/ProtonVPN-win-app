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

using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Client.Common.Dispatching;
using ProtonVPN.Client.Contracts.Messages;
using ProtonVPN.Client.Core.Bases;
using ProtonVPN.Client.Logic.Connection.Contracts;
using ProtonVPN.Client.Logic.Connection.Contracts.Enums;
using ProtonVPN.Client.Logic.Connection.Contracts.Messages;
using ProtonVPN.Client.Logic.Connection.Contracts.Statistics;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.Settings.Contracts.Messages;
using ProtonVPN.Client.Settings.Contracts.Models;
using ProtonVPN.Client.Settings.Contracts.Observers;
using ProtonVPN.Client.UI.Main.Home.Card.Feedback;
using ProtonVPN.Client.UI.Main.Home.Upsell;

namespace ProtonVPN.Integration.Tests.UI.Main.Home.Card.Feedback;

[TestClass]
public class ConnectionFeedbackComponentViewModelTest
{
    [TestMethod]
    public void ReceiveMainWindowFocused_WhenFeedbackIsVisible_InitializesOnceAndStartsTimerOnce()
    {
        // Arrange
        TestContext context = new();

        // Act
        context.ViewModel.Receive(new MainWindowFocusedMessage());
        context.ViewModel.Receive(new MainWindowFocusedMessage());

        // Assert
        Assert.IsTrue(context.ViewModel.IsConnectionFeedbackVisible);
        Assert.IsTrue(context.Timer.IsEnabled);
        Assert.AreEqual(1, context.Timer.StartCount);
        context.ConnectionStatisticsFeedback.Received(1).InitializeFeedback();
    }

    [TestMethod]
    public void ReceiveSettingChanged_WhenStatisticsSharingIsReenabled_RestartsTimerWithoutReinitializing()
    {
        // Arrange
        TestContext context = new();
        context.ViewModel.Receive(new MainWindowFocusedMessage());

        // Act
        context.IsShareStatisticsEnabled = false;
        context.ViewModel.Receive(CreateStatisticsSharingChangedMessage(true, false));

        // Assert disabled state
        Assert.IsFalse(context.ViewModel.IsConnectionFeedbackVisible);
        Assert.IsFalse(context.Timer.IsEnabled);

        // Act
        context.IsShareStatisticsEnabled = true;
        context.ViewModel.Receive(CreateStatisticsSharingChangedMessage(false, true));

        // Assert re-enabled state
        Assert.IsTrue(context.ViewModel.IsConnectionFeedbackVisible);
        Assert.IsTrue(context.Timer.IsEnabled);
        Assert.AreEqual(2, context.Timer.StartCount);
        context.ConnectionStatisticsFeedback.Received(1).InitializeFeedback();
    }

    [TestMethod]
    public void ReceiveFeatureFlagsChanged_WhenTimeoutChanges_RestartsActiveTimerWithNewInterval()
    {
        // Arrange
        TestContext context = new();
        context.ViewModel.Receive(new MainWindowFocusedMessage());

        // Act
        context.ConnectionFeedback = CreateConnectionFeedbackFeatureFlag(true, "30");
        context.ViewModel.Receive(CreateConnectionFeedbackChangedMessage(true, true, "10", "30"));

        // Assert
        Assert.IsTrue(context.Timer.IsEnabled);
        Assert.AreEqual(TimeSpan.FromSeconds(30), context.Timer.Interval);
        Assert.AreEqual(2, context.Timer.StartCount);
        Assert.AreEqual(1, context.Timer.StopCount);
        context.ConnectionStatisticsFeedback.Received(1).InitializeFeedback();
    }

    [TestMethod]
    public void ReceiveFeatureFlagsChanged_WhenFeatureIsEnabledAfterFocus_StartsFeedbackWithoutAnotherFocusEvent()
    {
        // Arrange
        TestContext context = new(connectionFeedbackEnabled: false);
        context.ViewModel.Receive(new MainWindowFocusedMessage());

        Assert.IsFalse(context.ViewModel.IsConnectionFeedbackVisible);
        Assert.IsFalse(context.Timer.IsEnabled);
        context.ConnectionStatisticsFeedback.DidNotReceive().InitializeFeedback();

        // Act
        context.ConnectionFeedback = CreateConnectionFeedbackFeatureFlag(true, "10");
        context.ViewModel.Receive(CreateConnectionFeedbackChangedMessage(false, true, "10", "10"));

        // Assert
        Assert.IsTrue(context.ViewModel.IsConnectionFeedbackVisible);
        Assert.IsTrue(context.Timer.IsEnabled);
        Assert.AreEqual(1, context.Timer.StartCount);
        context.ConnectionStatisticsFeedback.Received(1).InitializeFeedback();
    }

    [TestMethod]
    public async Task AutoDismiss_WhenConnectionDropsDuringDismiss_DoesNotDismissNextSession()
    {
        // Arrange
        TestContext context = new();
        context.ViewModel.Receive(new MainWindowFocusedMessage());

        // Act - start the old session's 500 ms dismissal.
        context.Timer.RaiseTick();

        // Assert dismissal started synchronously before the animation delay.
        Assert.IsTrue(context.ViewModel.IsDismissingFeedback);

        // Act - disconnect invalidates that delayed dismissal, then reconnect and focus a new session.
        context.IsConnected = false;
        context.ViewModel.Receive(new ConnectionStatusChangedMessage(ConnectionStatus.Disconnected));

        Assert.IsFalse(context.ViewModel.IsDismissingFeedback);
        Assert.IsFalse(context.ViewModel.IsFeedbackSent);

        context.IsConnected = true;
        context.ViewModel.Receive(new ConnectionStatusChangedMessage(ConnectionStatus.Connected));
        context.ViewModel.Receive(new MainWindowFocusedMessage());

        Assert.IsTrue(context.ViewModel.IsConnectionFeedbackVisible);
        Assert.IsTrue(context.Timer.IsEnabled);
        context.ConnectionStatisticsFeedback.Received(2).InitializeFeedback();

        // Let the old session's delayed finally block run.
        await Task.Delay(750);

        // Assert the stale completion did not hide or mutate the new session.
        Assert.IsFalse(context.ViewModel.IsFeedbackSent);
        Assert.IsFalse(context.ViewModel.IsDismissingFeedback);
        Assert.IsTrue(context.ViewModel.IsConnectionFeedbackVisible);
        Assert.IsTrue(context.Timer.IsEnabled);
    }

    private static SettingChangedMessage CreateStatisticsSharingChangedMessage(bool oldValue, bool newValue)
    {
        return new SettingChangedMessage(
            nameof(ISettings.IsShareStatisticsEnabled),
            typeof(bool),
            oldValue,
            newValue);
    }

    private static FeatureFlagsChangedMessage CreateConnectionFeedbackChangedMessage(
        bool oldValue,
        bool newValue,
        string oldPayload,
        string newPayload)
    {
        return new FeatureFlagsChangedMessage
        {
            Changes =
            [
                new FeatureFlagChange
                {
                    Name = nameof(IFeatureFlagsObserver.ConnectionFeedback),
                    OldValue = oldValue,
                    NewValue = newValue,
                    OldPayload = oldPayload,
                    NewPayload = newPayload,
                },
            ],
        };
    }

    private static FeatureFlag CreateConnectionFeedbackFeatureFlag(bool isEnabled, string payload)
    {
        return new FeatureFlag
        {
            Name = "IsConnectionFeedbackEnabled",
            IsEnabled = isEnabled,
            Payload = payload,
        };
    }

    private sealed class TestContext
    {
        public bool IsConnected { get; set; } = true;
        public bool IsShareStatisticsEnabled { get; set; } = true;
        public FeatureFlag ConnectionFeedback { get; set; }

        public TestDispatcherTimer Timer { get; } = new();
        public IConnectionStatisticsFeedback ConnectionStatisticsFeedback { get; } = Substitute.For<IConnectionStatisticsFeedback>();
        public ConnectionFeedbackComponentViewModel ViewModel { get; }

        public TestContext(bool connectionFeedbackEnabled = true)
        {
            ConnectionFeedback = CreateConnectionFeedbackFeatureFlag(connectionFeedbackEnabled, "10");

            IConnectionManager connectionManager = Substitute.For<IConnectionManager>();
            connectionManager.IsConnected.Returns(_ => IsConnected);

            ISettings settings = Substitute.For<ISettings>();
            settings.IsShareStatisticsEnabled.Returns(_ => IsShareStatisticsEnabled);

            IFeatureFlagsObserver featureFlagsObserver = Substitute.For<IFeatureFlagsObserver>();
            featureFlagsObserver.ConnectionFeedback.Returns(_ => ConnectionFeedback);

            IConnectionCardUpsellBannerModerator upsellBannerModerator = Substitute.For<IConnectionCardUpsellBannerModerator>();
            upsellBannerModerator.IsBannerVisible.Returns(false);

            TestUiThreadDispatcher uiThreadDispatcher = new(Timer);
            IViewModelHelper viewModelHelper = Substitute.For<IViewModelHelper>();
            viewModelHelper.UIThreadDispatcher.Returns(uiThreadDispatcher);

            ViewModel = new ConnectionFeedbackComponentViewModel(
                connectionManager,
                ConnectionStatisticsFeedback,
                settings,
                featureFlagsObserver,
                upsellBannerModerator,
                viewModelHelper);
        }
    }

    private sealed class TestUiThreadDispatcher(TestDispatcherTimer timer) : IUIThreadDispatcher
    {
        public bool TryEnqueue(
            Action callback,
            string sourceFilePath = "",
            string sourceMemberName = "",
            int sourceLineNumber = 0)
        {
            callback();
            return true;
        }

        public async Task<bool> TryEnqueueAsync(
            Func<Task> callback,
            string sourceFilePath = "",
            string sourceMemberName = "",
            int sourceLineNumber = 0)
        {
            await callback();
            return true;
        }

        public IDispatcherTimer GetTimer(TimeSpan interval)
        {
            timer.Interval = interval;
            return timer;
        }
    }

    private sealed class TestDispatcherTimer : IDispatcherTimer
    {
        public event EventHandler<object>? Tick;

        public bool IsEnabled { get; private set; }
        public TimeSpan Interval { get; set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

        public void Start()
        {
            IsEnabled = true;
            StartCount++;
        }

        public void Stop()
        {
            IsEnabled = false;
            StopCount++;
        }

        public void RaiseTick()
        {
            Tick?.Invoke(this, EventArgs.Empty);
        }
    }
}
