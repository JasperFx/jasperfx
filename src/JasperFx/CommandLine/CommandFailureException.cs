namespace JasperFx.CommandLine;

public class CommandFailureException : Exception
{
    public CommandFailureException(string message) : base(message)
    {
    }
}