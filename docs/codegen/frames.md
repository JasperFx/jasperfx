# Frames

Frames are the fundamental building blocks of the JasperFx code generation model. Each `Frame` is responsible for writing one or more lines of C# source code into a generated method body.

## The Frame Base Class

All frames derive from the abstract `Frame` class in `JasperFx.CodeGeneration.Frames`. A frame declares:

- Whether it is **async** (its constructor receives a `bool isAsync` flag).
- Which **Variables** it creates (the `creates` list).
- Which **Variables** it uses/depends on (the `uses` list).
- An optional **Next** frame in the chain.

The single abstract method every frame must implement:

```
void GenerateCode(GeneratedMethod method, ISourceWriter writer)
```

Inside `GenerateCode`, the frame writes C# text through the `ISourceWriter` and then typically calls `Next?.GenerateCode(method, writer)` to continue the chain.

## SyncFrame and AsyncFrame

JasperFx provides two convenience base classes so you do not have to pass the `isAsync` flag manually:

- **`SyncFrame`** -- sets `isAsync` to `false`. Use for frames that produce synchronous code.
- **`AsyncFrame`** -- sets `isAsync` to `true`. Use when the generated code must `await` something.

## Writing a Custom Sync Frame

<!-- snippet: sample_custom_sync_frame -->
<a id='snippet-sample_custom_sync_frame'></a>
```cs
public class LogMessageFrame : SyncFrame
{
    private readonly string _message;
    private Variable? _logger;

    public LogMessageFrame(string message)
    {
        _message = message;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"Console.WriteLine(\"{_message}\");");

        // Always call through to the next frame in the chain
        Next?.GenerateCode(method, writer);
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/FramesSamples.cs#L7-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_custom_sync_frame' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Key points:

1. Inherit from `SyncFrame` (or `AsyncFrame` for async code).
2. Override `GenerateCode` to write your C# lines through the `ISourceWriter`.
3. Always call `Next?.GenerateCode(method, writer)` at the end so the next frame in the chain can emit its code.

## Writing a Custom Async Frame

<!-- snippet: sample_custom_async_frame -->
<a id='snippet-sample_custom_async_frame'></a>
```cs
public class LoadEntityFrame : AsyncFrame
{
    private readonly Type _entityType;
    private Variable? _id;

    public LoadEntityFrame(Type entityType)
    {
        _entityType = entityType;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var entityVariable = Create(_entityType);

        writer.Write(
            $"var {entityVariable.Usage} = await repository.LoadAsync<{_entityType.Name}>({_id?.Usage ?? "id"});");

        Next?.GenerateCode(method, writer);
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/FramesSamples.cs#L30-L53' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_custom_async_frame' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Async frames work identically except they extend `AsyncFrame` and typically emit `await` expressions.

## Wrapping Frames

A frame can wrap subsequent frames by setting `Wraps = true`. This is useful for try/catch blocks, using blocks, timing wrappers, and similar patterns.

<!-- snippet: sample_wrapping_frame -->
<a id='snippet-sample_wrapping_frame'></a>
```cs
public class StopwatchFrame : SyncFrame
{
    public StopwatchFrame()
    {
        // Mark this frame as wrapping inner frames
        Wraps = true;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write("var stopwatch = System.Diagnostics.Stopwatch.StartNew();");
        writer.Write("BLOCK:try");

        // Let the inner frames generate their code
        Next?.GenerateCode(method, writer);

        writer.FinishBlock(); // end try
        writer.Write("BLOCK:finally");
        writer.Write("stopwatch.Stop();");
        writer.Write("Console.WriteLine($\"Elapsed: {stopwatch.ElapsedMilliseconds}ms\");");
        writer.FinishBlock(); // end finally
    }
}
```
<sup><a href='https://github.com/JasperFx/jasperfx/blob/master/src/DocSamples/FramesSamples.cs#L55-L81' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_wrapping_frame' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

When `Wraps` is `true`, the code generation engine knows that this frame opens a scope that encloses the next frame(s).

## Creating Variables from a Frame

Frames can declare that they "create" a variable. This is how the variable resolution system knows which frame must run before another:

```csharp
// Inside a Frame subclass
var result = Create<MyService>(); // registers the variable in this.creates
```

Any downstream frame that needs a `MyService` variable will automatically depend on this frame.

## Variable Dependencies

Frames can also declare variables they "use":

```csharp
uses.Add(someVariable);
```

The code generation engine uses these `creates` and `uses` declarations to determine the correct ordering of frames and to resolve variables across the method.

## Frame Ordering

When you add frames to a `GeneratedMethod`, the engine:

1. Collects all frames and their variable dependencies.
2. Topologically sorts frames so that a frame producing a variable always appears before frames consuming it.
3. Chains frames together via their `Next` property.
4. Calls `GenerateCode` on the first frame, which cascades through the chain.

You do not need to worry about insertion order for most cases -- the dependency graph handles it. However, if two frames have no dependency relationship, they will appear in the order you added them.
