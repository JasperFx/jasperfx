using JasperFx;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CoreTests.Resources;

public class StoreActivatorTests
{
    private readonly FakeActivator theActivator = new();

    private List<string> theSteps => theActivator.Steps;

    [Fact]
    public async Task runs_warning_then_create_then_migrate_then_assert_then_seed()
    {
        theActivator.Warning = "reused assembly";
        theActivator.Apply = true;
        theActivator.Assert = true;
        theActivator.Databases = ["one", "two"];
        theActivator.Seeders = [new Seeder(theActivator, "users"), new Seeder(theActivator, "roles")];

        await theActivator.StartAsync(CancellationToken.None);

        theSteps.ShouldBe([
            "warn:reused assembly",
            "create",
            "build",
            "apply:one",
            "apply:two",
            "after:one,two",
            "assert:one",
            "assert:two",
            "seed:users",
            "seed:roles"
        ]);
    }

    // Polecat's rule rather than Marten's, which built the databases unconditionally: with neither opted in
    // there is nothing to do with them, and a dynamic tenancy would hit its master table to find out.
    [Fact]
    public async Task a_store_with_neither_opted_in_touches_no_database_and_still_seeds()
    {
        theActivator.Seeders = [new Seeder(theActivator, "users")];

        await theActivator.StartAsync(CancellationToken.None);

        theSteps.ShouldBe(["create", "seed:users"]);
    }

    [Fact]
    public async Task asserting_does_not_need_migrating()
    {
        theActivator.Assert = true;
        theActivator.Databases = ["one"];

        await theActivator.StartAsync(CancellationToken.None);

        theSteps.ShouldBe(["create", "build", "assert:one"]);
    }

    [Fact]
    public async Task no_buffered_warning_logs_no_warning()
    {
        await theActivator.StartAsync(CancellationToken.None);

        theActivator.Logged.ShouldNotContain(x => x.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task the_warning_is_heard_even_when_the_migration_then_throws()
    {
        theActivator.Warning = "reused assembly";
        theActivator.Apply = true;
        theActivator.Databases = ["one"];
        theActivator.FailApplyOn["one"] = new DivideByZeroException();

        await Should.ThrowAsync<DivideByZeroException>(() => theActivator.StartAsync(CancellationToken.None));

        theSteps[0].ShouldBe("warn:reused assembly");
    }

    [Fact]
    public async Task under_fail_fast_a_failed_migration_stops_everything_after_it()
    {
        var failure = new DivideByZeroException();
        theActivator.Apply = true;
        theActivator.Assert = true;
        theActivator.Databases = ["one", "two", "three"];
        theActivator.FailApplyOn["two"] = failure;
        theActivator.Seeders = [new Seeder(theActivator, "users")];

        var thrown = await Should.ThrowAsync<DivideByZeroException>(
            () => theActivator.StartAsync(CancellationToken.None));

        thrown.ShouldBeSameAs(failure);
        theSteps.ShouldBe(["create", "build", "apply:one", "apply:two"]);
    }

    // The bug, and the point of the ordering: Marten logged the failure, kept going, and ran the seeders
    // against the database it had just said it could not migrate.
    [Fact]
    public async Task initial_data_does_not_run_on_a_schema_that_failed_to_migrate()
    {
        theActivator.Apply = true;
        theActivator.Mode = ResourceMigrationFailureMode.ContinueOnFailures;
        theActivator.Databases = ["one", "two", "three"];
        theActivator.FailApplyOn["two"] = new DivideByZeroException();
        theActivator.Seeders = [new Seeder(theActivator, "users")];

        await theActivator.StartAsync(CancellationToken.None);

        theSteps.ShouldBe(["create", "build", "apply:one", "apply:two", "apply:three", "after:one,three"]);

        theActivator.Logged.ShouldContain(x => x.Level == LogLevel.Error && x.Exception is DivideByZeroException);
        theActivator.Logged.ShouldContain(x => x.Level == LogLevel.Error && x.Message.Contains("Skipping initial data"));
    }

    [Fact]
    public async Task continue_on_failures_with_nothing_failing_still_seeds()
    {
        theActivator.Apply = true;
        theActivator.Mode = ResourceMigrationFailureMode.ContinueOnFailures;
        theActivator.Databases = ["one"];
        theActivator.Seeders = [new Seeder(theActivator, "users")];

        await theActivator.StartAsync(CancellationToken.None);

        theSteps.ShouldContain("seed:users");
    }

    // Asserting is the strictest thing a store can ask for. ContinueOnFailures is about the migration.
    [Fact]
    public async Task an_assert_that_fails_stops_startup_under_either_failure_mode()
    {
        var failure = new DivideByZeroException();
        theActivator.Assert = true;
        theActivator.Mode = ResourceMigrationFailureMode.ContinueOnFailures;
        theActivator.Databases = ["one", "two"];
        theActivator.FailAssertOn["one"] = failure;
        theActivator.Seeders = [new Seeder(theActivator, "users")];

        (await Should.ThrowAsync<DivideByZeroException>(() => theActivator.StartAsync(CancellationToken.None)))
            .ShouldBeSameAs(failure);

        theSteps.ShouldBe(["create", "build", "assert:one"]);
    }

    [Fact]
    public async Task an_assert_also_covers_a_database_that_failed_to_migrate()
    {
        theActivator.Apply = true;
        theActivator.Assert = true;
        theActivator.Mode = ResourceMigrationFailureMode.ContinueOnFailures;
        theActivator.Databases = ["one", "two"];
        theActivator.FailApplyOn["one"] = new DivideByZeroException();

        await theActivator.StartAsync(CancellationToken.None);

        theSteps.ShouldContain("assert:one");
        theSteps.ShouldContain("assert:two");
    }

    [Fact]
    public async Task a_failing_seeder_stops_startup_and_the_seeders_after_it()
    {
        var failure = new DivideByZeroException();
        theActivator.Seeders =
        [
            new Seeder(theActivator, "users") { Failure = failure },
            new Seeder(theActivator, "roles")
        ];

        (await Should.ThrowAsync<DivideByZeroException>(() => theActivator.StartAsync(CancellationToken.None)))
            .ShouldBeSameAs(failure);

        theSteps.ShouldBe(["create", "seed:users"]);
    }

    [Fact]
    public async Task asking_for_an_assert_a_store_never_implemented_is_refused_by_name()
    {
        var activator = new NoAssertActivator();

        (await Should.ThrowAsync<NotSupportedException>(() => activator.StartAsync(CancellationToken.None)))
            .Message.ShouldContain(nameof(NoAssertActivator));
    }

    // Stands in for Weasel, which holds the lock inside the apply and releases it on the way out (weasel#659).
    // What it pins here is this class's half: nothing runs while it is held, and the failure comes out.
    [Fact]
    public async Task the_lock_a_store_migrates_under_is_not_held_by_anything_that_follows()
    {
        var failure = new DivideByZeroException();
        theActivator.Apply = true;
        theActivator.Assert = true;
        theActivator.Mode = ResourceMigrationFailureMode.ContinueOnFailures;
        theActivator.Databases = ["one", "two"];
        theActivator.FailApplyOn["two"] = failure;
        theActivator.Seeders = [new Seeder(theActivator, "users")];

        await theActivator.StartAsync(CancellationToken.None);

        theActivator.HeldDuring.ShouldBe(["apply:one", "apply:two"]);
        theActivator.LockHeld.ShouldBeFalse();
    }

    [Fact]
    public async Task a_token_already_cancelled_runs_nothing()
    {
        await Should.ThrowAsync<OperationCanceledException>(
            () => theActivator.StartAsync(new CancellationToken(canceled: true)));

        theSteps.ShouldBeEmpty();
    }

    // Marten's catch filtered on the failure mode alone, so a stopping host under ContinueOnFailures logged
    // an error, tried every remaining database, and carried on.
    [Fact]
    public async Task a_host_stopping_mid_migration_cancels_under_continue_on_failures()
    {
        using var stopping = new CancellationTokenSource();

        theActivator.Apply = true;
        theActivator.Mode = ResourceMigrationFailureMode.ContinueOnFailures;
        theActivator.Databases = ["one", "two"];
        theActivator.OnApply["one"] = token =>
        {
            stopping.Cancel();
            token.ThrowIfCancellationRequested();
        };
        theActivator.Seeders = [new Seeder(theActivator, "users")];

        await Should.ThrowAsync<OperationCanceledException>(() => theActivator.StartAsync(stopping.Token));

        theSteps.ShouldBe(["create", "build", "apply:one"]);
        theActivator.Logged.ShouldNotContain(x => x.Level == LogLevel.Error);
    }

    // SqlClient reports a cancelled command as a SqlException, so this cannot key on the exception's type.
    [Fact]
    public async Task a_provider_exception_for_a_cancelled_command_is_not_mistaken_for_a_failed_migration()
    {
        using var stopping = new CancellationTokenSource();
        var providerFailure = new InvalidOperationException("Operation cancelled by user.");

        theActivator.Apply = true;
        theActivator.Mode = ResourceMigrationFailureMode.ContinueOnFailures;
        theActivator.Databases = ["one", "two"];
        theActivator.OnApply["one"] = _ =>
        {
            stopping.Cancel();
            throw providerFailure;
        };

        (await Should.ThrowAsync<InvalidOperationException>(() => theActivator.StartAsync(stopping.Token)))
            .ShouldBeSameAs(providerFailure);

        theSteps.ShouldBe(["create", "build", "apply:one"]);
    }

    // A command timeout surfacing as a cancellation is a failed migration, because nobody asked to stop.
    [Fact]
    public async Task a_cancellation_the_host_did_not_ask_for_is_a_failed_migration()
    {
        theActivator.Apply = true;
        theActivator.Mode = ResourceMigrationFailureMode.ContinueOnFailures;
        theActivator.Databases = ["one", "two"];
        theActivator.FailApplyOn["one"] = new TaskCanceledException("command timeout");
        theActivator.Seeders = [new Seeder(theActivator, "users")];

        await theActivator.StartAsync(CancellationToken.None);

        theSteps.ShouldContain("apply:two");
        theSteps.ShouldNotContain("seed:users");
    }

    [Fact]
    public async Task stopping_between_databases_stops_a_seam_that_ignores_the_token()
    {
        using var stopping = new CancellationTokenSource();

        theActivator.Apply = true;
        theActivator.Databases = ["one", "two"];
        theActivator.OnApply["one"] = _ => stopping.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => theActivator.StartAsync(stopping.Token));

        theSteps.ShouldBe(["create", "build", "apply:one"]);
    }

    [Fact]
    public async Task stopping_between_asserts_stops_the_next_assert()
    {
        using var stopping = new CancellationTokenSource();

        theActivator.Assert = true;
        theActivator.Databases = ["one", "two"];
        theActivator.OnAssert["one"] = () => stopping.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() => theActivator.StartAsync(stopping.Token));

        theSteps.ShouldBe(["create", "build", "assert:one"]);
    }

    [Fact]
    public async Task stopping_between_seeders_stops_a_seeder_that_ignores_the_token()
    {
        using var stopping = new CancellationTokenSource();

        theActivator.Seeders =
        [
            new Seeder(theActivator, "users") { After = () => stopping.Cancel() },
            new Seeder(theActivator, "roles")
        ];

        await Should.ThrowAsync<OperationCanceledException>(() => theActivator.StartAsync(stopping.Token));

        theSteps.ShouldBe(["create", "seed:users"]);
    }

    [Fact]
    public async Task a_real_host_stopping_during_activation_comes_back_as_a_cancellation()
    {
        using var stopping = new CancellationTokenSource();
        var migrating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var activator = new FakeActivator
        {
            Apply = true,
            Mode = ResourceMigrationFailureMode.ContinueOnFailures,
            Databases = ["one", "two"],
            Blocking = migrating
        };
        activator.Seeders = [new Seeder(activator, "users")];

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<IHostedService>(activator);
        using var host = builder.Build();

        var starting = host.StartAsync(stopping.Token);
        await migrating.Task;
        await stopping.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => starting);

        activator.Steps.ShouldBe(["create", "build", "apply:one"]);
    }

    [Fact]
    public async Task stopping_does_nothing()
    {
        await theActivator.StopAsync(CancellationToken.None);

        theSteps.ShouldBeEmpty();
    }

    private sealed class Seeder(FakeActivator activator, string name) : IInitialData<FakeStore>
    {
        public Exception? Failure { get; init; }
        public Action? After { get; init; }

        public Task Populate(FakeStore store, CancellationToken cancellation)
        {
            activator.Steps.Add($"seed:{name}");
            if (Failure != null) throw Failure;

            After?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStore;

    private sealed class FakeLogger(List<(LogLevel Level, string Message, Exception? Exception)> logged) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => logged.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class FakeActivator : StoreActivator<FakeStore, string>
    {
        public FakeActivator() : this([])
        {
        }

        private FakeActivator(List<(LogLevel Level, string Message, Exception? Exception)> logged)
            : base(new FakeStore(), new FakeLogger(logged))
        {
            Logged = logged;
        }

        public List<(LogLevel Level, string Message, Exception? Exception)> Logged { get; }
        public List<string> Steps { get; } = [];
        public List<string> HeldDuring { get; } = [];
        public bool LockHeld { get; private set; }

        public bool Apply { get; set; }
        public bool Assert { get; set; }
        public string? Warning { get; set; }
        public ResourceMigrationFailureMode Mode { get; set; } = ResourceMigrationFailureMode.FailFast;
        public IReadOnlyList<string> Databases { get; set; } = [];
        public List<IInitialData<FakeStore>> Seeders { get; set; } = [];
        public Dictionary<string, Exception> FailApplyOn { get; } = [];
        public Dictionary<string, Exception> FailAssertOn { get; } = [];
        public Dictionary<string, Action<CancellationToken>> OnApply { get; } = [];
        public Dictionary<string, Action> OnAssert { get; } = [];
        public TaskCompletionSource? Blocking { get; init; }

        protected override bool ShouldApplyChangesOnStartup => Apply;
        protected override bool ShouldAssertDatabaseMatchesConfigurationOnStartup => Assert;
        protected override ResourceMigrationFailureMode FailureMode => Mode;
        protected override IEnumerable<IInitialData<FakeStore>> InitialData => Seeders;

        protected override string? ApplicationAssemblyReuseWarning
        {
            get
            {
                if (Warning != null) Steps.Add($"warn:{Warning}");
                return Warning;
            }
        }

        protected override Task CreateDatabasesAsync(CancellationToken token)
        {
            Steps.Add("create");
            return Task.CompletedTask;
        }

        protected override ValueTask<IReadOnlyList<string>> BuildDatabasesAsync(CancellationToken token)
        {
            Steps.Add("build");
            return ValueTask.FromResult(Databases);
        }

        protected override async Task ApplyChangesAsync(string database, CancellationToken token)
        {
            LockHeld = true;
            try
            {
                Steps.Add($"apply:{database}");
                HeldDuring.Add($"apply:{database}");

                if (Blocking != null)
                {
                    Blocking.TrySetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }

                if (OnApply.TryGetValue(database, out var onApply)) onApply(token);
                if (FailApplyOn.TryGetValue(database, out var failure)) throw failure;
            }
            finally
            {
                LockHeld = false;
            }
        }

        protected override Task AfterApplyingChangesAsync(IReadOnlyList<string> migrated, CancellationToken token)
        {
            Steps.Add($"after:{string.Join(",", migrated)}");
            return Task.CompletedTask;
        }

        protected override Task AssertDatabaseMatchesConfigurationAsync(string database, CancellationToken token)
        {
            if (LockHeld) HeldDuring.Add($"assert:{database}");

            Steps.Add($"assert:{database}");

            if (OnAssert.TryGetValue(database, out var onAssert)) onAssert();
            return FailAssertOn.TryGetValue(database, out var failure) ? Task.FromException(failure) : Task.CompletedTask;
        }
    }

    private sealed class NoAssertActivator() : StoreActivator<FakeStore, string>(new FakeStore())
    {
        protected override bool ShouldApplyChangesOnStartup => false;
        protected override bool ShouldAssertDatabaseMatchesConfigurationOnStartup => true;
        protected override IEnumerable<IInitialData<FakeStore>> InitialData => [];

        protected override ValueTask<IReadOnlyList<string>> BuildDatabasesAsync(CancellationToken token)
            => ValueTask.FromResult<IReadOnlyList<string>>(["one"]);

        protected override Task ApplyChangesAsync(string database, CancellationToken token) => Task.CompletedTask;
    }
}
