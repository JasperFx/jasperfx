using System.Reflection;

namespace JasperFx.CommandLine.Parsing;

public static class QueueExtensions
{
    public static bool NextIsFlag(this Queue<string> queue)
    {
        return InputParser.IsFlag(queue.Peek());
    }

    public static bool NextIsFlagFor(this Queue<string> queue, MemberInfo property)
    {
        return InputParser.IsFlagFor(queue.Peek(), property);
    }
}