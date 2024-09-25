using System.Reflection;
using Widgets5;

namespace Widgets3;

public class Widget3CallingWidget5Caller
{
    public static Assembly Calling()
    {
        return Widget5Caller.Calling();
    }
}