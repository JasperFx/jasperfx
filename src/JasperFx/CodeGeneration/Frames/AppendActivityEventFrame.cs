using System.Diagnostics;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace JasperFx.CodeGeneration.Frames;

/// <summary>
/// Codegen frame that appends an <see cref="ActivityEvent"/> to
/// <see cref="Activity.Current"/> when an activity exists. Emits
/// <c>System.Diagnostics.Activity.Current?.AddEvent(new System.Diagnostics.ActivityEvent("..."));</c>
/// using fully qualified type names so the frame is safe to use from any
/// generated assembly without depending on namespace imports.
/// </summary>
public class AppendActivityEventFrame : Frame
{
    private readonly string _eventName;

    /// <param name="eventName">
    /// Name of the activity event. Forwarded as a string literal into the generated
    /// <see cref="ActivityEvent"/> constructor argument. The caller is responsible
    /// for escaping any embedded double quotes.
    /// </param>
    public AppendActivityEventFrame(string eventName) : base(false)
    {
        _eventName = eventName ?? throw new ArgumentNullException(nameof(eventName));
    }

    /// <summary>
    /// The event name that will be embedded into the generated code.
    /// </summary>
    public string EventName => _eventName;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write(
            $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}?.AddEvent(new {typeof(ActivityEvent).FullNameInCode()}(\"{_eventName}\"));");

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain) => [];
}
