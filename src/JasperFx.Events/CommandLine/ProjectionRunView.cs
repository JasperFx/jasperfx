using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// Renders a <see cref="ProjectionRunReport"/> — as JSON for agents and scripts, or as a console
/// table for a human at a terminal.
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: the report is serialized reflectively by System.Text.Json. The projection-run command is a development-time CLI surface, not part of an AOT-published runtime — the same disposition as ProjectionHost.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Same as IL2026 — reflective JSON serialization of the CLI's own report type.")]
internal static class ProjectionRunView
{
    private const int StatePreviewLength = 80;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // The source mode is named, not numbered: an agent reading "mode": 1 out of a saved report
        // has to go and find the enum to know what it ran.
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true
    };

    public static string ToJson(ProjectionRunReport report) => JsonSerializer.Serialize(report, _json);

    /// <summary>
    /// Write straight to <see cref="Console.Out"/> rather than through Spectre: the console writer
    /// wraps and styles its output, which would corrupt JSON a script is piping.
    /// </summary>
    public static void WriteJson(ProjectionRunReport report) => Console.Out.WriteLine(ToJson(report));

    public static void WriteConsole(ProjectionRunReport report)
    {
        if (report.Error.IsNotEmpty())
        {
            AnsiConsole.MarkupLine($"[red]{report.Error!.EscapeMarkup()}[/]");
            return;
        }

        AnsiConsole.Write(new Rule($"[bold]{report.Projection.EscapeMarkup()}[/] over {report.Source.Key.EscapeMarkup()}")
        {
            Justification = Justify.Left
        });

        AnsiConsole.MarkupLine(
            $"Store [blue]{(report.Store ?? "(unknown)").EscapeMarkup()}[/] · {report.EventCount} source event(s)" +
            (report.Source.TenantId.IsNotEmpty() ? $" · tenant [blue]{report.Source.TenantId!.EscapeMarkup()}[/]" : string.Empty));

        if (report.Truncated)
        {
            // A capped read produces a timeline that looks complete, so this cannot be a quiet omission.
            AnsiConsole.MarkupLine(
                "[yellow]The source read stopped at --max-events; this timeline is a prefix of the slice, not all of it[/]");
        }

        if (report.Aggregates.Count == 0)
        {
            AnsiConsole.MarkupLine(report.EventCount == 0
                ? "[yellow]No events matched the source slice[/]"
                : "[yellow]The projection produced no aggregate identity from these events[/]");
            return;
        }

        foreach (var aggregate in report.Aggregates)
        {
            writeAggregate(aggregate);
        }
    }

    private static void writeAggregate(ProjectionRunAggregateReport aggregate)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule($"[bold]{aggregate.Identity.EscapeMarkup()}[/]") { Justification = Justify.Left });

        var anyErrors = aggregate.Steps.Any(x => x.Error != null);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("#");
        table.AddColumn("Version");
        table.AddColumn("Event");
        table.AddColumn("Timestamp");
        table.AddColumn("ms");
        table.AddColumn("After");
        if (anyErrors) table.AddColumn("Error");

        foreach (var step in aggregate.Steps)
        {
            // Text rather than Markup: a JSON state preview is full of square brackets, which the
            // markup parser would either eat or choke on.
            var cells = new List<IRenderable>
            {
                new Text(step.Step.ToString()),
                new Text(step.StreamVersion.ToString()),
                new Text(step.EventType),
                new Text(step.Timestamp.ToString("u")),
                new Text(step.ElapsedMs.ToString("0.###")),
                new Text(preview(step.After))
            };

            if (anyErrors) cells.Add(new Text(step.Error ?? string.Empty));

            table.AddRow(cells);
        }

        AnsiConsole.Write(table);

        if (aggregate.FinalState.HasValue)
        {
            AnsiConsole.MarkupLine("[bold]Final state[/]");
            AnsiConsole.WriteLine(JsonSerializer.Serialize(aggregate.FinalState.Value, _json));
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No final state — the projection produced none for this identity[/]");
        }
    }

    private static string preview(JsonElement? state)
    {
        if (!state.HasValue) return "(none)";

        var text = state.Value.ToString();
        return text.Length <= StatePreviewLength ? text : text[..StatePreviewLength] + "…";
    }
}
