using Xunit;

// One test at a time, so each stress test has the machine to itself and never starves another test of
// thread-pool threads. This is the whole point of the project: see StressTests.csproj.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
