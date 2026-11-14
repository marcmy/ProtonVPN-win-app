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


using ProtonVPN.Client.Core.Models.ReportIssue.Fields;

namespace ProtonVPN.Client.Logic.Feedback.Tests;

[TestClass]
public class ReportIssueFieldTest
{
    [TestMethod]
    public void RequiredField_ShowsErrorsOnlyAfterTouch_AndResetClearsValueAndTouch()
    {
        MultiLineTextInputField field = new() { MinimumLength = 5, MaximumLength = 10 };
        Assert.IsTrue(field.HasErrors);
        Assert.IsFalse(field.ShouldShowAsInvalid);
        field.MarkAsTouched();
        Assert.IsTrue(field.ShouldShowAsInvalid);
        field.Value = "abcde";
        Assert.IsFalse(field.HasErrors);
        Assert.AreEqual("5/10", field.FormattedCurrentLength);
        field.Reset();
        Assert.IsNull(field.Value);
        Assert.IsFalse(field.IsTouched);
        Assert.IsTrue(field.HasErrors);
        Assert.IsFalse(field.ShouldShowAsInvalid);
    }

    [TestMethod]
    public void OptionalField_AllowsWhitespace_ButEnforcesMaximumForNonemptyValue()
    {
        SingleLineTextInputField field = new(false) { MaximumLength = 5 };
        field.Value = " ";
        Assert.IsFalse(field.HasErrors);
        field.Value = "123456";
        Assert.IsTrue(field.HasErrors);
        field.Value = "12345";
        Assert.IsFalse(field.HasErrors);
    }

    [TestMethod]
    public void ConstraintChange_RevalidatesExistingValueAndNotifiesDerivedProperties()
    {
        SingleLineTextInputField field = new() { Value = "hello" };
        List<string?> changed = [];
        field.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        field.MinimumLength = 6;
        Assert.IsTrue(field.HasErrors);
        Assert.IsFalse(field.IsMinimumLengthMet);
        CollectionAssert.Contains(changed, nameof(field.IsMinimumLengthMet));
        field.MinimumLength = 0;
        field.MaximumLength = 4;
        Assert.IsTrue(field.HasErrors);
        field.MaximumLength = 5;
        Assert.IsFalse(field.HasErrors);
        CollectionAssert.Contains(changed, nameof(field.FormattedCurrentLength));
    }

    [TestMethod]
    public void Defaults_AreCompatibleWithLongerApiMinimum_AndEmailEnforcesFormatAndLength()
    {
        Assert.AreEqual(500, new SingleLineTextInputField().EffectiveMaximumLength);
        Assert.AreEqual(4000, new MultiLineTextInputField().EffectiveMaximumLength);
        Assert.AreEqual(5000, new MultiLineTextInputField { MinimumLength = 5000 }.EffectiveMaximumLength);
        EmailInputField email = new() { Value = "not-an-email" };
        Assert.IsTrue(email.HasErrors);
        email.Value = "person@example.test";
        Assert.IsFalse(email.HasErrors);
        email.MaximumLength = 5;
        Assert.IsTrue(email.HasErrors);
    }
}
