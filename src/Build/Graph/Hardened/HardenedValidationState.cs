// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable enable

namespace Microsoft.Build.Graph.Hardened;

internal enum ValueAvailability
{
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

internal sealed class ValueOrigin(string description, ValueOrigin? previous = null)
{
    public override string ToString()
        => previous is null ? description : $"{description} from {previous}";
}
