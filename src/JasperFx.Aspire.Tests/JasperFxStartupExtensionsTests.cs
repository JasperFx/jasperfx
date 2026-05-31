using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using JasperFx.Aspire;
using Shouldly;

namespace JasperFx.Aspire.Tests;

public class JasperFxStartupExtensionsTests
{
    // AddProject(name, path) validates the file exists, so point at a real (throwaway) csproj.
    private static readonly string ProjectPath = CreateTempProject();

    private static string CreateTempProject()
    {
        var dir = Path.Combine(Path.GetTempPath(), "jasperfx-aspire-tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Api.csproj");
        if (!File.Exists(path))
        {
            File.WriteAllText(path,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        }

        return path;
    }

    private static IResourceBuilder<ProjectResource> Parent(out IDistributedApplicationBuilder builder)
    {
        builder = DistributedApplication.CreateBuilder([]);
        return builder.AddProject("api", ProjectPath);
    }

    private static IResource Gate(IDistributedApplicationBuilder builder, string name)
        => builder.Resources.Single(r => r.Name == name);

    private static bool WaitsForCompletionOn(IResource waiter, IResource awaited)
        => waiter.Annotations.OfType<WaitAnnotation>()
            .Any(w => ReferenceEquals(w.Resource, awaited) && w.WaitType == WaitType.WaitForCompletion);

    [Fact]
    public void creates_a_gate_resource_named_after_parent_verb_and_argument()
    {
        var api = Parent(out var builder);

        api.WithJasperFxStartup("resources", "setup");

        builder.Resources.ShouldContain(r => r.Name == "api-resources-setup");
    }

    [Fact]
    public void the_parent_waits_for_completion_of_the_gate()
    {
        var api = Parent(out var builder);

        api.WithJasperFxStartup("resources", "setup");

        var gate = Gate(builder, "api-resources-setup");
        WaitsForCompletionOn(api.Resource, gate).ShouldBeTrue();
    }

    [Fact]
    public void the_gate_points_at_the_same_project_as_the_parent()
    {
        var api = Parent(out var builder);

        api.WithJasperFxStartup("resources", "setup");

        var gate = Gate(builder, "api-resources-setup");
        gate.TryGetLastAnnotation<IProjectMetadata>(out var metadata).ShouldBeTrue();
        metadata!.ProjectPath.ShouldBe(ProjectPath);
    }

    [Fact]
    public void the_gate_clones_the_parents_environment_references()
    {
        var api = Parent(out var builder);
        api.WithEnvironment("MARKER", "from-parent");

        // capture the parent's reference annotations as they stand before the gate is wired
        var parentEnv = api.Resource.Annotations.OfType<EnvironmentCallbackAnnotation>().ToArray();

        api.WithJasperFxStartup("resources", "setup");

        var gate = Gate(builder, "api-resources-setup");
        var gateEnv = gate.Annotations.OfType<EnvironmentCallbackAnnotation>().ToArray();
        foreach (var annotation in parentEnv)
        {
            gateEnv.ShouldContain(annotation);
        }
    }

    [Fact]
    public void gates_run_sequentially_in_declaration_order_by_default()
    {
        var api = Parent(out var builder);

        api.WithJasperFxStartup(c =>
        {
            c.Run("resources", "setup");
            c.Run("codegen", "write");
        });

        var first = Gate(builder, "api-resources-setup");
        var second = Gate(builder, "api-codegen-write");

        // the second gate waits for the first
        WaitsForCompletionOn(second, first).ShouldBeTrue();
        // and the parent waits for both
        WaitsForCompletionOn(api.Resource, first).ShouldBeTrue();
        WaitsForCompletionOn(api.Resource, second).ShouldBeTrue();
    }

    [Fact]
    public void a_parallel_gate_does_not_chain_after_the_previous_gate()
    {
        var api = Parent(out var builder);

        api.WithJasperFxStartup(c =>
        {
            c.Run("resources", "setup");
            c.Run("codegen", "write", g => g.Parallel = true);
        });

        var first = Gate(builder, "api-resources-setup");
        var parallel = Gate(builder, "api-codegen-write");

        // the parallel gate is NOT chained after the first
        WaitsForCompletionOn(parallel, first).ShouldBeFalse();
        // but the parent still waits for it
        WaitsForCompletionOn(api.Resource, parallel).ShouldBeTrue();
    }

    [Fact]
    public void run_when_false_skips_the_gate_entirely()
    {
        var api = Parent(out var builder);

        api.WithJasperFxStartup("resources", "setup", gate: g => g.RunWhen = _ => false);

        builder.Resources.ShouldNotContain(r => r.Name == "api-resources-setup");
    }

    [Fact]
    public void an_advisory_gate_runs_but_does_not_block_startup()
    {
        var api = Parent(out var builder);

        api.WithJasperFxStartup("check-env", gate: g => g.BlockOnFailure = false);

        var gate = Gate(builder, "api-check-env");           // it still exists / runs
        WaitsForCompletionOn(api.Resource, gate).ShouldBeFalse(); // but the parent doesn't wait on it
    }

    [Fact]
    public void custom_resource_name_is_honored()
    {
        var api = Parent(out var builder);

        api.WithJasperFxStartup("resources", "setup", gate: g => g.ResourceName = "provision-db");

        builder.Resources.ShouldContain(r => r.Name == "provision-db");
    }

    [Fact]
    public void check_helper_adds_a_blocking_check_env_gate()
    {
        var api = Parent(out var builder);

        api.WithJasperFxStartup(c => c.Check());

        var gate = Gate(builder, "api-check-env");
        WaitsForCompletionOn(api.Resource, gate).ShouldBeTrue();
    }
}
