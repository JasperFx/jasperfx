namespace FSharpCodegenTarget;

/// <summary>
///     The interface the generated F# type implements — stands in for a Wolverine
///     handler/endpoint contract.
/// </summary>
public interface IGreeter
{
    string Greet(string name);
}

/// <summary>
///     The async counterpart of <see cref="IGreeter" />, used to exercise the F# <c>task { }</c>
///     emit path.
/// </summary>
public interface IAsyncGreeter
{
    Task<string> GreetAsync(string name);
}

/// <summary>
///     A simple value object the generated method constructs (exercises ConstructorFrame).
/// </summary>
public class Salutation
{
    public Salutation(string name)
    {
        Text = "Hello " + name;
    }

    public string Text { get; }
}

/// <summary>
///     A dependency the generated type takes through its constructor and calls
///     (exercises constructor injection + MethodCall).
/// </summary>
public class GreetingService
{
    public string CreateGreeting(Salutation salutation)
    {
        return salutation.Text + "!";
    }

    public Task<string> CreateGreetingAsync(Salutation salutation)
    {
        return Task.FromResult(salutation.Text + "!");
    }
}
