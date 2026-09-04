using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Core;
using Spectre.Console;

namespace JasperFx.Events.CommandLine;

/// <summary>
/// Renders a <see cref="StreamQueryReport"/> — as JSON for agents and scripts (the default), or as
/// a console table for a human at a terminal (<c>--format text</c>).
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode",
    Justification = "Class-level: the report is serialized reflectively by System.Text.Json. The stream-query command is a development-time CLI surface, not part of an AOT-published runtime — the same disposition as ProjectionRunView and EventQueryView.")]
[UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode",
    Justification = "Same as IL2026 — reflective JSON serialization of the CLI's own report type.")]
internal static class StreamQueryView
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
        // The echoed filters are all optional; an agent reading the report cares about the ones
        // that were set, not a wall of nulls.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ToJson(StreamQueryReport report) => JsonSerializer.Serialize(report, _json);

    /// <summary>
    /// Write straight to <see cref="Console.Out"/> rather than through Spectre: the console writer
    /// wraps and styles its output, which would corrupt JSON a script is piping.
    /// </summary>
    public static void WriteJson(StreamQueryReport report) => Console.Out.WriteLine(ToJson(report));

    public static void WriteConsole(StreamQueryReport report)
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
            $"Store [blue]{(report.Store ?? "(unknown)").EscapeMarkup()}[/] · {report.TotalCount} matching stream(s)" +
            $" · page {report.PageNumber} ({report.Streams.Count} shown)" +
            (report.Query.TenantId.IsNotEmpty() ? $" · tenant [blue]{report.Query.TenantId!.EscapeMarkup()}[/]" : string.Empty));

        if (report.Streams.Count == 0)
        {
            // A real answer, not a failure: the filters matched nothing (or the page is past the end).
            AnsiConsole.MarkupLine(report.TotalCount == 0
                ? "[yellow]No streams matched the query[/]"
                : "[yellow]This page is past the end of the matches[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("Stream");
        table.AddColumn("Type");
        table.AddColumn("Version");
        table.AddColumn("Compacted");
        table.AddColumn("Growth");
        table.AddColumn("Created");
        table.AddColumn("Last append");
        table.AddColumn("Archived");

        foreach (var stream in report.Streams)
        {
            table.AddRow(
                new Text(stream.StreamId),
                new Text(stream.AggregateType ?? "(none)"),
                new Text(stream.Version.ToString()),
                new Text(stream.CompactedVersion == 0 ? "never" : stream.CompactedVersion.ToString()),
                new Text(stream.VersionsSinceCompaction.ToString()),
                new Text(stream.Created.ToString("u")),
                new Text(stream.LastTimestamp.ToString("u")),
                new Text(stream.IsArchived ? "yes" : string.Empty));
        }

        AnsiConsole.Write(table);

        if (report.HasMore)
        {
            AnsiConsole.MarkupLine($"[yellow]More matches remain — rerun with --page {report.PageNumber + 1}[/]");
        }
    }
}
