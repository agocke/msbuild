// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;

#nullable enable

namespace Microsoft.Build.Execution;

// Compiled only into the isolated benchmark engine, never into the product build.
public static unsafe class RestoreBenchmarkEvaluation
{
    private const int ProtocolVersion = 1;
    private static readonly object s_initializeLock = new();
    private static ProjectCollection? s_nativeCollection;
    private static EvaluationContext? s_nativeContext;
    private static delegate* unmanaged[Cdecl]<byte*, int, byte**, int*, int> s_evaluate;
    private static delegate* unmanaged[Cdecl]<byte*, void> s_free;
    private static string? s_transportMode;
    private static long s_requests;
    private static long s_bytes;
    private static long s_evaluateMicroseconds;
    private static long s_serializeMicroseconds;
    private static long s_transferMicroseconds;
    private static long s_deserializeMicroseconds;

    [ThreadStatic]
    private static bool t_bypass;

    internal static bool TryLoad(string file, ProjectOptions options, out ProjectInstance? instance)
    {
        instance = null;
        if (!RuntimeFeature.IsDynamicCodeSupported || t_bypass)
        {
            return false;
        }

        string? mode = Environment.GetEnvironmentVariable("MSBUILD_RESTORE_EVAL_MODE");
        if (string.IsNullOrEmpty(mode) || mode == "managed")
        {
            return false;
        }

        if (mode is not ("native" or "roundtrip"))
        {
            throw new InvalidOperationException($"Unknown MSBUILD_RESTORE_EVAL_MODE '{mode}'.");
        }

        if (options.EvaluationStage != ProjectEvaluationStage.Full || options.DirectoryCacheFactory is not null)
        {
            throw new NotSupportedException("The restore evaluation bridge requires full evaluation and no custom directory cache.");
        }

        InitializeTransport(mode);
        string sdkDirectory = Environment.GetEnvironmentVariable("MSBUILD_RESTORE_EVAL_SDK_DIRECTORY")
            ?? throw new InvalidOperationException("MSBUILD_RESTORE_EVAL_SDK_DIRECTORY must name the benchmark SDK directory.");
        byte[] request;
        using (MemoryStream stream = new())
        {
            using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(ProtocolVersion);
            writer.Write(sdkDirectory);
            writer.Write(Path.GetFullPath(file));
            writer.Write(options.ToolsVersion ?? "");
            writer.Write(options.SubToolsetVersion ?? "");
            writer.Write((int)options.LoadSettings);
            writer.Write(options.Interactive);
            writer.Write(options.GlobalProperties?.Count ?? 0);
            if (options.GlobalProperties is not null)
            {
                foreach ((string key, string value) in options.GlobalProperties)
                {
                    writer.Write(key);
                    writer.Write(value);
                }
            }

            request = stream.ToArray();
        }

        long transferStart = Stopwatch.GetTimestamp();
        byte[] response;
        if (mode == "roundtrip")
        {
            t_bypass = true;
            try
            {
                response = EvaluateRequest(request);
            }
            finally
            {
                t_bypass = false;
            }
        }
        else
        {
            byte* buffer = null;
            int length = 0;
            fixed (byte* input = request)
            {
                int status = s_evaluate(input, request.Length, &buffer, &length);
                try
                {
                    if (buffer == null || length <= 0 || status is not (0 or 1))
                    {
                        throw new InvalidOperationException($"Native evaluation transport failed (status {status}).");
                    }

                    response = new ReadOnlySpan<byte>(buffer, length).ToArray();
                }
                finally
                {
                    if (buffer != null)
                    {
                        s_free(buffer);
                    }
                }
            }
        }

        long transferMicroseconds = MicrosecondsSince(transferStart);
        long deserializeStart = Stopwatch.GetTimestamp();
        using MemoryStream reply = new(response, writable: false);
        using BinaryReader reader = new(reply, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBoolean())
        {
            throw new InvalidOperationException($"Native AOT evaluation failed for '{file}':\n{reader.ReadString()}");
        }

        long evaluationMicroseconds = reader.ReadInt64();
        long serializationMicroseconds = reader.ReadInt64();
        using (ITranslator translator = BinaryTranslator.GetReadTranslator(reply, new ReaderFactory()))
        {
            instance = ProjectInstance.FactoryForDeserialization(translator);
        }

        if (!instance.TranslateEntireState)
        {
            throw new InvalidDataException("The evaluation bridge returned incomplete project state.");
        }

        ProjectCollection collection = options.ProjectCollection ?? ProjectCollection.GlobalProjectCollection;
        instance.LateInitialize(collection.ProjectRootElementCache, collection.HostServices);
        Interlocked.Increment(ref s_requests);
        Interlocked.Add(ref s_bytes, response.Length);
        Interlocked.Add(ref s_evaluateMicroseconds, evaluationMicroseconds);
        Interlocked.Add(ref s_serializeMicroseconds, serializationMicroseconds);
        Interlocked.Add(ref s_transferMicroseconds, transferMicroseconds - evaluationMicroseconds - serializationMicroseconds);
        Interlocked.Add(ref s_deserializeMicroseconds, MicrosecondsSince(deserializeStart));
        return true;
    }

    public static byte[] EvaluateRequest(byte[] request)
    {
        using MemoryStream input = new(request, writable: false);
        using BinaryReader reader = new(input);
        if (reader.ReadInt32() != ProtocolVersion)
        {
            throw new InvalidDataException("Evaluation bridge protocol mismatch.");
        }

        string sdkDirectory = reader.ReadString();
        string file = reader.ReadString();
        string toolsVersion = reader.ReadString();
        string subToolsetVersion = reader.ReadString();
        ProjectLoadSettings loadSettings = (ProjectLoadSettings)reader.ReadInt32();
        bool interactive = reader.ReadBoolean();
        int count = reader.ReadInt32();
        Dictionary<string, string> properties = new(count, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < count; i++)
        {
            properties.Add(reader.ReadString(), reader.ReadString());
        }

        lock (s_initializeLock)
        {
            if (s_nativeCollection is null)
            {
                AppContext.SetData("Microsoft.DotNet.Sdk.Root", sdkDirectory);
                s_nativeCollection = new ProjectCollection();
                s_nativeCollection.IsBuildEnabled = false;
                s_nativeContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
            }
        }

        long start = Stopwatch.GetTimestamp();
        ProjectInstance project = ProjectInstance.FromFile(file, new ProjectOptions
        {
            GlobalProperties = properties,
            ToolsVersion = toolsVersion.Length == 0 ? null : toolsVersion,
            SubToolsetVersion = subToolsetVersion.Length == 0 ? null : subToolsetVersion,
            ProjectCollection = s_nativeCollection,
            EvaluationContext = s_nativeContext,
            LoadSettings = loadSettings,
            Interactive = interactive,
            EvaluationStage = ProjectEvaluationStage.Full,
        });
        long evaluationMicroseconds = MicrosecondsSince(start);
        project.TranslateEntireState = true;
        using MemoryStream state = new();
        start = Stopwatch.GetTimestamp();
        using (ITranslator translator = BinaryTranslator.GetWriteTranslator(state))
        {
            ((ITranslatable)project).Translate(translator);
        }

        byte[] payload = state.ToArray();
        long serializationMicroseconds = MicrosecondsSince(start);
        using MemoryStream output = new();
        using BinaryWriter writer = new(output, Encoding.UTF8, leaveOpen: true);
        writer.Write(true);
        writer.Write(evaluationMicroseconds);
        writer.Write(serializationMicroseconds);
        writer.Write(payload);
        return output.ToArray();
    }

    private static void InitializeTransport(string mode)
    {
        lock (s_initializeLock)
        {
            if (s_transportMode is not null)
            {
                if (mode != s_transportMode)
                {
                    throw new InvalidOperationException("Evaluation modes cannot change within a process.");
                }

                return;
            }

            if (mode == "native")
            {
                string library = Environment.GetEnvironmentVariable("MSBUILD_RESTORE_EVAL_LIBRARY")
                    ?? throw new InvalidOperationException("MSBUILD_RESTORE_EVAL_LIBRARY must name the native evaluation library.");
                string expectedHash = File.ReadAllText(library + ".engine.sha256").Trim();
                string actualHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ProjectInstance).Assembly.Location)));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The native and managed engines do not match. Rebuild the benchmark host.");
                }

                nint handle = NativeLibrary.Load(library);
                s_evaluate = (delegate* unmanaged[Cdecl]<byte*, int, byte**, int*, int>)NativeLibrary.GetExport(handle, "restore_eval");
                s_free = (delegate* unmanaged[Cdecl]<byte*, void>)NativeLibrary.GetExport(handle, "restore_eval_free");
            }

            AppDomain.CurrentDomain.ProcessExit += (_, _) => WriteSummary();
            s_transportMode = mode;
        }
    }

    private static void WriteSummary()
    {
        string? prefix = Environment.GetEnvironmentVariable("MSBUILD_RESTORE_EVAL_SUMMARY");
        if (string.IsNullOrEmpty(prefix))
        {
            return;
        }

        string path = $"{prefix}.{Environment.ProcessId}.tsv";
        using StreamWriter writer = new(path);
        writer.WriteLine("requests\tstate_bytes\tevaluation_us_sum\tserialization_us_sum\ttransfer_us_sum\tdeserialization_us_sum");
        writer.WriteLine(string.Join("\t", [
            s_requests.ToString(CultureInfo.InvariantCulture),
            s_bytes.ToString(CultureInfo.InvariantCulture),
            s_evaluateMicroseconds.ToString(CultureInfo.InvariantCulture),
            s_serializeMicroseconds.ToString(CultureInfo.InvariantCulture),
            s_transferMicroseconds.ToString(CultureInfo.InvariantCulture),
            s_deserializeMicroseconds.ToString(CultureInfo.InvariantCulture),
        ]));
    }

    private static long MicrosecondsSince(long start) =>
        (long)(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 1000);

    private sealed class ReaderFactory : BinaryReaderFactory
    {
        public override BinaryReader Create(Stream stream) => new(stream);
    }
}
