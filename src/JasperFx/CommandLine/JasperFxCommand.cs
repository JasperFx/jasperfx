using JasperFx.CommandLine.Help;

namespace JasperFx.CommandLine;

/// <summary>
///     Base class for all JasperFx commands
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract class JasperFxCommand<T> : IJasperFxCommand<T>
{
    protected JasperFxCommand()
    {
        Usages = new UsageGraph(GetType());
    }

    public UsageGraph Usages { get; }

    public Type InputType => typeof(T);

    Task<bool> IJasperFxCommand.Execute(object input)
    {
        return Task.FromResult(Execute((T)input));
    }

    /// <summary>
    ///     If your command has multiple argument usage patterns ala the Git command line, use
    ///     this method to define the valid combinations of arguments and optionally limit the flags that are valid
    ///     for each usage
    /// </summary>
    /// <param name="description">The description of this usage to be displayed from the CLI help command</param>
    /// <returns></returns>
    public UsageGraph.UsageExpression<T> Usage(string description)
    {
        return Usages.AddUsage<T>(description);
    }

    /// <summary>
    ///     The actual execution of the command. Return "false" to denote failures
    ///     or "true" for successes
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public abstract bool Execute(T input);
}