using JasperFx.CommandLine.Descriptions;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Spectre.Console;

namespace CommandLineTests.Descriptions
{
    public class DescribeCommandTests
    {
        private readonly ISystemPart[] theParts = new[]
            { new DescribedPart(), new ConsoleWritingPart(), };


        [Fact]
        public async Task write_to_console()
        {
            await DescribeCommand.WriteToConsole(theParts);

            theParts[1].As<ConsoleWritingPart>()
                .DidWriteToConsole.ShouldBeTrue();
        }
        
        public class FakeService
        {
            public string Name { get; set; }
        }

        public class DescribedPart : SystemPartBase
        {
            public DescribedPart() : base(Guid.NewGuid().ToString(), new Uri("described://one"))
            {
            }

            public string Title { get; set; } = Guid.NewGuid().ToString();
            public string Key { get; set; } = Guid.NewGuid().ToString();
            public string Body { get; set; } = Guid.NewGuid().ToString();


        }

        public class DescribedAndTablePart : DescribedPart, IDescribesProperties
        {
            public bool DidWriteToConsole { get; set; }

            public override Task WriteToConsole()
            {
                DidWriteToConsole = true;

                var table = DescribeProperties().BuildTableForProperties();
                AnsiConsole.Write(table);

                return Task.CompletedTask;
            }

            public IDictionary<string, object> DescribeProperties()
            {
                return new Dictionary<string, object>
                {
                    { "foo", "bar" },
                    { "number", 5 },
                };
            }
        }

        public class ConsoleWritingPart : DescribedPart
        {
            public override Task WriteToConsole()
            {
                DidWriteToConsole = true;

                Console.WriteLine(Body);

                return Task.CompletedTask;
            }

            public bool DidWriteToConsole { get; set; }
        }

    }
}