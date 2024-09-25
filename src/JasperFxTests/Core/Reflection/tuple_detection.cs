using JasperFx.Core.Reflection;
using Shouldly;

namespace JasperFxTests.Core.Reflection;

public class tuple_detection
{
    [Theory]
    [InlineData(typeof(string), false)]
    [InlineData(typeof((bool, int)), true)]
    [InlineData(typeof((bool, int, string)), true)]
    [InlineData(typeof((bool, int, string, double)), true)]
    [InlineData(typeof((bool, int, string, double, long)), true)]
    [InlineData(typeof((bool, int, string, double, long, DateTime)), true)]
    [InlineData(typeof((bool, int, string, double, long, DateTime, DateOnly)), true)]
    [InlineData(typeof((bool, int, string, double, long, DateTime, DateOnly, TimeOnly)), true)]
    [InlineData(typeof((bool, int, string, double, long, DateTime, DateOnly, TimeOnly, string)), true)]
    public void try_it_out(Type type, bool expected)
    {
        type.IsValueTuple().ShouldBe(expected);
    }
}