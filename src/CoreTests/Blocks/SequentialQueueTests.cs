using JasperFx.Blocks;
using Shouldly;

namespace CoreTests.Blocks;

public class SequentialQueueTests
{
    private List<Guid> randomList(int count)
    {
        var list = new List<Guid>();
        for (int i = 0; i < count; i++)
        {
            list.Add(Guid.NewGuid());
        }

        return list;
    }

    [Fact]
    public async Task process_a_bunch()
    {
        var list = new List<Guid>();
        var expected = randomList(100);

        await using var queue = new SequentialQueue<Guid>(async (n, _) =>
        {
            await Task.Delay(Random.Shared.Next(50, 100), _);
            list.Add(n);
        });

        foreach (var guid in expected)
        {
            await queue.PostAsync(guid);
        }
        
        await queue.WaitForCompletionAsync();
        
        list.ShouldBe(expected);
    }
    
    [Fact]
    public async Task process_with_parallel_writers()
    {
        var expected1 = randomList(100);
        var expected2 = randomList(100);
        var expected3 = randomList(100);
        var expected4 = randomList(100);
        
        var list = new List<Guid>();
        
        await using var queue = new SequentialQueue<Guid>(async (n, _) =>
        {
            await Task.Delay(Random.Shared.Next(10, 25), _);
            list.Add(n);
        });

        Func<List<Guid>, Task> publish = async l =>
        {
            foreach (var guid in l)
            {
                await queue.PostAsync(guid);
            }
        };
        
        await Task.WhenAll([
            Task.Run(() => publish(expected1)),
            Task.Run(() => publish(expected2)),
            Task.Run(() => publish(expected3)),
            Task.Run(() => publish(expected4)),
            
            
            ]);
        
        await queue.WaitForCompletionAsync();

        var all = expected1.Concat(expected2).Concat(expected3).Concat(expected4).OrderBy(x => x).ToArray();
        var actual = list.OrderBy(x => x).ToArray();
        
        actual.ShouldBe(all);
    }
}
