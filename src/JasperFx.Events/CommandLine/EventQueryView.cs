using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// Renders an <see cref="EventQueryReport"/> — as JSON for agents and scripts (the default), or as
/// a console table for a human at a terminal (<c>--format text</c>).
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: the report is serialized reflectively by System.Text.Json. The event-query command is a development-time CLI surface, not part of an AOT-published runtime — the same disposition as ProjectionRunView.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Same as IL2026 — reflective JSON serialization of the CLI's own report type.")]
internal static class EventQueryView
{
    private const int PayloadPreviewLength = 60;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
        // The echoed query is full of optional members; an agent reading the report cares about the
        // filters that were set, not a wall of nulls.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ToJson(EventQueryReport report) => JsonSerializer.Serialize(report, _json);

    /// <summary>
    /// Write straight to <see cref="Console.Out"/> rather than through Spectre: the console writer
    /// wraps and styles its output, which would corrupt JSON a script is piping.
    /// </summary>
    public static void WriteJson(EventQueryReport report) => Console.Out.WriteLine(ToJson(report));

    public static void WriteConsole(EventQueryReport report)
    {
        if (report.Error.IsNotEmpty())
        {
            AnsiConsole.MarkupLine($"[red]{report.Error!.EscapeMarkup()}[/]");

            if (report.Diagnosis.IsNotEmpty())
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[yellow]{report.Diagnosis!.EscapeMarkup()}[/]");
            }

            return;
        }

        AnsiConsole.MarkupLine(
            $"Store [blue]{(report.Store ?? "(unknown)").EscapeMarkup()}[/] · {report.TotalCount} matching event(s)" +
            $" · page {report.PageNumber} ({report.Events.Count} shown)" +
            (report.Query.TenantId.IsNotEmpty() ? $" · tenant [blue]{report.Query.TenantId!.EscapeMarkup()}[/]" : string.Empty));

        if (report.Events.Count == 0)
        {
            // A real answer, not a failure: the filters matched nothing (or the page is past the end).
            AnsiConsole.MarkupLine(report.TotalCount == 0
                ? "[yellow]No events matched the query[/]"
                : "[yellow]This page is past the end of the matches[/]");
            return;
        }

        var showPayloads = report.Events.Any(x => x.Data.HasValue);

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Seq");
        table.AddColumn("Stream");
        table.AddColumn("Ver");
        table.AddColumn("Event");
        table.AddColumn("Timestamp");
        if (showPayloads) table.AddColumn("Data");

        foreach (var e in report.Events)
        {
            // Text rather than Markup: a JSON payload preview is full of square brackets, which the
            // markup parser would either eat or choke on.
            var cells = new List<IRenderable>
            {
                new Text(e.Sequence.ToString()),
                new Text(e.StreamId),
                new Text(e.Version.ToString()),
                new Text(e.EventType),
                new Text(e.Timestamp.ToString("u"))
            };

            if (showPayloads) cells.Add(new Text(preview(e.Data)));

            table.AddRow(cells);
        }

        AnsiConsole.Write(table);

        if (report.HasMore)
        {
            AnsiConsole.MarkupLine($"[yellow]More matches remain — rerun with --page {report.PageNumber + 1}[/]");
        }
    }

    private static string preview(JsonElement? data)
    {
        if (!data.HasValue) return "(omitted)";

        var text = data.Value.ToString();
        return text.Length <= PayloadPreviewLength ? text : text[..PayloadPreviewLength] + "…";
    }
}
