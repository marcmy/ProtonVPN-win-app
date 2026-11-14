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

using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ProtonVPN.Client.Core.Models.ReportIssue.Fields;

public abstract partial class TextInputFieldBase : IssueInputField<string>
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveMinimumLength), nameof(EffectiveMaximumLength), nameof(IsMandatory),
        nameof(HasMinimumLength), nameof(HasMaximumLength), nameof(HasLengthConstraint), nameof(IsMinimumLengthMet), nameof(FormattedCurrentLength))]
    private int? _minimumLength;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveMaximumLength), nameof(HasMaximumLength), nameof(HasLengthConstraint), nameof(FormattedCurrentLength))]
    private int? _maximumLength;

    protected abstract int DefaultMaximumLength { get; }
    public int EffectiveMinimumLength => MinimumLength ?? 1;
    public int EffectiveMaximumLength => MaximumLength ?? Math.Max(DefaultMaximumLength, EffectiveMinimumLength);
    public bool IsMandatory => EffectiveMinimumLength > 0;
    public bool HasMinimumLength => EffectiveMinimumLength > 1;
    public bool HasMaximumLength => EffectiveMaximumLength > 0;
    public bool HasLengthConstraint => HasMinimumLength || HasMaximumLength;
    public int CurrentLength => Value?.Length ?? 0;
    public string FormattedCurrentLength => $"{CurrentLength}/{EffectiveMaximumLength}";
    public bool IsMinimumLengthMet => !HasMinimumLength || CurrentLength >= EffectiveMinimumLength;
    public string LengthHint { get; set; } = string.Empty;

    protected TextInputFieldBase(bool isFieldMandatory = true)
    {
        if (!isFieldMandatory)
        {
            MinimumLength = 0;
        }
    }

    protected override bool IsValid()
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
            return !IsMandatory;
        }
        return (!HasMinimumLength || CurrentLength >= EffectiveMinimumLength)
            && (!HasMaximumLength || CurrentLength <= EffectiveMaximumLength);
    }

    protected override void OnValueChanged()
    {
        base.OnValueChanged();
        OnPropertyChanged(nameof(CurrentLength));
        OnPropertyChanged(nameof(FormattedCurrentLength));
        OnPropertyChanged(nameof(IsMinimumLengthMet));
    }

    partial void OnMinimumLengthChanged(int? value) => ValidateProperty(Value, nameof(Value));
    partial void OnMaximumLengthChanged(int? value) => ValidateProperty(Value, nameof(Value));

    protected override string GetSerializedValue()
    {
        return Value ?? string.Empty;
    }
}
