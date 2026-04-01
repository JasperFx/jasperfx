using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace DocSamples;

public class ExtensionSamples
{
    #region sample_string_extensions
    public void StringHelpers()
    {
        // Convert to camel case
        var camel = "SomePropertyName".ToCamelCase();
        // => "somePropertyName"

        // Check if a string is empty or whitespace
        var isEmpty = "".IsEmpty();
        var isNotEmpty = "hello".IsNotEmpty();

        // Joining strings
        var joined = new[] { "one", "two", "three" }.Join(", ");
    }
    #endregion

    #region sample_enumerable_extensions
    public void EnumerableHelpers()
    {
        var items = new List<string> { "a", "b", "c", "a" };

        // AddRange that works on IList
        items.Fill("d");

        // Add only if not already present
        items.Fill("a"); // no-op if already present

        // Each / EachAsync for side effects
        items.Each(item => Console.WriteLine(item));
    }
    #endregion

    #region sample_reflection_extensions
    public void ReflectionHelpers()
    {
        // Check if a type implements an interface
        var implements = typeof(List<string>).CanBeCastTo<IEnumerable<string>>();

        // Get a human-readable type name
        var name = typeof(Dictionary<string, int>).FullNameInCode();

        // Close an open generic type
        var closed = typeof(List<>).CloseAndBuildAs<object>(typeof(string));
    }
    #endregion
}
