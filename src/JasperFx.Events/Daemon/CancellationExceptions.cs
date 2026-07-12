using System.Data.Common;

namespace JasperFx.Events.Daemon;

/// <summary>
///     Classifies whether an exception observed while a shard is being cancelled or torn down is a
///     genuine side effect of that cancellation, or a real failure that deserves to be logged before
///     the teardown discards it. A schema problem doesn't stop being a schema problem just because
///     the shard's CancellationTokenSource fired first (jasperfx#507)
/// </summary>
public static class CancellationExceptions
{
    public static bool IsCancellationLike(Exception exception)
    {
        switch (exception)
        {
            case OperationCanceledException:
                return true;

            // A connection/session disposed underneath an in-flight operation during teardown
            case ObjectDisposedException:
                return true;

            case AggregateException aggregate:
                return aggregate.InnerExceptions.Count > 0 &&
                       aggregate.InnerExceptions.All(IsCancellationLike);

            // Provider-agnostic via DbException.SqlState (populated by Npgsql among others).
            // Database exceptions like MartenCommandException wrap the provider exception, so the
            // inner-exception walk below covers the wrapped case
            case DbException db when isCancellationSqlState(db.SqlState):
                return true;
        }

        return exception.InnerException != null && IsCancellationLike(exception.InnerException);
    }

    private static bool isCancellationSqlState(string? sqlState)
    {
        if (sqlState == null)
        {
            return false;
        }

        // 57014 = query_canceled (the server-side face of a cancelled command);
        // class 08 = connection exceptions (connection_failure, connection_does_not_exist, ...)
        return sqlState == "57014" || sqlState.StartsWith("08", StringComparison.Ordinal);
    }
}
