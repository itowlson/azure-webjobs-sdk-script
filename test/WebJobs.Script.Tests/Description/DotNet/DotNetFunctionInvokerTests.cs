﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Script.Binding;
using Microsoft.Azure.WebJobs.Script.Description;
using Microsoft.Azure.WebJobs.Script.Diagnostics;
using Microsoft.Azure.WebJobs.Script.Tests.Properties;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Scripting;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Azure.WebJobs.Script.Tests.Description.DotNet
{
    public class DotNetFunctionInvokerTests
    {
        [Fact]
        public async Task ReloadScript_WithInvalidCompilationAndMissingMethod_ReportsResults()
        {
            // Create the compilation exception we expect to throw during the reload
            var descriptor = new DiagnosticDescriptor(DotNetConstants.MissingFunctionEntryPointCompilationCode, 
                "Test compilation exception", "Test compilation error", "AzureFunctions", DiagnosticSeverity.Error, true);
            var exception = new CompilationErrorException("Test compilation exception", ImmutableArray.Create(Diagnostic.Create(descriptor, Location.None)));

            // Create the invoker dependencies and setup the appropriate method to throw the exception
            RunDependencies dependencies = CreateDependencies();
            dependencies.Compilation.Setup(c => c.GetEntryPointSignature(It.IsAny<IFunctionEntryPointResolver>()))
             .Throws(exception);

            string rootFunctionsFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(rootFunctionsFolder);

            // Create a dummy file to represent our function
            string filePath = Path.Combine(rootFunctionsFolder, Guid.NewGuid().ToString() + ".csx");
            File.WriteAllText(filePath, string.Empty);

            var metadata = new FunctionMetadata
            {
                ScriptFile = filePath,
                Name = Guid.NewGuid().ToString(),
                ScriptType = ScriptType.CSharp
            };

            metadata.Bindings.Add(new BindingMetadata() { Name = "Test", Type = "ManualTrigger" });

            var invoker = new DotNetFunctionInvoker(dependencies.Host.Object, metadata, new Collection<Script.Binding.FunctionBinding>(),
                new Collection<Script.Binding.FunctionBinding>(), dependencies.EntrypointResolver.Object, new FunctionAssemblyLoader(string.Empty), 
                dependencies.CompilationServiceFactory.Object, dependencies.TraceWriterFactory.Object);

            // Update the file to trigger a reload
            File.WriteAllText(filePath, string.Empty);

            CompilationErrorException resultException = await Assert.ThrowsAsync<CompilationErrorException>(() => invoker.GetFunctionTargetAsync());

            await TestHelpers.Await(() =>
            {
                return dependencies.TraceWriter.Traces.Any(t => t.Message.Contains("Compilation failed.")) &&
                 dependencies.TraceWriter.Traces.Any(t => t.Message.Contains(DotNetConstants.MissingFunctionEntryPointCompilationCode));
            });
        }

        [Fact]
        public async Task Compilation_WithMissingBindingArguments_LogsAF004Warning()
        {
            // Create the compilation exception we expect to throw during the reload           
            string rootFunctionsFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(rootFunctionsFolder);
            
            // Create the invoker dependencies and setup the appropriate method to throw the exception
            RunDependencies dependencies = CreateDependencies();
                
            // Create a dummy file to represent our function
            string filePath = Path.Combine(rootFunctionsFolder, Guid.NewGuid().ToString() + ".csx");
            File.WriteAllText(filePath, Resources.TestFunctionWithMissingBindingArgumentsCode);

            var metadata = new FunctionMetadata
            {
                ScriptFile = filePath,
                Name = Guid.NewGuid().ToString(),
                ScriptType = ScriptType.CSharp
            };

            metadata.Bindings.Add(new BindingMetadata() { Name = "myQueueItem", Type = "ManualTrigger" });

            var testBinding = new Mock<FunctionBinding>(null, new BindingMetadata() { Name = "TestBinding", Type = "blob" }, FileAccess.Write);
            
            var invoker = new DotNetFunctionInvoker(dependencies.Host.Object, metadata, new Collection<FunctionBinding>(),
                new Collection<FunctionBinding> { testBinding.Object }, new FunctionEntryPointResolver(), new FunctionAssemblyLoader(string.Empty),
                new DotNetCompilationServiceFactory(), dependencies.TraceWriterFactory.Object);

            await invoker.GetFunctionTargetAsync();

            Assert.Contains(dependencies.TraceWriter.Traces,
                t => t.Message.Contains($"warning {DotNetConstants.MissingBindingArgumentCompilationCode}") && t.Message.Contains("'TestBinding'"));
        }

        [Fact]
        public async Task Compilation_OnSecondaryHost_SuppressesLogs()
        {
            // Create the compilation exception we expect to throw during the reload           
            string rootFunctionsFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(rootFunctionsFolder);

            // Create the invoker dependencies and setup the appropriate method to throw the exception
            RunDependencies dependencies = CreateDependencies();
            
            // Set the host to secondary
            dependencies.Host.SetupGet(h => h.IsPrimary).Returns(false);

            // Create a dummy file to represent our function
            string filePath = Path.Combine(rootFunctionsFolder, Guid.NewGuid().ToString() + ".csx");
            File.WriteAllText(filePath, Resources.TestFunctionWithMissingBindingArgumentsCode);

            var metadata = new FunctionMetadata
            {
                ScriptFile = filePath,
                Name = Guid.NewGuid().ToString(),
                ScriptType = ScriptType.CSharp
            };

            metadata.Bindings.Add(new BindingMetadata() { Name = "myQueueItem", Type = "ManualTrigger" });

            var testBinding = new Mock<FunctionBinding>(null, new BindingMetadata() { Name = "TestBinding", Type = "blob" }, FileAccess.Write);

            var invoker = new DotNetFunctionInvoker(dependencies.Host.Object, metadata, new Collection<FunctionBinding>(),
                new Collection<FunctionBinding> { testBinding.Object }, new FunctionEntryPointResolver(), new FunctionAssemblyLoader(string.Empty),
                new DotNetCompilationServiceFactory(), dependencies.TraceWriterFactory.Object);

            await invoker.GetFunctionTargetAsync();

            // Verify that logs on the second instance were suppressed
            int count = dependencies.TraceWriter.Traces.Count();
            Assert.Equal(0, count);
        }

        [Fact]
        public void ValidateFunctionBindingArguments_ReturnBinding_Succeeds()
        {
            Collection<FunctionParameter> parameters = new Collection<FunctionParameter>()
            {
                new FunctionParameter("input", "String", false, RefKind.None)
            };
            FunctionSignature signature = new FunctionSignature("Test", "Test", ImmutableArray.CreateRange<FunctionParameter>(parameters), "Test", false);

            Collection<FunctionBinding> inputBindings = new Collection<FunctionBinding>()
            {
                TestHelpers.CreateTestBinding(new JObject
                {
                    { "type", "blobTrigger" },
                    { "name", "input" },
                    { "direction", "in" },
                    { "path", "test" }
                })
            };
            Collection<FunctionBinding> outputBindings = new Collection<FunctionBinding>()
            {
                TestHelpers.CreateTestBinding(new JObject
                {
                    { "type", "blob" },
                    { "name", ScriptConstants.SystemReturnParameterBindingName },
                    { "direction", "out" },
                    { "path", "test/test" }
                })
            };
            var diagnostics = DotNetFunctionInvoker.ValidateFunctionBindingArguments(signature, "input", inputBindings, outputBindings);
            Assert.Equal(0, diagnostics.Count());
        }

        private RunDependencies CreateDependencies(TraceLevel traceLevel = TraceLevel.Info)
        {
            var dependencies = new RunDependencies();

            var traceWriter = new TestTraceWriter(System.Diagnostics.TraceLevel.Verbose);
            var scriptHostConfiguration = new ScriptHostConfiguration
            {
                HostConfig = new JobHostConfiguration(),
                TraceWriter = traceWriter,
                FileLoggingMode = FileLoggingMode.Always,
                FileWatchingEnabled = true
            };

            scriptHostConfiguration.HostConfig.Tracing.ConsoleLevel = System.Diagnostics.TraceLevel.Verbose;

            var host = new Mock<ScriptHost>(scriptHostConfiguration);
            host.SetupGet(h => h.IsPrimary).Returns(true);

            var entrypointResolver = new Mock<IFunctionEntryPointResolver>();

            var compilation = new Mock<ICompilation>();
            compilation.Setup(c => c.GetDiagnostics())
                .Returns(ImmutableArray<Diagnostic>.Empty);

            var compilationService = new Mock<ICompilationService>();
            compilationService.Setup(s => s.SupportedFileTypes)
                .Returns(() => new[] { ".csx" });
            compilationService.Setup(s => s.GetFunctionCompilation(It.IsAny<FunctionMetadata>()))
                .Returns(compilation.Object);

            var compilationServiceFactory = new Mock<ICompilationServiceFactory>();
            compilationServiceFactory.Setup(f => f.CreateService(ScriptType.CSharp, It.IsAny<IFunctionMetadataResolver>()))
                .Returns(compilationService.Object);

            var traceWriterFactory = new Mock<ITraceWriterFactory>();
            traceWriterFactory.Setup(f => f.Create())
                .Returns(traceWriter);

            var metricsLogger = new MetricsLogger();
            scriptHostConfiguration.HostConfig.AddService<IMetricsLogger>(metricsLogger);

            return new RunDependencies
            {
                Host = host,
                EntrypointResolver = entrypointResolver,
                Compilation = compilation,
                CompilationService = compilationService,
                CompilationServiceFactory = compilationServiceFactory,
                TraceWriterFactory = traceWriterFactory,
                TraceWriter = traceWriter
            };
        }

        private class RunDependencies
        {
            public Mock<ScriptHost> Host { get; set; }
            public Mock<IFunctionEntryPointResolver> EntrypointResolver { get; set; }
            public Mock<ICompilation> Compilation { get; set; }
            public Mock<ICompilationService> CompilationService { get; set; }
            public Mock<ICompilationServiceFactory> CompilationServiceFactory { get; set; }
            public Mock<ITraceWriterFactory> TraceWriterFactory { get; set; }
            public TestTraceWriter TraceWriter { get; set; }
        }
    }
}
