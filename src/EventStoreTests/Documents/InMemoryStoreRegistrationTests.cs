using JasperFx.Events;
using JasperFx.Events.Documents;
using JasperFx.Events.Projections;
using EventStoreTests.Documents.LiveAggregation;
using JasperFx.Events.InMemory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace EventStoreTests.Documents;

/// <summary>
/// jasperfx#965: the in-memory prototyping store's registration and guardrails.
/// </summary>
public class InMemoryStoreRegistrationTests
{
    private static IHost buildHost(string environment, Action<InMemoryStoreOptions>? configure = null,
        CapturingLoggerProvider? logs = null)
    {
        return Host.CreateDefaultBuilder()
            .UseEnvironment(environment)
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                if (logs is not null) logging.AddProvider(logs);
            })
            .ConfigureServices(services => services.AddInMemoryStoreForPrototyping(configure))
            .Build();
    }

    [Fact]
    public async Task refuses_to_start_in_the_production_environment()
    {
        using var host = buildHost(Environments.Production);

        var ex = await Should.ThrowAsync<NotSupportedException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        // The message names both ways out
        ex.Message.ShouldContain("DOTNET_ENVIRONMENT to Development");
        ex.Message.ShouldContain("AllowStartingInTheProductionEnvironment = true");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Testing")]
    public async Task starts_with_a_warning_anywhere_but_production(string environment)
    {
        var logs = new CapturingLoggerProvider();
        using var host = buildHost(environment, logs: logs);

        await host.StartAsync(TestContext.Current.CancellationToken);

        logs.Messages.ShouldContain(x => x.Level == LogLevel.Warning
                                         && x.Message.Contains("in-memory prototyping store is in use")
                                         && x.Message.Contains(environment));

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_opt_out_lets_a_production_test_host_start()
    {
        using var host = buildHost(Environments.Production, x => x.AllowStartingInTheProductionEnvironment = true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_store_is_the_document_session_factory_and_is_configured()
    {
        using var host = buildHost(Environments.Development,
            x => x.ConfigureDocuments(store => store.SoftDeletedTypes.Add(typeof(InMemoryStoreRegistrationTests))));

        var store = host.Services.GetRequiredService<InMemoryDocumentStore>();
        host.Services.GetRequiredService<IDocumentSessionFactory>().ShouldBeSameAs(store);
        store.SoftDeletedTypes.ShouldContain(typeof(InMemoryStoreRegistrationTests));

        await using var session = host.Services.GetRequiredService<IDocumentSessionFactory>().LightweightSession();
        session.ShouldBeOfType<InMemoryDocumentSession>();
    }

    // ---- jasperfx#964 phase 4: the event store, end to end through the registration ----

    [Fact]
    public async Task events_and_an_inline_snapshot_end_to_end_through_the_registration()
    {
        var token = TestContext.Current.CancellationToken;
        using var host = buildHost(Environments.Development,
            x => x.ConfigureProjections(projections => projections.Snapshot<Ticket>(SnapshotLifecycle.Inline)));
        await host.StartAsync(token);

        // Only the store-agnostic contract from here on, which is all Wolverine's integration will see
        var sessions = host.Services.GetRequiredService<IDocumentSessionFactory>();
        var id = Guid.NewGuid();

        await using (var session = sessions.LightweightSession())
        {
            session.Events.StartStream<Ticket>(id, new TicketOpened("printer on fire"));
            await session.SaveChangesAsync(token);
        }

        await using (var session = sessions.LightweightSession())
        {
            var stream = await session.Events.FetchForWriting<Ticket>(id, token);
            stream.Aggregate!.Title.ShouldBe("printer on fire");

            stream.AppendOne(new TicketAssigned("agent-7"));
            await session.SaveChangesAsync(token);
        }

        // FetchLatest is on the writable session's event API
        await using (var query = sessions.LightweightSession())
        {
            (await query.Events.FetchLatest<Ticket>(id, token))!.AssignedTo.ShouldBe("agent-7");

            // ...and that came from the inline snapshot, written in the commit
            (await query.LoadAsync<Ticket>(id, token))!.AssignedTo.ShouldBe("agent-7");
        }

        await host.StopAsync(token);
    }

    [Fact]
    public async Task the_event_configuration_applies_before_projections_whatever_the_call_order()
    {
        var token = TestContext.Current.CancellationToken;

        // A stub with no Id is closed over the STREAM identity when it is registered as a snapshot, so the
        // registration only works if the string identity was configured first
        using var host = buildHost(Environments.Development, x => x
            .ConfigureProjections(projections => projections.Snapshot<Escalation>(SnapshotLifecycle.Inline))
            .ConfigureEvents(events => events.StreamIdentity = StreamIdentity.AsString));
        await host.StartAsync(token);

        var sessions = host.Services.GetRequiredService<IDocumentSessionFactory>();

        await using (var session = sessions.LightweightSession())
        {
            session.Events.StartStream<Escalation>("ESC-1", new TicketOpened("stub"), new TicketAssigned("agent-1"));
            await session.SaveChangesAsync(token);
        }

        await using (var query = sessions.LightweightSession())
        {
            (await query.Events.FetchLatest<Escalation>("ESC-1", token))!.Count.ShouldBe(2);
        }

        await host.StopAsync(token);
    }

    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(CapturingLoggerProvider parent) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (parent.Messages) parent.Messages.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
