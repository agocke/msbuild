// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using Microsoft.Build.Collections;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;

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

internal sealed class HardenedConcreteState :
    IPropertyProvider<ProjectPropertyInstance>,
    IItemProvider<ProjectItemInstance>
{
    private readonly ProjectInstance _project;
    private readonly PropertyDictionary<ProjectPropertyInstance> _properties = new();

    internal HardenedConcreteState(ProjectInstance project)
    {
        _project = project;
    }

    private HardenedConcreteState(
        ProjectInstance project,
        PropertyDictionary<ProjectPropertyInstance> properties)
    {
        _project = project;
        _properties = properties;
    }

    internal string ProjectDirectory => _project.Directory;

    internal HardenedConcreteState Clone()
        => new(_project, new PropertyDictionary<ProjectPropertyInstance>(_properties));

    internal void SetProperty(string name, string value)
    {
        _properties.Set(ProjectPropertyInstance.Create(name, value));
    }

    public ProjectPropertyInstance GetProperty(string name)
        => (_properties.GetProperty(name) ?? _project.GetProperty(name))!;

    public ProjectPropertyInstance GetProperty(string name, int startIndex, int endIndex)
        => (_properties.GetProperty(name, startIndex, endIndex) ??
            ((IPropertyProvider<ProjectPropertyInstance>)_project).GetProperty(name, startIndex, endIndex))!;

    public ICollection<ProjectItemInstance> GetItems(string itemType)
        => _project.GetItems(itemType);
}
