using System.Reflection;
using JasperFx.Core.TypeScanning;

namespace Widgets1;

public class WidgetCallingAssemblyFinder
{
    public static Assembly Calling()
    {
        return CallingAssembly.Find();
    }
}