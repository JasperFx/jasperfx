using JasperFx;
using Shouldly;

namespace CoreTests;

public class StoreConfigContractTests
{
    [Fact]
    public async Task initial_data_collection_runs_class_and_lambda_populators()
    {
        var store = new FakeStore();
        var collection = new InitialDataCollection<FakeStore>
        {
            new SeedItem(),
        };
        collection.Add((s, _) =>
        {
            s.Seeded.Add("lambda");
            return Task.CompletedTask;
        });

        foreach (var data in collection)
        {
            await data.Populate(store, CancellationToken.None);
        }

        store.Seeded.ShouldBe(["class", "lambda"]);
    }

    [Fact]
    public void configure_store_hook_mutates_options()
    {
        var options = new FakeOptions();
        IConfigureStore<FakeOptions> hook = new SetNameHook("configured");

        hook.Configure(services: null!, options);

        options.Name.ShouldBe("configured");
    }

    [Fact]
    public async Task async_configure_store_hook_mutates_options()
    {
        var options = new FakeOptions();
        IAsyncConfigureStore<FakeOptions> hook = new AsyncSetNameHook("async-configured");

        await hook.Configure(options, CancellationToken.None);

        options.Name.ShouldBe("async-configured");
    }

    private sealed class FakeStore
    {
        public List<string> Seeded { get; } = [];
    }

    private sealed class FakeOptions
    {
        public string? Name { get; set; }
    }

    private sealed class SeedItem : IInitialData<FakeStore>
    {
        public Task Populate(FakeStore store, CancellationToken cancellation)
        {
            store.Seeded.Add("class");
            return Task.CompletedTask;
        }
    }

    private sealed class SetNameHook(string name) : IConfigureStore<FakeOptions>
    {
        public void Configure(IServiceProvider services, FakeOptions options) => options.Name = name;
    }

    private sealed class AsyncSetNameHook(string name) : IAsyncConfigureStore<FakeOptions>
    {
        public ValueTask Configure(FakeOptions options, CancellationToken cancellationToken)
        {
            options.Name = name;
            return ValueTask.CompletedTask;
        }
    }
}
