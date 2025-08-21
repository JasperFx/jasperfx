using JasperFx.Core;
using Xunit.Abstractions;

namespace CoreTests.Core;

public class data_segregation
{
    private readonly ITestOutputHelper _output;

    public data_segregation(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void segregate_by_modulo()
    {
        var list = new List<Guid>();
        for (int i = 0; i < 1000; i++)
        {
            list.Add(Guid.NewGuid());
        }

        var values = new int[4];

        var first = new List<Guid>();

        foreach (var guid in list)
        {
            var slot = Math.Abs(guid.ToString().GetDeterministicHashCode() % 4);

            if (slot == 0)
            {
                first.Add(guid);
            }
            
            values[slot] += 1;
        }

        for (int i = 0; i < values.Length; i++)
        {
            _output.WriteLine(values[i].ToString());
        }

        var values2 = new int[5];
        foreach (var guid2 in first)
        {
            var slot2 = Math.Abs(guid2.ToString().DeterministicJavaCompliantHash() % values2.Length);
            values2[slot2] += 1;
        }
        
        _output.WriteLine("2nd Grouping");
        for (int i = 0; i < values2.Length; i++)
        {
            _output.WriteLine(values2[i].ToString());
        }
        
        
    }
}

