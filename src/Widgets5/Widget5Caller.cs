using System.Reflection;
using JasperFx.Core.TypeScanning;

namespace Widgets5;

public class Widget5Caller
{
    public static Assembly Calling()
    {
        return CallingAssembly.Find();
    }
}