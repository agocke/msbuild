using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.Build.Framework;
using BuildTask = Microsoft.Build.Utilities.Task;

internal static class Program
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, "NuGet.Build.Tasks.Console.Program", "NuGet.Build.Tasks.Console")]
    private static int Main(string[] args)
    {
        // RestoreTaskEx expects to launch "dotnet <console dll> ...", so the console path comes first.
        if (args.Length > 0 && args[0].EndsWith(".Console.dll", StringComparison.OrdinalIgnoreCase))
        {
            args = args[1..];
        }

        // Every task static-graph restore runs for dotnet/runtime; anything else fails observably (MSB4283).
        Microsoft.Build.Tasks.BuiltInTasks.RegisterAll();
        BuildTask.RegisterTask<Microsoft.Build.Tasks.GetReferenceAssemblyPaths>();
        BuildTask.RegisterTask<NuGet.Build.Tasks.CheckForDuplicateNuGetItemsTask>();
        BuildTask.RegisterTask<Microsoft.NET.Build.Tasks.GetPackagesToPrune>();
        BuildTask.RegisterTask<Microsoft.NET.Build.Tasks.CheckForImplicitPackageReferenceOverrides>();
        BuildTask.RegisterTask<Microsoft.NET.Build.Tasks.ApplyImplicitVersions>();
        BuildTask.RegisterTask<Microsoft.NET.Build.Tasks.CheckIfPackageReferenceShouldBeFrameworkReference>();
        BuildTask.RegisterTask<Microsoft.NET.Build.Tasks.ResolveAppHosts>();
        BuildTask.RegisterTask<Microsoft.NET.Build.Tasks.ProcessFrameworkReferences>();
        BuildTask.RegisterTask<Microsoft.NET.Build.Tasks.CheckForDuplicateFrameworkReferences>();
        BuildTask.RegisterTask<Microsoft.NET.Build.Tasks.CreateWindowsSdkKnownFrameworkReferences>();

        // The SDK's disk-discovered resolvers, registered in-process instead of loaded by path.
        SdkResolver.Register(new Microsoft.Build.NuGetSdkResolver.NuGetSdkResolver());
        SdkResolver.Register(new Microsoft.NET.Sdk.WorkloadMSBuildSdkResolver.WorkloadSdkResolver());

        Type program = Type.GetType("NuGet.Build.Tasks.Console.Program, NuGet.Build.Tasks.Console", throwOnError: true)!;
        MethodInfo main = program.GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!;
        return ((Task<int>)main.Invoke(null, [args])!).GetAwaiter().GetResult();
    }
}
