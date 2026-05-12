using System.Diagnostics.CodeAnalysis;
using JasperFx.CommandLine.Help;

namespace JasperFx.CommandLine;

/// <summary>
///     Base class for all JasperFx commands
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract class JasperFxCommand<T> : IJasperFxCommand<T>
{
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
        Justification = "JasperFxCommand<T> instances are constructed by ICommandCreator (annotated) from a Type that survives the trim graph because the command was reachable through CommandFactory.RegisterCommand[s]. The UsageGraph reads members of T (the user input type) — if T is reachable as the command's input, its members are preserved by the entry-point annotations.")]
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
        Justification = "Same as IL2026 — the UsageGraph closes List<elementType> for enumerable arguments via InputParser.BuildHandler, reached only through annotated CommandFactory entry points.")]
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