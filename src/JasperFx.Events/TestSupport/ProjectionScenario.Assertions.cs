using JasperFx.Core.Reflection;

namespace JasperFx.Events.TestSupport;

public abstract partial class ProjectionScenario<TOperations, TQuerySession>
    where TOperations : TQuerySession, IStorageOperations
{
    /// <summary>
    ///     General hook to make any assertion against the projected data with a query session
    /// </summary>
    /// <param name="description">Descriptive explanation of the assertion in case of failures</param>
    /// <param name="assertions"></param>
    public void AssertAgainstProjectedData(string description, Func<TQuerySession, CancellationToken, Task> assertions)
    {
        assertion(assertions).Description = description;
    }

    /// <summary>
    ///     Verify that a document with the supplied id exists. The id can be a Guid, string,
    ///     int, or long -- whatever identity type the document uses
    /// </summary>
    /// <param name="id">The identity of the document</param>
    /// <param name="assertions">Optional lambda to make additional assertions about the document state</param>
    /// <typeparam name="T">The document type</typeparam>
    public void DocumentShouldExist<T>(object id, Action<T>? assertions = null) where T : class
    {
        assertion(async (session, ct) =>
        {
            var document = await LoadDocumentAsync<T>(session, id, ct).ConfigureAwait(false);
            if (document == null)
            {
                throw new ProjectionScenarioAssertionException(
                    $"Document {typeof(T).FullNameInCode()} with id '{id}' does not exist");
            }

            assertions?.Invoke(document);
        }).Description = $"Document {typeof(T).FullNameInCode()} with id '{id}' should exist";
    }

    /// <summary>
    ///     Asserts that a document with a given id has been deleted or does not exist. The id
    ///     can be a Guid, string, int, or long -- whatever identity type the document uses
    /// </summary>
    /// <param name="id">The identity of the document</param>
    /// <typeparam name="T">The document type</typeparam>
    public void DocumentShouldNotExist<T>(object id) where T : class
    {
        assertion(async (session, ct) =>
        {
            var document = await LoadDocumentAsync<T>(session, id, ct).ConfigureAwait(false);
            if (document != null)
            {
                throw new ProjectionScenarioAssertionException(
                    $"Document {typeof(T).FullNameInCode()} with id '{id}' exists, but should not.");
            }
        }).Description = $"Document {typeof(T).FullNameInCode()} with id '{id}' should not exist or be deleted";
    }
}
