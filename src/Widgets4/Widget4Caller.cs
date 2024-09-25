using System.Reflection;
using JasperFx.Core.TypeScanning;

[assembly: IgnoreAssembly]

namespace Widgets4;

public class Widget4Caller
{
    public static Assembly Calling()
    {
        return CallingAssembly.Find();
    }
}