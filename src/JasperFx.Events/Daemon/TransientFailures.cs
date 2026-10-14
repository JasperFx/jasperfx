using System.Data.Common;
using System.Net.Sockets;

namespace JasperFx.Events.Daemon;

/// <summary>
/// wolverine#4986: decides whether a fault that stopped a projection or subscription shard is expected to clear on
/// its own, for <see cref="ShardFailure.IsTransient" />. A supervisor -- Wolverine's node agent, CritterWatch -- uses
/// that to tell "restart it after a while" apart from "needs an operator".
///
/// <para>
/// The answer comes from the exception itself rather than from a list of registered types. The registered list in
/// <see cref="Projections.ProjectionExceptions" /> is far too coarse for this: a store registers its driver's whole
/// exception hierarchy there, which makes a missing column just as "transient" as a pool timeout. That list still
/// decides what the projection bases rethrow for the daemon's retries; this does not change that.
/// </para>
///
/// <para>
/// The whole exception graph is walked -- inner exceptions and <see cref="AggregateException.InnerExceptions" /> --
/// because the database fault routinely arrives wrapped in a store or daemon exception. The failure is transient
/// when any exception in it is:
/// </para>
/// <list type="bullet">
/// <item>a <see cref="DbException" /> reporting <see cref="DbException.IsTransient" />, which drivers implement from
/// the server's error code (Npgsql from the SQLSTATE: connection, resource, shutdown, deadlock and serialization
/// failures are transient, a missing column or table is not)</item>
/// <item>a <see cref="TimeoutException" /></item>
/// <item>a <see cref="SocketException" />, or an <see cref="IOException" /> other than a file system one</item>
/// </list>
/// <para>
/// A store whose driver does not implement <see cref="DbException.IsTransient" /> well can add a classifier with
/// <see cref="AddClassifier" />. Its verdict wins over the rules above for any exception it answers about.
/// </para>
/// </summary>
public static class TransientFailures
{
    private static Func<Exception, bool?>[] _classifiers = [];
    private static readonly object _locker = new();

    /// <summary>
    /// Add a classifier for one store or driver. Return true or false for an exception the classifier recognizes
    /// -- which then overrides the built in rules for that exception -- and null for anything else.
    /// </summary>
    public static void AddClassifier(Func<Exception, bool?> classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);

        lock (_locker)
        {
            if (_classifiers.Contains(classifier)) return;
            _classifiers = [.._classifiers, classifier];
        }
    }

    /// <summary>
    /// Whether anything in <paramref name="exception" />'s graph is a fault expected to clear on its own.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return isTransient(exception, 0);
    }

    // Exception graphs can in principle be cyclic or absurdly deep; nothing real is 32 levels deep
    private const int MaxDepth = 32;

    private static bool isTransient(Exception exception, int depth)
    {
        if (depth > MaxDepth) return false;

        if (classify(exception)) return true;

        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(x => isTransient(x, depth + 1));
        }

        return exception.InnerException != null && isTransient(exception.InnerException, depth + 1);
    }

    private static bool classify(Exception exception)
    {
        foreach (var classifier in _classifiers)
        {
            var verdict = classifier(exception);
            if (verdict.HasValue) return verdict.Value;
        }

        return exception switch
        {
            DbException db => db.IsTransient,
            TimeoutException => true,
            SocketException => true,
            // A connection dropped part way through a read surfaces as EndOfStreamException, which stays transient
            FileNotFoundException or DirectoryNotFoundException or PathTooLongException => false,
            IOException => true,
            _ => false
        };
    }
}
