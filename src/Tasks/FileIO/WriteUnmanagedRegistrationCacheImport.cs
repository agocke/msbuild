// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
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
    /// Writes the presence of the unmanaged registration cache as an imported MSBuild property.
    /// </summary>
    [MSBuildDeclaredIOTask]
    [MSBuildMultiThreadableTask]
    public sealed class WriteUnmanagedRegistrationCacheImport : TaskExtension, IMultiThreadableTask
    {
        private const string MSBuildXmlNamespace = "http://schemas.microsoft.com/developer/msbuild/2003";

        /// <inheritdoc />
        public TaskEnvironment TaskEnvironment { get; set; } = TaskEnvironment.Fallback;

        /// <summary>
        /// The unmanaged registration cache whose presence should be recorded.
        /// </summary>
        [Required]
        public ITaskItem CacheFile { get; set; }

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
            ArgumentNullException.ThrowIfNull(CacheFile);
            ArgumentNullException.ThrowIfNull(ImportFile);

            AbsolutePath cacheFilePath = TaskEnvironment.GetAbsolutePath(CacheFile.ItemSpec);
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

                if (FileSystems.Default.FileExists(cacheFilePath))
                {
                    writer.WriteStartElement("PropertyGroup", MSBuildXmlNamespace);
                    writer.WriteElementString("_HardenedUnmanagedRegistrationCacheExists", MSBuildXmlNamespace, "true");
                    writer.WriteEndElement();
                }

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
    }
}
