using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JasperFx.Resources;

/// <summary>
///     The hosted service a document store starts with: migrate every database, optionally assert the
///     result, then seed. A store supplies what each step means.
/// </summary>
/// <typeparam name="TStore">The store type an <see cref="IInitialData{TStore}" /> populates.</typeparam>
/// <typeparam name="TDatabase">
///     What the store migrates one at a time. Usually its database type. A store whose own
///     apply already spans every database (Fisher's does, to report a partial failure per tenant) can
///     answer with itself, as a single entry.
/// </typeparam>
/// <remarks>
///     <para>
///         jasperfx#1054. Lifted from Marten's <c>MartenActivator</c>, Polecat's
///         <c>PolecatActivator</c> and Fisher's three hosted services, which had the same order and
///         differed in what happens when a step fails.
///     </para>
///     <para>
///         <b>There is no lock here, and that is the seam rather than an omission.</b> The global
///         migration lock is Weasel's <c>IGlobalLock&lt;TConnection&gt;</c>: it is session-scoped, taken
///         on the connection the migration runs on, per database, and only after Weasel's fingerprint
///         fast path has decided there is work to do. So it cannot be held from out here, and a second
///         lock around this class would be a different lock. <see cref="ApplyChangesAsync" /> is where a
///         store passes its lock in, and Weasel releases it on failure (weasel#659). Nothing after that
///         call runs under the lock.
///     </para>
///     <para>
///         <b>A store on <see cref="ResourceMigrationFailureMode.ContinueOnFailures" /> should leave
///         Weasel's own failure mode at FailFast.</b> Weasel treats a lost lock under that mode as
///         nothing to do and returns normally, which from here reads as a schema that migrated. Left to
///         throw, it arrives at this class like any other failure, is logged, and stops the seeders.
///     </para>
/// </remarks>
public abstract class StoreActivator<TStore, TDatabase> : IHostedService
{
    /// <param name="store">The store being activated.</param>
    /// <param name="logger">Null for no logging.</param>
    protected StoreActivator(TStore store, ILogger? logger = null)
    {
        Store = store;
        Logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    ///     The store being activated.
    /// </summary>
    public TStore Store { get; }

    /// <summary>
    ///     The activator's logger, for a store's own steps.
    /// </summary>
    protected ILogger Logger { get; }

    /// <summary>
    ///     Whether to apply the configured schema to every database.
    /// </summary>
    protected abstract bool ShouldApplyChangesOnStartup { get; }

    /// <summary>
    ///     Whether to assert that every database matches the configured schema.
    /// </summary>
    protected virtual bool ShouldAssertDatabaseMatchesConfigurationOnStartup => false;

    /// <summary>
    ///     What a failed migration does to startup. Defaults to
    ///     <see cref="ResourceMigrationFailureMode.FailFast" />.
    /// </summary>
    protected virtual ResourceMigrationFailureMode FailureMode => ResourceMigrationFailureMode.FailFast;

    /// <summary>
    ///     The seeders to run once the schema is settled, in order.
    /// </summary>
    protected abstract IEnumerable<IInitialData<TStore>> InitialData { get; }

    /// <summary>
    ///     JasperFx's application-assembly-reuse warning (jasperfx#543) if the store buffered one,
    ///     logged first so that it is heard even if a migration then throws.
    /// </summary>
    protected virtual string? ApplicationAssemblyReuseWarning => null;

    /// <summary>
    ///     The databases to migrate or assert. Asked only when one of those is going to happen, so a
    ///     store with neither opted in starts without touching a database.
    /// </summary>
    protected abstract ValueTask<IReadOnlyList<TDatabase>> BuildDatabasesAsync(CancellationToken token);

    /// <summary>
    ///     Apply the configured schema to one database, taking whatever lock the store migrates under.
    /// </summary>
    protected abstract Task ApplyChangesAsync(TDatabase database, CancellationToken token);

    /// <summary>
    ///     Assert one database against the configured schema, throwing if it does not match.
    /// </summary>
    protected virtual Task AssertDatabaseMatchesConfigurationAsync(TDatabase database, CancellationToken token)
        => throw new NotSupportedException(
            $"{GetType().FullNameInCode()} opted into asserting the schema without overriding {nameof(AssertDatabaseMatchesConfigurationAsync)}.");

    /// <summary>
    ///     Create whatever databases are missing, before anything goes looking for them. Runs whether or
    ///     not the store migrates. Does nothing by default.
    /// </summary>
    protected virtual Task CreateDatabasesAsync(CancellationToken token) => Task.CompletedTask;

    /// <summary>
    ///     Schema work that belongs to the migration but not to one database, such as rolling a
    ///     partition window forward. Runs before the assert, so that a difference this closes is not
    ///     failed over. Does nothing by default.
    /// </summary>
    /// <param name="migrated">
    ///     The databases that migrated. Under
    ///     <see cref="ResourceMigrationFailureMode.ContinueOnFailures" /> that can be fewer than were
    ///     attempted.
    /// </param>
    /// <param name="token">Cancels with the host's startup.</param>
    protected virtual Task AfterApplyingChangesAsync(IReadOnlyList<TDatabase> migrated, CancellationToken token)
        => Task.CompletedTask;

    /// <summary>
    ///     How to name a database in a log message.
    /// </summary>
    protected virtual string DescribeDatabase(TDatabase database) => database?.ToString() ?? "unknown";

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var reuseWarning = ApplicationAssemblyReuseWarning;
        if (reuseWarning.IsNotEmpty())
        {
            Logger.LogWarning("{ApplicationAssemblyReuseWarning}", reuseWarning);
        }

        await CreateDatabasesAsync(cancellationToken).ConfigureAwait(false);

        var schemaIsSettled = true;

        if (ShouldApplyChangesOnStartup || ShouldAssertDatabaseMatchesConfigurationOnStartup)
        {
            var databases = await BuildDatabasesAsync(cancellationToken).ConfigureAwait(false);

            if (ShouldApplyChangesOnStartup)
            {
                schemaIsSettled = await applyChangesAsync(databases, cancellationToken).ConfigureAwait(false);
            }

            if (ShouldAssertDatabaseMatchesConfigurationOnStartup)
            {
                // Every database, including one that failed to migrate. Asserting is its own opt-in and
                // the strictest thing a store can ask for, so ContinueOnFailures does not excuse it.
                foreach (var database in databases)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await AssertDatabaseMatchesConfigurationAsync(database, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (!schemaIsSettled)
        {
            // Marten ran its seeders regardless here, against the database it had just logged as failed.
            Logger.LogError(
                "Skipping initial data because a database failed to migrate. Startup is continuing under ResourceMigrationFailureMode.ContinueOnFailures.");
            return;
        }

        foreach (var initialData in InitialData)
        {
            // A seeder is not bound to honour the token, and this is the last chance to stop before
            // writing to a store whose host has already gone away.
            cancellationToken.ThrowIfCancellationRequested();

            Logger.LogInformation("Applying initial data {InitialData}", initialData);
            await initialData.Populate(Store, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public virtual Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<bool> applyChangesAsync(IReadOnlyList<TDatabase> databases, CancellationToken token)
    {
        var migrated = new List<TDatabase>(databases.Count);
        var continueOnFailures = FailureMode == ResourceMigrationFailureMode.ContinueOnFailures;

        foreach (var database in databases)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                await ApplyChangesAsync(database, token).ConfigureAwait(false);
                migrated.Add(database);
            }

            // Not when the host is stopping. Marten's catch had no such guard, so a shutdown under
            // ContinueOnFailures was logged as a failed migration and every remaining database was tried
            // anyway. Tested on the token and not on OperationCanceledException, because SqlClient reports
            // a cancelled command as a SqlException.
            catch (Exception e) when (continueOnFailures && !token.IsCancellationRequested)
            {
                Logger.LogError(e,
                    "Failed to apply configured database changes to {Database} at startup. Continuing startup anyway because ResourceMigrationFailureMode is ContinueOnFailures.",
                    DescribeDatabase(database));
            }
        }

        await AfterApplyingChangesAsync(migrated, token).ConfigureAwait(false);

        return migrated.Count == databases.Count;
    }
}
