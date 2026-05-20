using System.Linq.Expressions;
using JasperFx.Events;
using Shouldly;

namespace EventTests;

// Compile-pins the entire lifted IPatchExpression<T> surface (Marten's superset) by
// implementing it, and verifies the fluent methods chain. If a method signature changes,
// RecordingPatchExpression stops compiling.
public class PatchExpressionContractTests
{
    [Fact]
    public void remove_action_ordinals_match_both_stores()
    {
        ((int)RemoveAction.RemoveFirst).ShouldBe(0);
        ((int)RemoveAction.RemoveAll).ShouldBe(1);
    }

    [Fact]
    public void fluent_surface_chains()
    {
        IPatchExpression<Target> patch = new RecordingPatchExpression<Target>();

        var result = patch
            .Set("Name", "x")
            .Set(t => t.Count, 5)
            .Increment(t => t.Count)
            .Increment(t => t.Count, 3)
            .Append(t => t.Tags, "a")
            .AppendIfNotExists(t => t.Tags, "b")
            .AppendIfNotExists(t => t.Tags, "c", e => e == "c")
            .Insert(t => t.Tags, "d")
            .InsertIfNotExists(t => t.Tags, "e", e => e == "e", 0)
            .Remove(t => t.Tags, "a")
            .Remove(t => t.Tags, e => e.StartsWith("a"), RemoveAction.RemoveAll)
            .Rename("old", t => (object)t.Name)
            .Delete("redundant")
            .Delete(t => t.Count);

        result.ShouldBeSameAs(patch);
        ((RecordingPatchExpression<Target>)patch).Calls.ShouldBeGreaterThan(10);
    }

    private sealed class Target
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
        public List<string> Tags { get; set; } = [];
    }

    private sealed class RecordingPatchExpression<T> : IPatchExpression<T>
    {
        public int Calls { get; private set; }

        private IPatchExpression<T> Track()
        {
            Calls++;
            return this;
        }

        public IPatchExpression<T> Set<TValue>(string name, TValue value) => Track();
        public IPatchExpression<T> Set<TParent, TValue>(string name, Expression<Func<T, TParent>> expression, TValue value) => Track();
        public IPatchExpression<T> Set<TValue>(Expression<Func<T, TValue>> expression, TValue value) => Track();
        public IPatchExpression<T> Duplicate<TElement>(Expression<Func<T, TElement>> expression, params Expression<Func<T, TElement>>[] destinations) => Track();
        public IPatchExpression<T> Increment(Expression<Func<T, int>> expression, int increment = 1) => Track();
        public IPatchExpression<T> Increment(Expression<Func<T, long>> expression, long increment = 1) => Track();
        public IPatchExpression<T> Increment(Expression<Func<T, double>> expression, double increment = 1) => Track();
        public IPatchExpression<T> Increment(Expression<Func<T, float>> expression, float increment = 1) => Track();
        public IPatchExpression<T> Increment(Expression<Func<T, decimal>> expression, decimal increment = 1) => Track();
        public IPatchExpression<T> Append<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression, TElement element) => Track();
        public IPatchExpression<T> Append<TElement>(Expression<Func<T, IDictionary<string, TElement>>> expression, string key, TElement element) => Track();
        public IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression, TElement element) => Track();
        public IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IDictionary<string, TElement>>> expression, string key, TElement element) => Track();
        public IPatchExpression<T> AppendIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression, TElement element, Expression<Func<TElement, bool>> predicate) => Track();
        public IPatchExpression<T> Insert<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression, TElement element, int? index = null) => Track();
        public IPatchExpression<T> InsertIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression, TElement element, int? index = null) => Track();
        public IPatchExpression<T> InsertIfNotExists<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression, TElement element, Expression<Func<TElement, bool>> predicate, int? index = null) => Track();
        public IPatchExpression<T> Remove<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression, TElement element, RemoveAction action = RemoveAction.RemoveFirst) => Track();
        public IPatchExpression<T> Remove<TElement>(Expression<Func<T, IDictionary<string, TElement>>> expression, string key) => Track();
        public IPatchExpression<T> Remove<TElement>(Expression<Func<T, IEnumerable<TElement>>> expression, Expression<Func<TElement, bool>> predicate, RemoveAction action = RemoveAction.RemoveFirst) => Track();
        public IPatchExpression<T> Rename(string oldName, Expression<Func<T, object>> expression) => Track();
        public IPatchExpression<T> Delete(string name) => Track();
        public IPatchExpression<T> Delete<TParent>(string name, Expression<Func<T, TParent>> expression) => Track();
        public IPatchExpression<T> Delete<TElement>(Expression<Func<T, TElement>> expression) => Track();
    }
}
