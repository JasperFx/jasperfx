using System.Reflection;
using JasperFx;
using JasperFx.Core.TypeScanning;
using Shouldly;
using Widgets1;
using Widgets5;
using RunnerFrame = xunit.v3.stackwalk.standin.RunnerFrame;

namespace CoreTests.TypeScanning;

public class CallingAssemblyTests
{
    [Fact]
    public void use_current_assembly()
    {
        CallingAssembly.Find()
            .ShouldBe(GetType().Assembly);
    }

    [Fact]
    public void from_another_assembly()
    {
        WidgetCallingAssemblyFinder.Calling()
            .ShouldBe(typeof(WidgetCallingAssemblyFinder).Assembly);
    }

    [Fact]
    public void skip_ignore_assembly()
    {
        // Widget4 assembly should be ignored
        Widget5CallingWidget4Caller.Calling()
            .ShouldBe(typeof(Widget5CallingWidget4Caller).Assembly);
    }

    // GH-600: AssemblyScanner.TheCallingAssembly() resolves through here, so a scan configured from an
    // async test could adopt the runner and then scan an assembly holding none of the suite's types --
    // the same defect fixed in JasperFxOptions.DetermineCallingAssembly, on the other stack walk.

    [Fact]
    public void walks_past_a_test_runner_frame_out_to_the_calling_assembly()
    {
        RunnerFrame.Invoke(CallingAssembly.Find)
            .ShouldBe(GetType().Assembly);
    }

    [Fact]
    public void never_adopts_a_test_runner_as_the_calling_assembly()
    {
        var assembly = RunnerFrame.Invoke(CallingAssembly.Find);

        assembly.ShouldNotBeNull();
        JasperFxOptions.IsTestRunnerAssembly(assembly.GetName().Name!).ShouldBeFalse();
    }

    [Fact]
    public void find_is_safe_to_call_concurrently()
    {
        // The old implementation cached failed Assembly.Load attempts in a plain static List<string> that
        // every caller mutated, so concurrent scans raced on it.
        var results = new Assembly?[64];

        Parallel.For(0, results.Length, i => results[i] = CallingAssembly.Find());

        results.ShouldAllBe(x => x != null);
    }
}