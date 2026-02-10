using JasperFx.CommandLine.Descriptions;

namespace JasperFx.Resources;

public class ResourceSetupException : Exception
{
    public ResourceSetupException(IStatefulResource resource, Exception ex) : base(
        $"Failed to setup resource {resource.Name} of type {resource.Type}", ex)

    {
    }
    
    public ResourceSetupException(IResourceCreator resource, Exception ex) : base(
        $"Failed to execute IResourceCreator {resource.Name} of type {resource.Type}", ex)

    {
    }

    public ResourceSetupException(ISystemPart systemPart, Exception ex) : base(
        $"Failed to build resources from system part {systemPart}", ex)
    {
    }
}