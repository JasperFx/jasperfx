namespace JasperFx.Events.Projections;

public class DuplicateSubscriptionNamesException: Exception
{
    public DuplicateSubscriptionNamesException(string message): base(message)
    {
    }
}