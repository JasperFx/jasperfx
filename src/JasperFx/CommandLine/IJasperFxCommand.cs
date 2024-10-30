using JasperFx.CommandLine.Help;

namespace JasperFx.CommandLine;

public interface IJasperFxCommand
{
    Type InputType { get; }
    UsageGraph Usages { get; }
    Task<bool> Execute(object input);
}

public interface IJasperFxCommand<T> : IJasperFxCommand
{
}