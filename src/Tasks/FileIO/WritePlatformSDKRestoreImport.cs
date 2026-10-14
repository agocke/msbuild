// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Text;
using System.Xml;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Utilities;

#nullable enable

namespace Microsoft.Build.Tasks;

/// <summary>
/// Persists platform SDK discovery and its configuration for a later graph evaluation.
/// </summary>
[MSBuildDeclaredIOTask]
[MSBuildMultiThreadableTask]
public sealed class WritePlatformSDKRestoreImport : TaskExtension, IMultiThreadableTask
{
    private const string MSBuildXmlNamespace = "http://schemas.microsoft.com/developer/msbuild/2003";

    public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

    [Required]
    public ITaskItem File { get; set; } = null!;

    public ITaskItem[] Inputs { get; set; } = [];

    public ITaskItem[] Facts { get; set; } = [];

    public ITaskItem[] DeclaredInputs { get; set; } = [];

    public ITaskItem[] DeclaredOutputs { get; set; } = [];

    public override bool Execute()
    {
        ArgumentNullException.ThrowIfNull(File);
        StringBuilder contents = new();
        try
        {
            using (XmlWriter writer = XmlWriter.Create(contents, new XmlWriterSettings
            {
                Indent = true,
                NewLineChars = "\n",
                OmitXmlDeclaration = true,
            }))
            {
                writer.WriteStartElement("Project", MSBuildXmlNamespace);
                writer.WriteAttributeString("InitialTargets", "_ValidatePlatformSDKRestore");
                writer.WriteStartElement("PropertyGroup", MSBuildXmlNamespace);
                foreach (ITaskItem input in Inputs)
                {
                    string name = XmlConvert.VerifyNCName(input.ItemSpec);
                    writer.WriteElementString("_RestoredPlatformSDKInput" + name,
                        MSBuildXmlNamespace, EscapingUtilities.Escape(input.GetMetadata("Value")));
                }

                foreach (ITaskItem fact in Facts)
                {
                    string name = XmlConvert.VerifyNCName(fact.ItemSpec);
                    writer.WriteElementString(name, MSBuildXmlNamespace,
                        EscapingUtilities.Escape(fact.GetMetadata("Value")));
                }

                writer.WriteEndElement();
                writer.WriteStartElement("Target", MSBuildXmlNamespace);
                writer.WriteAttributeString("Name", "_ValidatePlatformSDKRestore");
                foreach (ITaskItem input in Inputs)
                {
                    string name = XmlConvert.VerifyNCName(input.ItemSpec);
                    writer.WriteStartElement("MSBuildInternalMessage", MSBuildXmlNamespace);
                    writer.WriteAttributeString("Condition",
                        $"!$([System.String]::Equals('$(_PlatformSDKRestoreInput{name})', '$(_RestoredPlatformSDKInput{name})', System.StringComparison.Ordinal))");
                    writer.WriteAttributeString("ResourceName", "CommonSdk.PlatformSDKRestoreConfigurationChanged");
                    writer.WriteAttributeString("Severity", "Error");
                    writer.WriteAttributeString("FormatArguments",
                        EscapingUtilities.Escape(File.ItemSpec) + ";" + name);
                    writer.WriteAttributeString("DeclaredInputs", string.Empty);
                    writer.WriteAttributeString("DeclaredOutputs", string.Empty);
                    writer.WriteEndElement();
                }

                writer.WriteEndElement();
                writer.WriteEndElement();
            }
        }
        catch (XmlException e)
        {
            Log.LogErrorWithCodeFromResources("WriteLinesToFile.ErrorOrWarning", File.ItemSpec, e.Message, string.Empty);
            return false;
        }

        WriteLinesToFile write = new()
        {
            BuildEngine = BuildEngine,
            TaskEnvironment = TaskEnvironment,
            File = File,
            Lines = [new TaskItem(EscapingUtilities.Escape(contents.ToString()))],
            Overwrite = true,
            WriteOnlyWhenDifferent = true,
        };
        return write.Execute();
    }
}
