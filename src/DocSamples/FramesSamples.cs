using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace DocSamples;

#region sample_custom_sync_frame

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

#endregion

#region sample_custom_async_frame

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

#endregion

#region sample_wrapping_frame

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

#endregion
