using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace JasperFx.Events.Projections;

/// <summary>
///     Thrown when any configuration rules for an active projection are violated and the projection is invalid
/// </summary>
public class InvalidProjectionException: Exception
{
    public InvalidProjectionException(string message): base(message)
    {
    }

    public InvalidProjectionException(object projection, IEnumerable<MethodSlot> invalidMethods): base(
        ToMessage(projection, invalidMethods))
    {
        InvalidMethods = invalidMethods.ToArray();
    }

    public InvalidProjectionException(string[] messages): base(messages.Join(System.Environment.NewLine))
    {
        InvalidMethods = new MethodSlot[0];
    }

    public MethodSlot[] InvalidMethods { get; }

    private static string ToMessage(object projection, IEnumerable<MethodSlot> invalidMethods)
    {
        var writer = new StringWriter();
        writer.WriteLine($"Projection {projection.GetType().FullNameInCode()} has validation errors:");
        foreach (var slot in invalidMethods)
        {
            writer.WriteLine(slot.Signature());
            foreach (var error in slot.Errors) writer.WriteLine(" - " + error);
        }

        return writer.ToString();
    }
}
