using JasperFx.CommandLine.Help;

namespace JasperFx.CommandLine;

public interface IOaktonCommand
{
    Type InputType { get; }
    UsageGraph Usages { get; }
    Task<bool> Execute(object input);
}

public interface IOaktonCommand<T> : IOaktonCommand
{
}