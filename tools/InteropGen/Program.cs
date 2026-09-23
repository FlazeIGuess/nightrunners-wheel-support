using System;
using System.Collections.Generic;
using System.IO;
using AsmResolver.DotNet;
using AssetRipper.Primitives;
using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using Il2CppInterop.Common;
using Il2CppInterop.Generator;
using Il2CppInterop.Generator.Runners;
using LibCpp2IL;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InteropGen
{
    // Offline interop generator: mirrors BepInEx.Unity.IL2CPP.Il2CppInteropManager (BE 788).
    // Produces the same interop assemblies BepInEx would generate at runtime -
    // deterministically identical, because same tool version + same GameAssembly/metadata.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Length < 5)
            {
                Console.Error.WriteLine("Usage: InteropGen <GameAssembly.dll> <global-metadata.dat> <unityVersion e.g. 2019.4.41> <unityBaseLibsDir> <outputDir>");
                return 2;
            }

            string gameAssemblyPath = args[0];
            string metadataPath = args[1];
            string unityVersionStr = args[2];
            string unityBaseLibsDir = args[3];
            string outputDir = args[4];

            foreach (var p in new[] { gameAssemblyPath, metadataPath })
            {
                if (!File.Exists(p)) { Console.Error.WriteLine($"Not found: {p}"); return 2; }
            }
            if (!Directory.Exists(unityBaseLibsDir)) { Console.Error.WriteLine($"unity-libs folder missing: {unityBaseLibsDir}"); return 2; }
            Directory.CreateDirectory(outputDir);
            foreach (var f in Directory.EnumerateFiles(outputDir, "*.dll")) File.Delete(f);

            UnityVersion version = UnityVersion.Parse(unityVersionStr);
            Console.WriteLine($"[InteropGen] Unity {version} | GameAssembly {gameAssemblyPath}");

            // 1) Register instruction sets + binary support (as in the manager cctor)
            InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_32);
            InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64);
            LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();

            // Mirror Cpp2IL logging to the console (compact)
            Cpp2IL.Core.Logging.Logger.WarningLog += (m, s) => Console.WriteLine($"[Cpp2IL/{s}] WARN {m.Trim()}");
            Cpp2IL.Core.Logging.Logger.ErrorLog += (m, s) => Console.WriteLine($"[Cpp2IL/{s}] ERR  {m.Trim()}");

            // 2) Cpp2IL: produce dummy assemblies
            Console.WriteLine("[InteropGen] Cpp2IL InitializeLibCpp2Il ...");
            Cpp2IlApi.InitializeLibCpp2Il(gameAssemblyPath, metadataPath, version);

            var layers = new List<Cpp2IlProcessingLayer> { new AttributeInjectorProcessingLayer() };
            foreach (var l in layers) l.PreProcess(Cpp2IlApi.CurrentAppContext, layers);
            foreach (var l in layers) l.Process(Cpp2IlApi.CurrentAppContext);

            Console.WriteLine("[InteropGen] BuildAssemblies (AsmResolverDllOutputFormatDefault) ...");
            List<AssemblyDefinition> sourceAssemblies = new AsmResolverDllOutputFormatDefault().BuildAssemblies(Cpp2IlApi.CurrentAppContext);
            Console.WriteLine($"[InteropGen] {sourceAssemblies.Count} dummy assemblies produced.");

            LibCpp2IlMain.Reset();
            Cpp2IlApi.CurrentAppContext = null;

            // 3) Il2CppInterop.Generator: write interop assemblies
            var options = new GeneratorOptions
            {
                GameAssemblyPath = gameAssemblyPath,
                Source = sourceAssemblies,
                OutputDir = outputDir,
                UnityBaseLibsDir = unityBaseLibsDir,
            };

            ILogger logger = new SimpleConsoleLogger();
            Console.WriteLine("[InteropGen] Generating interop assemblies ...");
            Il2CppInteropGenerator.Create(options)
                .AddLogger(logger)
                .AddInteropAssemblyGenerator()
                .Run();

            int dllCount = Directory.GetFiles(outputDir, "*.dll").Length;
            Console.WriteLine($"[InteropGen] DONE. {dllCount} interop DLLs in {outputDir}");
            return dllCount > 0 ? 0 : 1;
        }
    }

    internal sealed class SimpleConsoleLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            Console.WriteLine($"[Interop] {logLevel}: {formatter(state, exception)}");
            if (exception != null) Console.WriteLine(exception);
        }
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new NullScope(); public void Dispose() { } }
    }
}
