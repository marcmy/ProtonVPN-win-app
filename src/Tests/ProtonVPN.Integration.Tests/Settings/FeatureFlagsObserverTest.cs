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

using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using ProtonVPN.Api.Contracts;
using ProtonVPN.Api.Contracts.Features;
using ProtonVPN.Client.EventMessaging.Contracts;
using ProtonVPN.Client.Settings.Contracts;
using ProtonVPN.Client.Settings.Contracts.Messages;
using ProtonVPN.Client.Settings.Contracts.Models;
using ProtonVPN.Client.Settings.Contracts.Observers;
using ProtonVPN.Client.Settings.Observers;
using ProtonVPN.Configurations.Contracts;
using ProtonVPN.IssueReporting.Contracts;
using ProtonVPN.Logging.Contracts;

namespace ProtonVPN.Integration.Tests.Settings;

[TestClass]
public class FeatureFlagsObserverTest
{
    [TestMethod]
    public async Task UpdateAsync_WhenOnlyConnectionFeedbackPayloadChanges_SendsPayloadChange()
    {
        // Arrange
        ISettings settings = Substitute.For<ISettings>();
        settings.FeatureFlags.Returns(
        [
            new FeatureFlag
            {
                Name = "IsConnectionFeedbackEnabled",
                IsEnabled = true,
                Payload = "10",
            },
        ]);

        FeatureFlagsResponse updatedResponse = new()
        {
            FeatureFlags =
            [
                new FeatureFlagResponse
                {
                    Name = "IsConnectionFeedbackEnabled",
                    IsEnabled = true,
                    Variant = new FeatureFlagVariantResponse
                    {
                        Name = string.Empty,
                        IsEnabled = true,
                        Payload = new FeatureFlagVariantPayloadResponse
                        {
                            Type = "string",
                            Value = "30",
                        },
                    },
                },
            ],
        };

        ApiResponseResult<FeatureFlagsResponse> initialFailure = ApiResponseResult<FeatureFlagsResponse>.Fail(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "Initial background refresh suppressed by test");
        ApiResponseResult<FeatureFlagsResponse> updatedResult = ApiResponseResult<FeatureFlagsResponse>.Ok(
            new HttpResponseMessage(HttpStatusCode.OK),
            updatedResponse);

        TaskCompletionSource<bool> initialRefreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IApiClient apiClient = Substitute.For<IApiClient>();
        apiClient.GetFeatureFlagsAsync(Arg.Any<CancellationToken>()).Returns(
            _ =>
            {
                initialRefreshStarted.TrySetResult(true);
                return Task.FromResult(initialFailure);
            },
            _ => Task.FromResult(updatedResult));

        FeatureFlagsChangedMessage? sentMessage = null;
        IEventMessageSender eventMessageSender = Substitute.For<IEventMessageSender>();
        eventMessageSender
            .When(x => x.Send(Arg.Any<FeatureFlagsChangedMessage>()))
            .Do(callInfo => sentMessage = callInfo.Arg<FeatureFlagsChangedMessage>());

        IConfiguration config = Substitute.For<IConfiguration>();
        config.FeatureFlagsUpdateInterval.Returns(TimeSpan.FromHours(1));

        using FeatureFlagsObserver observer = new(
            Substitute.For<ILogger>(),
            Substitute.For<IIssueReporter>(),
            settings,
            apiClient,
            config,
            eventMessageSender);

        await initialRefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Act
        await observer.UpdateAsync(CancellationToken.None);

        // Assert
        Assert.IsNotNull(sentMessage);
        Assert.HasCount(1, sentMessage.Changes);

        FeatureFlagChange change = sentMessage.Changes[0];
        Assert.AreEqual(nameof(IFeatureFlagsObserver.ConnectionFeedback), change.Name);
        Assert.AreEqual(true, change.OldValue);
        Assert.AreEqual(true, change.NewValue);
        Assert.AreEqual("10", change.OldPayload);
        Assert.AreEqual("30", change.NewPayload);
        Assert.IsFalse(change.HasValueChanged);
        Assert.IsTrue(change.HasPayloadChanged);
        Assert.IsTrue(change.HasChanged);
    }
}
