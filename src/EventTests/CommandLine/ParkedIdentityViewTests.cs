using JasperFx.Events.CommandLine;
using JasperFx.Events.Daemon;
using Shouldly;
using Spectre.Console;

namespace EventTests.CommandLine;

/// <summary>
/// jasperfx#1273: the <c>projections parked</c> table shows a parked slice's natural key right after its identity.
/// </summary>
public class ParkedIdentityViewTests
{
    private readonly StringWriter theOutput = new();
    private readonly ParkedIdentityView theView;

    public ParkedIdentityViewTests()
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(theOutput)
        });

        // Wide enough that no cell wraps
        console.Profile.Width = 300;

        theView = new ParkedIdentityView(console);
    }

    private static ParkedIdentityRow aRow(string identity, string? naturalKey) => new(new Uri("marten://main"), "main",
        new ParkedIdentity
        {
            ProjectionName = "Orders",
            ShardKey = "All",
            TenantId = "*DEFAULT*",
            Identity = identity,
            IdentityText = identity,
            IdentityType = "string",
            NaturalKey = naturalKey,
            FirstFailingSequence = 12,
            Category = ShardFailureCategory.ApplyEvent,
            ExceptionMessage = "boom",
            ParkedAt = new DateTimeOffset(2026, 11, 20, 8, 30, 0, TimeSpan.Zero),
            ReplayAttempts = 1
        });

    /// <summary>The trimmed cells of every table line that has cells: the header first, then one per row.</summary>
    private List<string[]> cells()
    {
        return theOutput.ToString()
            .Split('\n')
            .Where(x => x.TrimStart().StartsWith('│'))
            .Select(x => x.Trim().Trim('│').Split('│').Select(cell => cell.Trim()).ToArray())
            .ToList();
    }

    [Fact]
    public void the_natural_key_column_comes_right_after_the_identity()
    {
        theView.WriteParked([aRow("order-1", "ORD-1001")]);

        cells()[0].ShouldBe([
            "Database", "Projection", "Tenant", "Identity", "Natural Key", "Failed At", "Error", "Parked", "Replays"
        ]);
    }

    [Fact]
    public void shows_the_natural_key_when_the_store_filled_it()
    {
        theView.WriteParked([aRow("order-1", "ORD-1001")]);

        cells()[1].ShouldBe([
            "main", "Orders", "*DEFAULT*", "order-1", "ORD-1001", "12", "ApplyEvent: boom", "2026-11-20 08:30:00Z",
            "1"
        ]);
    }

    [Fact]
    public void the_natural_key_is_blank_when_there_is_none()
    {
        theView.WriteParked([aRow("order-1", "ORD-1001"), aRow("order-2", null)]);

        var rows = cells();
        rows[1][4].ShouldBe("ORD-1001");
        rows[2][3].ShouldBe("order-2");
        rows[2][4].ShouldBe("");
        rows[2][5].ShouldBe("12");
    }

    [Fact]
    public void escapes_markup_in_a_natural_key()
    {
        theView.WriteParked([aRow("order-1", "[ORD]-1001")]);

        cells()[1][4].ShouldBe("[ORD]-1001");
    }
}
