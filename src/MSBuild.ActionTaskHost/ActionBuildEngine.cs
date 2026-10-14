// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using Microsoft.Build.Framework;

namespace Microsoft.Build.ActionTaskHost;

internal sealed class ActionBuildEngine(TaskInvocation invocation) : IBuildEngine
{
    internal bool HasLoggedErrors { get; private set; }

    public bool ContinueOnError => false;

    public int LineNumberOfTaskNode => invocation.LineNumber;

    public int ColumnNumberOfTaskNode => invocation.ColumnNumber;

    public string ProjectFileOfTaskNode => invocation.ProjectFile;

    public void LogErrorEvent(BuildErrorEventArgs e)
    {
        HasLoggedErrors = true;
        Console.Error.WriteLine(HostResources.Format("ErrorEvent", e.File, e.LineNumber, e.ColumnNumber, e.Code, e.Message));
    }

    public void LogWarningEvent(BuildWarningEventArgs e)
        => Console.Error.WriteLine(HostResources.Format("WarningEvent", e.File, e.LineNumber, e.ColumnNumber, e.Code, e.Message));

    public void LogMessageEvent(BuildMessageEventArgs e) => Console.Out.WriteLine(e.Message);

    public void LogCustomEvent(CustomBuildEventArgs e) => Console.Out.WriteLine(e.Message);

    public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs)
        => throw new NotSupportedException(HostResources.Format("NestedBuildNotSupported"));
}
