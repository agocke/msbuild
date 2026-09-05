// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics.CodeAnalysis;

#nullable enable

namespace Microsoft.Build.Graph.Hardened;

internal enum ValueAvailability
{
    Uninitialized,
    Static,
    Blocked,
    Deferred,
}

internal readonly record struct ValueState(ValueAvailability Availability, ValueOrigin? Origin = null)
{
    internal static ValueState Static => new(ValueAvailability.Static);

    internal static ValueState Deferred(ValueOrigin origin) => new(ValueAvailability.Deferred, origin);

    internal static ValueState Blocked(ValueOrigin origin) => new(ValueAvailability.Blocked, origin);

    internal bool IsStatic => Availability == ValueAvailability.Static;

    internal ValueState WithOrigin(string description)
        => IsStatic ? this : new ValueState(Availability, new ValueOrigin(description, Origin));

    internal static ValueState Combine(ValueState left, ValueState right)
        => left.Availability >= right.Availability ? left : right;
}

internal readonly struct HardenedValue<T>
{
    private readonly T? _value;

    private HardenedValue(ValueState state, T? value)
    {
        State = state;
        _value = value;
    }

    internal ValueState State { get; }

    internal bool IsStatic => State.IsStatic;

    internal static HardenedValue<T> Static(T value)
        => new(ValueState.Static, value);

    internal static HardenedValue<T> NonStatic(ValueState state)
    {
        if (state.Availability is not (ValueAvailability.Blocked or ValueAvailability.Deferred))
        {
            throw new ArgumentException(
                "A non-static hardened value must be blocked or deferred.",
                nameof(state));
        }

        return new HardenedValue<T>(state, default);
    }

    internal bool TryGetStaticValue([NotNullWhen(true)] out T? value)
    {
        value = IsStatic ? _value : default;
        return IsStatic;
    }

    internal T GetStaticValue()
        => IsStatic
            ? _value!
            : throw new InvalidOperationException("A non-static hardened value has no concrete value.");

    internal HardenedValue<T> WithOrigin(string description)
        => IsStatic
            ? this
            : NonStatic(State.WithOrigin(description));
}

internal sealed class ValueOrigin(string description, ValueOrigin? previous = null)
{
    public override string ToString()
        => previous is null ? description : $"{description} from {previous}";
}
