using JasperFx.Core.TypeScanning;
using Shouldly;

namespace CoreTests.TypeScanning;

public class AssemblyFinderTests
{
    [Fact]
    public void find_assemblies_in_dependency_order()
    {
        var widgets =
            AssemblyFinder.FindAssemblies(a => a.GetName().Name.StartsWith("Widget"));

        var names = widgets.Select(x => x.GetName().Name).ToArray();

        var index3 = Array.IndexOf(names, "Widgets3");
        var index5 = Array.IndexOf(names, "Widgets5");

        index3.ShouldBeGreaterThan(index5);
    }

    // jasperfx#1143: the skip compared the full path against "/runtimes" and "/native", so it never
    // matched the '\\'-separated paths Directory.EnumerateFiles returns on Windows.
    [Theory]
    [InlineData(@"runtimes\win-x64\native\covrun64.dll")]
    [InlineData(@"runtimes\win\lib\net9.0\System.Something.dll")]
    [InlineData(@"native\msdia140.dll")]
    [InlineData("runtimes/linux-x64/native/libcovrun64.so.dll")]
    [InlineData("Runtimes/osx-arm64/lib/net9.0/Foo.dll")]
    [InlineData(@"de\App.resources.dll")]
    [InlineData("zh-Hans/Microsoft.CodeAnalysis.resources.dll")]
    public void skips_runtimes_native_and_satellite_files_with_either_separator(string relativePath)
    {
        AssemblyFinder.ShouldSkipFile(relativePath).ShouldBeTrue();
    }

    [Theory]
    [InlineData("App.dll")]
    [InlineData("native.dll")]
    [InlineData("runtimes.dll")]
    [InlineData(@"plugins\NativeInterop.dll")]
    [InlineData("runtimes-config/App.Plugin.dll")]
    [InlineData("Company.Resources.dll")]
    [InlineData("App.resources.dll")]
    public void keeps_ordinary_assemblies(string relativePath)
    {
        AssemblyFinder.ShouldSkipFile(relativePath).ShouldBeFalse();
    }

    [Fact]
    public void the_scan_never_tries_to_load_runtimes_native_or_satellite_files()
    {
        // The scanned folder itself sits under one named "native-app", which the old full-path
        // check also matched, so it skipped every file in it.
        var root = Path.Combine(Path.GetTempPath(), "jasperfx-1143-" + Guid.NewGuid().ToString("N"),
            "native-app", "bin");

        string writeJunk(params string[] segments)
        {
            var file = Path.Combine([root, ..segments]);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(file, [0x4D, 0x5A, 0x00, 0x01]);
            return file;
        }

        try
        {
            writeJunk("runtimes", "win-x64", "native", "covrun64_1143.dll");
            writeJunk("native", "msdia140_1143.dll");
            writeJunk("de", "NotAnAssembly1143.resources.dll");
            var visible = writeJunk("NotAnAssembly1143.dll");

            var failures = new List<string>();
            AssemblyFinder.FindAssemblies(_ => true, root, failures.Add, false).ToArray();

            failures.ShouldBe([visible]);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(root))!, true);
        }
    }
}
