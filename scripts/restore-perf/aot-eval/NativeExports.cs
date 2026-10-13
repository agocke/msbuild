// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;

internal static unsafe class NativeExports
{
    private static readonly object s_initializeLock = new();
    private static bool s_initialized;

    [UnmanagedCallersOnly(EntryPoint = "restore_eval", CallConvs = [typeof(CallConvCdecl)])]
    private static int Evaluate(byte* input, int length, byte** output, int* outputLength)
    {
        *output = null;
        *outputLength = 0;
        byte[] response;
        int status;
        // Exceptions cannot cross the native ABI; return the complete diagnostic, never fall back.
        try
        {
            byte[] request = new ReadOnlySpan<byte>(input, length).ToArray();
            lock (s_initializeLock)
            {
                if (!s_initialized)
                {
                    using BinaryReader reader = new(new MemoryStream(request, writable: false));
                    reader.ReadInt32();
                    AppContext.SetData("Microsoft.DotNet.Sdk.Root", reader.ReadString());
                    SdkResolver.Register(new CachedSdkResolver());
                    SdkResolver.Register(new Microsoft.NET.Sdk.WorkloadMSBuildSdkResolver.WorkloadSdkResolver());
                    s_initialized = true;
                }
            }

            response = RestoreBenchmarkEvaluation.EvaluateRequest(request);
            status = 0;
        }
        catch (Exception exception)
        {
            using MemoryStream stream = new();
            using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(false);
            writer.Write(exception.ToString());
            response = stream.ToArray();
            status = 1;
        }

        byte* buffer = (byte*)NativeMemory.Alloc((nuint)response.Length);
        response.CopyTo(new Span<byte>(buffer, response.Length));
        *output = buffer;
        *outputLength = response.Length;
        return status;
    }

    [UnmanagedCallersOnly(EntryPoint = "restore_eval_free", CallConvs = [typeof(CallConvCdecl)])]
    private static void Free(byte* buffer) => NativeMemory.Free(buffer);
}
