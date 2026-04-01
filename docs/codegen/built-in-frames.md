# Built-in Frames

JasperFx ships with several ready-to-use `Frame` implementations. These cover the most common code generation patterns so you rarely need to write custom frames for straightforward scenarios.

## CommentFrame

Writes a single-line C# comment into the generated code. Useful for making generated source more readable.

<!-- snippet: sample_comment_frame_usage -->
<a id='snippet-sample_comment_frame_usage'></a>
```cs
public static void UsingCommentFrame()
{
    var rules = new GenerationRules("MyApp.Generated");
    var assembly = new GeneratedAssembly(rules);

    var type = assembly.AddType("Worker", typeof(IWorker));
    var method = type.MethodFor(nameof(IWorker.Execute));

    // CommentFrame writes a C# comment line
    method.Frames.Add(new CommentFrame("Begin processing"));
    method.Frames.Code("Console.WriteLine(\"Working...\");");
}

public interface IWorker
{
    void Execute();
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/BuiltInFramesSamples.cs#L9-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_comment_frame_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Generated output:

```
// Begin processing
```

## CodeFrame

A general-purpose frame that writes a single statement from a format string. Variable placeholders are resolved automatically.

<!-- snippet: sample_code_frame_usage -->
<a id='snippet-sample_code_frame_usage'></a>
```cs
public static void UsingCodeFrame()
{
    var rules = new GenerationRules("MyApp.Generated");
    var assembly = new GeneratedAssembly(rules);

    var type = assembly.AddType("Processor", typeof(IProcessor));
    var method = type.MethodFor(nameof(IProcessor.Process));

    // CodeFrame uses a format string with variable placeholders
    method.Frames.Code("Console.WriteLine({0});", Use.Type<string>());
}

public interface IProcessor
{
    void Process(string input);
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/BuiltInFramesSamples.cs#L31-L50' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_code_frame_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The `{0}` placeholder is replaced by the resolved variable's `Usage` name. You can reference multiple variables with `{1}`, `{2}`, etc.

## ReturnFrame

Generates a `return` statement, optionally returning a resolved variable.

<!-- snippet: sample_return_frame_usage -->
<a id='snippet-sample_return_frame_usage'></a>
```cs
public static void UsingReturnFrame()
{
    var rules = new GenerationRules("MyApp.Generated");
    var assembly = new GeneratedAssembly(rules);

    var type = assembly.AddType("Checker", typeof(IChecker));
    var method = type.MethodFor(nameof(IChecker.IsValid));

    method.Frames.Code("var result = {0} != null;", Use.Type<object>());

    // ReturnFrame generates "return <variable>;"
    method.Frames.Add(new ReturnFrame(typeof(bool)));
}

public interface IChecker
{
    bool IsValid(object input);
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/BuiltInFramesSamples.cs#L52-L73' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_return_frame_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

- `new ReturnFrame()` -- generates `return;` for void methods.
- `new ReturnFrame(typeof(bool))` -- resolves a `bool` variable and generates `return <variable>;`.
- `new ReturnFrame(variable)` -- generates `return <variable.Usage>;` for a specific variable.

## MethodCall

Generates a call to an existing method with full argument and return value resolution. This is the most commonly used frame and has its own dedicated page.

<!-- snippet: sample_method_call_frame_usage -->
<a id='snippet-sample_method_call_frame_usage'></a>
```cs
public static void UsingMethodCallFrame()
{
    var rules = new GenerationRules("MyApp.Generated");
    var assembly = new GeneratedAssembly(rules);

    var type = assembly.AddType("Invoker", typeof(IInvoker));
    var method = type.MethodFor(nameof(IInvoker.Invoke));

    // MethodCall generates a call to a static or instance method
    var call = new MethodCall(typeof(Console), nameof(Console.WriteLine));
    method.Frames.Add(call);
}

public interface IInvoker
{
    void Invoke(string message);
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/BuiltInFramesSamples.cs#L121-L141' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_method_call_frame_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [MethodCall](./method-call) for the full reference.

## ConstructorFrame

Generates a `new` expression for a given type, resolving constructor arguments through the variable system.

<!-- snippet: sample_constructor_frame_usage -->
<a id='snippet-sample_constructor_frame_usage'></a>
```cs
public static void UsingConstructorFrame()
{
    var rules = new GenerationRules("MyApp.Generated");
    var assembly = new GeneratedAssembly(rules);

    var type = assembly.AddType("ServiceBuilder", typeof(IServiceBuilder));
    var method = type.MethodFor(nameof(IServiceBuilder.Build));

    // ConstructorFrame generates "var widget = new Widget();"
    var ctor = new ConstructorFrame<Widget>(() => new Widget());
    method.Frames.Add(ctor);
}

public interface IServiceBuilder
{
    Widget Build();
}

public class Widget;
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/BuiltInFramesSamples.cs#L97-L119' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_constructor_frame_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The generic `ConstructorFrame<T>` variant accepts a lambda expression to identify the constructor. The generated code will be something like:

```
var widget = new Widget();
```

### ConstructorCallMode

| Mode | Generated Code |
|------|---------------|
| `Variable` | `var x = new T(...);` (default) |
| `ReturnValue` | `return new T(...);` |
| `UsingNestedVariable` | `using var x = new T(...);` |

## IfBlock

Wraps inner frames in a conditional `if` block.

<!-- snippet: sample_if_block_usage -->
<a id='snippet-sample_if_block_usage'></a>
```cs
public static void UsingIfBlock()
{
    var rules = new GenerationRules("MyApp.Generated");
    var assembly = new GeneratedAssembly(rules);

    var type = assembly.AddType("Guard", typeof(IGuard));
    var method = type.MethodFor(nameof(IGuard.Check));

    // IfBlock wraps inner frames in an if statement
    var inner = new CodeFrame(false, "Console.WriteLine(\"Input is not null\");");
    method.Frames.Add(new IfBlock("input != null", inner));
}

public interface IGuard
{
    void Check(object input);
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/BuiltInFramesSamples.cs#L75-L95' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_if_block_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can pass a string condition or a `Variable` (uses the variable's `Usage` property as the condition).

## ThrowExceptionFrame

Generates a `throw new ExceptionType(...)` statement.

```csharp
var frame = new ThrowExceptionFrame<InvalidOperationException>(someVariable);
// Generates: throw new System.InvalidOperationException(someVariable);
```

## TemplateFrame

An abstract base for frames that use a template string with typed variable placeholders. Subclass it and override `Template()`:

```csharp
public class MyFrame : TemplateFrame
{
    protected override string Template()
    {
        var input = Arg<string>();
        return $"Console.WriteLine({input});";
    }
}
```

The `Arg<T>()` method creates a placeholder that the engine resolves to a real variable during code arrangement.

## Summary

| Frame | Purpose |
|-------|---------|
| `CommentFrame` | Writes a `// comment` line |
| `CodeFrame` | Writes a single statement from a format string |
| `ReturnFrame` | Writes `return;` or `return variable;` |
| `MethodCall` | Calls an existing method |
| `ConstructorFrame` | Calls `new T(...)` |
| `IfBlock` | Wraps frames in `if (condition) { }` |
| `ThrowExceptionFrame<T>` | Throws an exception |
| `TemplateFrame` | Template-based code with typed placeholders |
