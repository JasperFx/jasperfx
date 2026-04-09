using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Descriptions;
using JasperFx.CommandLine.Internal.Conversion;
using JasperFx.CommandLine.Parsing;
using JasperFx.Resources;

namespace CommandLineBenchmarks;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ParsingBenchmarks
{
    private CommandFactory _factory = null!;

    [GlobalSetup]
    public void Setup()
    {
        _factory = new CommandFactory();
        _factory.RegisterCommand<DescribeCommand>();
        _factory.RegisterCommand<ResourcesCommand>();
    }

    // --- Handler Building: Reflection vs Generated ---

    [Benchmark(Description = "Reflection: GetHandlers(DescribeInput)")]
    public List<ITokenHandler> Reflection_BuildHandlers_DescribeInput()
    {
        return InputParser.GetHandlers(typeof(DescribeInput));
    }

    [Benchmark(Description = "Generated: TryGetHandlers(DescribeInput)")]
    public List<ITokenHandler>? Generated_BuildHandlers_DescribeInput()
    {
        return GeneratedParserRegistry.TryGetHandlers(typeof(DescribeInput));
    }

    [Benchmark(Description = "Reflection: GetHandlers(ResourceInput)")]
    public List<ITokenHandler> Reflection_BuildHandlers_ResourceInput()
    {
        return InputParser.GetHandlers(typeof(ResourceInput));
    }

    [Benchmark(Description = "Generated: TryGetHandlers(ResourceInput)")]
    public List<ITokenHandler>? Generated_BuildHandlers_ResourceInput()
    {
        return GeneratedParserRegistry.TryGetHandlers(typeof(ResourceInput));
    }

    // --- Full Parse Pipeline (uses generated parsers via UsageGraph) ---

    [Benchmark(Description = "Full Parse: describe --file out.txt")]
    public CommandRun ParseSimpleCommand()
    {
        return _factory.BuildRun("describe --file out.txt");
    }

    [Benchmark(Description = "Full Parse: describe (complex flags)")]
    public CommandRun ParseComplexFlags()
    {
        return _factory.BuildRun("describe --file out.txt --environment Testing --verbose --log-level Debug");
    }

    [Benchmark(Description = "Full Parse: resources check (with filters)")]
    public CommandRun ParseResourceCommand()
    {
        return _factory.BuildRun("resources check --timeout 30 --type MyResource --name primary");
    }

    // --- Tokenization (unchanged, for reference) ---

    [Benchmark(Description = "Tokenize + Preprocess")]
    public List<string> TokenizeAndPreprocess()
    {
        var tokens = StringTokenizer.Tokenize("describe --file out.txt --environment=Testing --verbose -abc");
        return ArgPreprocessor.Process(tokens).ToList();
    }
}

public class Program
{
    public static void Main(string[] args)
    {
        BenchmarkRunner.Run<ParsingBenchmarks>();
    }
}
