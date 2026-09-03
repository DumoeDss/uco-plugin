#nullable enable

using System.IO;
using System.Threading.Tasks;
using com.IvanMurzak.McpPlugin.Common.Model;
using com.IvanMurzak.ReflectorNet;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Version = com.IvanMurzak.McpPlugin.Common.Version;

namespace com.IvanMurzak.McpPlugin.Tests.Mcp
{
    /// <summary>
    /// Task 6.1: the shared builder registers the authoring safety policy as
    /// the outermost middleware of one pipeline instance that both the
    /// regular and the system manager use, and a host-supplied project path
    /// policy is the one the policy consults.
    /// </summary>
    public sealed class AuthoringBuilderWiringTests
    {
        [Fact]
        public void Build_RegistersSafetyAsOutermostMiddlewareOfOneSharedPipeline()
        {
            var recording = new RecordingMiddleware();
            var projectRoot = Path.Combine(Path.GetTempPath(), "mcp-wiring-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(projectRoot, "Assets"));
            var pathPolicy = new ProjectPathPolicy(projectRoot);
            var builder = new McpPluginBuilder(new Version());
            builder.WithProjectPathPolicy(pathPolicy)
                .AddToolExecutionMiddleware(recording);

            builder.Build(new Reflector());
            var services = builder.ServiceProvider!;

            var pipeline = services.GetRequiredService<ToolExecutionPipeline>();
            pipeline.Middleware[0].ShouldBeOfType<AuthoringSafetyMiddleware>();
            pipeline.Middleware[1].ShouldBeSameAs(recording);

            var toolManager = services.GetRequiredService<IToolManager>().ShouldBeOfType<McpToolManager>();
            var systemManager = services.GetRequiredService<McpSystemToolManager>();
            toolManager.ExecutionPipeline.ShouldBeSameAs(pipeline);
            systemManager.ExecutionPipeline.ShouldBeSameAs(pipeline);
            services.GetRequiredService<ISystemToolManager>().ShouldBeSameAs(systemManager);

            // The host-supplied path policy replaces the process-cwd default.
            var safety = (AuthoringSafetyMiddleware)pipeline.Middleware[0];
            safety.Policy.PathPolicy.ShouldBeSameAs(pathPolicy);
            safety.Policy.ShouldBeSameAs(services.GetRequiredService<AuthoringSafetyPolicy>());
            safety.Policy.PathPolicy!.ProjectRoot.ShouldBe(pathPolicy.ProjectRoot);
        }

        [Fact]
        public void Build_WithoutCustomMiddlewareStillHasSafetyThenPassThrough()
        {
            var builder = new McpPluginBuilder(new Version());
            builder.Build(new Reflector());
            var pipeline = builder.ServiceProvider!.GetRequiredService<ToolExecutionPipeline>();

            pipeline.Middleware.Count.ShouldBe(2);
            pipeline.Middleware[0].ShouldBeOfType<AuthoringSafetyMiddleware>();
            pipeline.Middleware[1].ShouldBeOfType<PassThroughToolExecutionMiddleware>();
        }

        private sealed class RecordingMiddleware : IToolExecutionMiddleware
        {
            public int Calls { get; private set; }

            public Task<ResponseCallTool> InvokeAsync(ToolCallContext context, ToolCallNext next)
            {
                Calls++;
                return next(context);
            }
        }
    }
}
