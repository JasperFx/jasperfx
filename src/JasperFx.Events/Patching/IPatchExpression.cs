using System.Linq.Expressions;

namespace JasperFx.Events;

/// <summary>
/// Fluent surface for modifying JSON properties of a persisted document in-place, without
/// loading/deserializing the full document. The interface is dialect-agnostic; concrete
/// implementations translate to the store's JSON-mutation idiom (Postgres <c>jsonb_set</c>,
/// SQL Server <c>JSON_MODIFY</c>, etc.).
/// </summary>
/// <remarks>
/// Lifted from the near-verbatim duplicate that lived in Marten (<c>Marten.Patching</c>) and
/// Polecat (<c>Polecat.Patching</c>). Marten's interface is the canonical superset adopted
/// here — it carries predicate-based <see cref="AppendIfNotExists{TElement}(Expression{Func{T, IEnumerable{TElement}}}, TElement, Expression{Func{TElement, bool}})"/>,
/// <c>InsertIfNotExists(..., predicate, ...)</c>, and <c>Remove(..., predicate, ...)</c>
/// overloads that Polecat lacked, plus the <c>Rename(string, Expression&lt;Func&lt;T, object&gt;&gt;)</c>
/// shape (Polecat had a generic <c>Rename&lt;TElement&gt;</c>). Polecat grows the missing
/// overloads when it adopts this. Part of the Critter Stack 2026 dedupe pillar
/// (<see href="https://github.com/JasperFx/jasperfx/issues/214">#214</see>).
/// </remarks>
public interface IPatchExpression<T>
{
    /// <summary>
    /// Set a single field or property value within the persisted JSON data
    /// </summary>
    IPatchExpression<T> Set<TValue>(string name, TValue value);

    /// <summary>
    /// Set a single field or property value within the persisted JSON data, relative to a parent path
    /// </summary>
    IPatchExpression<T> Set<TParent, TValue>(string name, Expression<Func<T, TParent>> expression, TValue value);

    /// <summary>
    /// Set a single field or property value within the persisted JSON data
    /// </summary>
    IPatchExpression<T> Set<TValue>(Expression<Func<T, TValue>> expression, TValue value);

    /// <summary>
    /// Copy a single field or property value within the persisted JSON data to one or more destinations
    /// </summary>
    IPatchExpression<T> Duplicate<TElement>(Expression<Func<T, TElement>> expression,
        params Expression<Func<T, TElement>>[] destinations);

    /// <summary>
    /// Increment a single field or property by adding the increment value to the persisted value
    /// </summary>
    IPatchExpression<T> Increment(Expression<Func<T, int>> expression, int increment = 1);

    /// <summary>
    /// Increment a single field or property by adding the increment value to the persisted value
    /// </summary>
    IPatchExpression<T> Increment(Expression<Func<T, long>> expression, long increment = 1);

    /// <summary>
    /// Increment a single field or property by adding the increment value to the persisted value
    /// </summary>
    IPatchExpression<T> Increment(Expression<Func<T, double>> expression, double increment = 1);

    /// <summary>
    /// Increment a single field or property by adding the increment value to the persisted value
    /// </summary>
    IPatchExpression<T> Increment(Expression<Func<T, float>> expression, float increment = 1);

    /// <summary>
    /// Increment a single field or property by adding the increment value to the persisted value
    /// </summary>
    IPatchExpression<T> Increment(Expression<Func<T, decimal>> expression, decimal increment = 1);

    /// <summary>
    /// Append an element to the end of a child collection on the persisted document
    /// </summary>
    IPatchExpression<T> Append<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression, TElement element);

    /// <summary>
    /// Append an element with the specified key to a child dictionary on the persisted document
    /// </summary>
    IPatchExpression<T> Append<TElement>(Expression<Func<T, IDictionary<string, TElement>>> expression, string key,
        TElement element);

    /// <summary>
    /// Append an element to the end of a child collection on the persisted document if the element does not already exist
    /// </summary>
    IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        TElement element);

    /// <summary>
    /// Append an element with the specified key to a child dictionary on the persisted document if the key does not already exist
    /// </summary>
    IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IDictionary<string, TElement>>> expression,
        string key, TElement element);

    /// <summary>
    /// Append an element to the end of a child collection on the persisted document if the element does not already exist by predicate
    /// </summary>
    IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        TElement element, Expression<Func<TElement, bool>> predicate);

    /// <summary>
    /// Insert an element at the designated index to a child collection on the persisted document
    /// </summary>
    IPatchExpression<T> Insert<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression, TElement element,
        int? index = null);

    /// <summary>
    /// Insert an element at the designated index to a child collection on the persisted document if the value does not already exist at that index
    /// </summary>
    IPatchExpression<T> InsertIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        TElement element, int? index = null);

    /// <summary>
    /// Insert an element at the designated index to a child collection on the persisted document if the value does not already exist by predicate
    /// </summary>
    IPatchExpression<T> InsertIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        TElement element, Expression<Func<TElement, bool>> predicate, int? index = null);

    /// <summary>
    /// Remove element from a child collection on the persisted document
    /// </summary>
    IPatchExpression<T> Remove<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression, TElement element,
        RemoveAction action = RemoveAction.RemoveFirst);

    /// <summary>
    /// Remove an element with the specified key from a child dictionary on the persisted document
    /// </summary>
    IPatchExpression<T> Remove<TElement>(Expression<Func<T, IDictionary<string, TElement>>> expression, string key);

    /// <summary>
    /// Remove element from a child collection by predicate on the persisted document
    /// </summary>
    IPatchExpression<T> Remove<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression,
        Expression<Func<TElement, bool>> predicate, RemoveAction action = RemoveAction.RemoveFirst);

    /// <summary>
    /// Rename a property or field in the persisted JSON document
    /// </summary>
    IPatchExpression<T> Rename(string oldName, Expression<Func<T, object>> expression);

    /// <summary>
    /// Delete a removed property or field in the persisted JSON data
    /// </summary>
    IPatchExpression<T> Delete(string name);

    /// <summary>
    /// Delete a removed property or field in the persisted JSON data, relative to a parent path
    /// </summary>
    IPatchExpression<T> Delete<TParent>(string name, Expression<Func<T, TParent>> expression);

    /// <summary>
    /// Delete an existing property or field in the persisted JSON data
    /// </summary>
    IPatchExpression<T> Delete<TElement>(Expression<Func<T, TElement>> expression);
}
