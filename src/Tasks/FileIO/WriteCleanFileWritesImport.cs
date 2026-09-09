// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Text;
using System.Xml;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.Utilities;

#nullable disable

namespace Microsoft.Build.Tasks
{
    /// <summary>
    /// Writes the prior clean ledger as an ordinary imported MSBuild item file.
    /// </summary>
    [MSBuildDeclaredIOTask]
    [MSBuildMultiThreadableTask]
    public sealed class WriteCleanFileWritesImport : TaskExtension, IMultiThreadableTask
    {
        private const string MSBuildXmlNamespace = "http://schemas.microsoft.com/developer/msbuild/2003";
        private static readonly char[] s_charsToTrim = ['\0', ' ', '\t'];

        /// <inheritdoc />
        public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

        /// <summary>
        /// The existing clean ledger to snapshot.
        /// </summary>
        [Required]
        public ITaskItem CleanFile { get; set; }

        /// <summary>
        /// The imported MSBuild file to write.
        /// </summary>
        [Required]
        public ITaskItem ImportFile { get; set; }

        /// <summary>
        /// Exact file paths whose pre-invocation state may affect this invocation.
        /// </summary>
        public ITaskItem[] DeclaredInputs { get; set; }

        /// <summary>
        /// Exact persistent file paths that this invocation may create or modify.
        /// </summary>
        public ITaskItem[] DeclaredOutputs { get; set; }

        /// <inheritdoc cref="ITask.Execute" />
        public override bool Execute()
        {
            ArgumentNullException.ThrowIfNull(CleanFile);
            ArgumentNullException.ThrowIfNull(ImportFile);

            AbsolutePath cleanFilePath = TaskEnvironment.GetAbsolutePath(CleanFile.ItemSpec);
            AbsolutePath importFilePath = TaskEnvironment.GetAbsolutePath(ImportFile.ItemSpec);

            try
            {
                string[] priorFileWrites = FileSystems.Default.FileExists(cleanFilePath)
                    ? File.ReadAllLines(cleanFilePath)
                    : [];

                StringBuilder contents = new();
                using (XmlWriter writer = XmlWriter.Create(
                    contents,
                    new XmlWriterSettings
                    {
                        Indent = true,
                        NewLineChars = "\n",
                        OmitXmlDeclaration = true,
                    }))
                {
                    writer.WriteStartElement("Project", MSBuildXmlNamespace);
                    writer.WriteStartElement("ItemGroup", MSBuildXmlNamespace);

                    foreach (string priorFileWrite in priorFileWrites)
                    {
                        string trimmedPriorFileWrite = priorFileWrite.Trim(s_charsToTrim);
                        if (trimmedPriorFileWrite.Length == 0)
                        {
                            continue;
                        }

                        writer.WriteStartElement("_CleanUnfilteredPriorFileWrites", MSBuildXmlNamespace);
                        writer.WriteAttributeString("Include", trimmedPriorFileWrite);
                        writer.WriteEndElement();
                    }

                    writer.WriteEndElement();
                    writer.WriteEndElement();
                }

                WriteLinesToFile writeImport = new()
                {
                    BuildEngine = BuildEngine,
                    TaskEnvironment = TaskEnvironment,
                    File = ImportFile,
                    Lines = [new TaskItem(EscapingUtilities.Escape(contents.ToString()))],
                    Overwrite = true,
                    WriteOnlyWhenDifferent = true,
                };

                return writeImport.Execute();
            }
            catch (Exception e) when (ExceptionHandling.IsIoRelatedException(e))
            {
                Log.LogErrorWithCodeFromResources(
                    "ReadLinesFromFile.ErrorOrWarning",
                    cleanFilePath.OriginalValue,
                    e.Message);
                return false;
            }
            catch (XmlException e)
            {
                Log.LogErrorWithCodeFromResources(
                    "WriteLinesToFile.ErrorOrWarning",
                    importFilePath.OriginalValue,
                    e.Message,
                    string.Empty);
                return false;
            }
        }
    }
}
