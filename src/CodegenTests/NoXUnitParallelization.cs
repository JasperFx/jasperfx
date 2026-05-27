using Xunit;

// CodegenTests mutate process-wide static state (e.g. DynamicCodeBuilder.WithinCodegenCommand) and
// compile assemblies, so they must not run in parallel across collections. Matches CommandLineTests
// and the CI convention of running these suites serially.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
