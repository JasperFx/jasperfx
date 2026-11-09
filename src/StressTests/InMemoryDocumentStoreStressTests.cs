using JasperFx.Events.InMemory;
using Shouldly;

namespace StressTests;

// Moved from EventStoreTests' InMemoryAtomicCommitTests (jasperfx#963).
public class InMemoryDocumentStoreStressTests
{
    public class Note
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Text { get; set; } = "";
    }

    [Fact]
    public async Task concurrent_units_of_work_all_land()
    {
        var store = new InMemoryDocumentStore();

        var notes = Enumerable.Range(0, 200).Select(i => new Note { Text = $"note {i}" }).ToArray();

        await Parallel.ForEachAsync(notes, TestContext.Current.CancellationToken, async (note, token) =>
        {
            await using var session = store.LightweightSession();
            session.Store(note);
            await session.SaveChangesAsync(token);
        });

        await using var query = store.QuerySession();
        query.Query<Note>().Count().ShouldBe(200);
    }
}
