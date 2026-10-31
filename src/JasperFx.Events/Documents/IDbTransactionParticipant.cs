using System.Data.Common;

namespace JasperFx.Events.Documents;

/// <summary>
/// Work that writes inside a document session's own transaction, on its own connection. Enlisted through
/// <see cref="IDocumentWriteOperations.AddTransactionParticipant(IDbTransactionParticipant)" />.
/// </summary>
/// <remarks>
/// <para>
/// Marten, Polecat and Fisher each declare an <c>ITransactionParticipant</c> over their own driver's
/// connection and transaction. Those three differ by exactly those two parameter types, and that is the
/// only reason a message bus had to write its envelope rows once per store. This is the same member
/// over the base pair, so the SQL can live once. See
/// <see href="https://github.com/JasperFx/jasperfx/issues/1052" />.
/// </para>
/// <para>
/// Declared here, not reused from Weasel's generic <c>ITransactionParticipant&lt;TConnection, TTransaction&gt;</c>,
/// because JasperFx sits below Weasel. Named differently from the stores' interfaces so that a file
/// importing a store and this namespace does not collide, and because the two have different lifetimes:
/// see the method's remarks.
/// </para>
/// <para>
/// No after-commit member. Polecat's participant has none to bind to, and a consumer that needs to know
/// the commit landed has <see cref="IDocumentSaveListener" />.
/// </para>
/// </remarks>
public interface IDbTransactionParticipant
{
    /// <summary>
    /// Write inside the transaction, after every operation in the unit of work has succeeded and before the
    /// commit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠️ <strong>Write on the <paramref name="connection" /> you are handed.</strong> A second connection to
    /// the same database is a second transaction, which on SQLite is a second writer that blocks on the
    /// first from inside it: a hang rather than an error.
    /// </para>
    /// <para>
    /// ⚠️ <strong>Called once per attempt.</strong> A store that retries a failed transaction calls this
    /// again on a fresh connection, because what the first call wrote rolled back with it. Hold no state
    /// here that assumes one call. An exception rolls the whole unit of work back.
    /// </para>
    /// <para>
    /// A participant belongs to one unit of work: it runs on every attempt of the next commit that
    /// succeeds, and not on the commit after that. That differs from the stores' own participants, all
    /// three of which stay enlisted until the session is disposed, which suits an EF Core context and
    /// would write an envelope row twice on a session saved twice.
    /// </para>
    /// </remarks>
    Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction, CancellationToken token);
}
