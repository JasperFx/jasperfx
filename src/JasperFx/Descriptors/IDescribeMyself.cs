namespace JasperFx.Descriptors;

/// <summary>
/// Just gives an object more control over how it creates an OptionsDescription
/// </summary>
public interface IDescribeMyself
{
    OptionsDescription ToDescription();
}