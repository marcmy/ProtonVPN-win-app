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
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with ProtonVPN. If not, see <https://www.gnu.org/licenses/>.
 */


using NSubstitute;
using ProtonVPN.Api.Contracts;
using ProtonVPN.Api.Contracts.ReportAnIssue;

namespace ProtonVPN.Client.Logic.Feedback.Tests;

[TestClass]
public class ReportIssueDataProviderTest
{
    [TestMethod]
    public async Task FailedRequest_UsesEmbeddedForm_ButRetriesAndCachesSubsequentSuccess()
    {
        IApiClient api = Substitute.For<IApiClient>();
        api.GetReportAnIssueFormData().Returns(ApiResponseResult<ReportAnIssueFormResponse>.Fail(null!, "offline"), ValidResponse());
        ReportIssueDataProvider provider = new(api);

        List<IssueCategoryResponse> fallback = await provider.GetCategoriesAsync();
        Assert.IsTrue(fallback.Count > 0);
        Assert.IsTrue(fallback.SelectMany(c => c.InputFields).Any(f => f.Constraints?.MinimumLength >= 100));
        Assert.AreEqual("API category", (await provider.GetCategoriesAsync()).Single().Label);
        Assert.AreEqual("API category", (await provider.GetCategoriesAsync()).Single().Label);
        await api.Received(2).GetReportAnIssueFormData();
    }

    [TestMethod]
    [DataRow(-1, 10)]
    [DataRow(0, 0)]
    [DataRow(10, 5)]
    public async Task InvalidConstraints_FallBackWithoutCachingBadForm(int minimum, int maximum)
    {
        IApiClient api = Substitute.For<IApiClient>();
        ApiResponseResult<ReportAnIssueFormResponse> response = ValidResponse();
        response.Value.Categories[0].InputFields[0].Constraints = new() { MinimumLength = minimum, MaximumLength = maximum };
        api.GetReportAnIssueFormData().Returns(response, ValidResponse());
        ReportIssueDataProvider provider = new(api);

        Assert.AreNotEqual("API category", (await provider.GetCategoriesAsync()).First().Label);
        Assert.AreEqual("API category", (await provider.GetCategoriesAsync()).Single().Label);
        await api.Received(2).GetReportAnIssueFormData();
    }

    private static ApiResponseResult<ReportAnIssueFormResponse> ValidResponse() =>
        ApiResponseResult<ReportAnIssueFormResponse>.Ok(null!, new ReportAnIssueFormResponse
        {
            Categories = [new IssueCategoryResponse
            {
                Label = "API category", SubmitLabel = "API category",
                InputFields = [new IssueInputResponse { Label = "Details", SubmitLabel = "Details", Type = "TextMultiLine" }],
            }],
        });
}
