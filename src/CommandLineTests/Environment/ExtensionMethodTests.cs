using JasperFx.CommandLine.Descriptions;
using JasperFx.Environment;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace CommandLineTests.Environment
{
    public class ExtensionMethodTests
    {
        private readonly ServiceCollection theServices = new ServiceCollection();

        private EnvironmentCheckResults _results;

        private EnvironmentCheckResults theResults
        {
            get
            {
                if (_results == null)
                    _results = EnvironmentChecker.ExecuteAllEnvironmentChecks(theServices.BuildServiceProvider())
                        .GetAwaiter().GetResult();

                return _results;
            }
        }

        public class Thing
        {
            public string Name { get; set; }
            public int Number { get; set; }
        }

        [Fact]
        public void asynchronous_action()
        {
            var thing = new Thing{Name = "Blob"};
            theServices.AddSingleton(thing);
            
            theServices.CheckEnvironment("good", (s, c) => Task.CompletedTask);
            theServices.CheckEnvironment("bad", (s, c) => throw new InvalidOperationException(s.GetService<Thing>().Name));
            
            theResults.Successes.First().ShouldBe("good");
            theResults.Failures.Single().Exception.Message.ShouldBe("Blob");
        }

        [Fact]
        public void use_a_resource_collection_automatically_success()
        {
            var resource = Substitute.For<IStatefulResource>();
            resource.Name.Returns("Envelopes");
            resource.Type.Returns("Database");
            resource.ResourceUri.Returns(new Uri("resource://one"));
            resource.SubjectUri.Returns(new Uri("subject://two"));

            var collection = Substitute.For<ISystemPart>();
            collection.FindResources().Returns(new List<IStatefulResource> { resource });
            theServices.AddSingleton(collection);
            
            theResults.Successes.Last().ShouldBe("subject://two/");
        }
        
        [Fact]
        public void use_a_resource_collection_automatically_fail()
        {
            var resource = Substitute.For<IStatefulResource>();
            resource.Name.Returns("Envelopes");
            resource.Type.Returns("Database");
            resource.ResourceUri.Returns(new Uri("resource://one"));
            resource.SubjectUri.Returns(new Uri("subject://two"));
            resource.Check(Arg.Any<CancellationToken>()).Throws(new DivideByZeroException());
            
            var collection = Substitute.For<ISystemPart>();
            collection.FindResources().Returns(new List<IStatefulResource> { resource });
            theServices.AddSingleton(collection);
            
            theResults.Failures.Single().Description.ShouldBe("subject://two/ (resource://one/)");
        }
        

        
        [Fact]
        public void synchronous_action()
        {
            var thing = new Thing{Name = "Blob"};
            theServices.AddSingleton(thing);
            
            theServices.CheckEnvironment("good", (s) => {});
            theServices.CheckEnvironment("bad", s => throw new InvalidOperationException(s.GetService<Thing>().Name));
            
            theResults.Successes.First().ShouldBe("good");
            theResults.Failures.Single().Exception.Message.ShouldBe("Blob");
        }

        [Fact]
        public void synchronous_with_service()
        {
            var thing = new Thing{Name = "Blob"};
            theServices.AddSingleton(thing);
            
            theServices.CheckEnvironment<Thing>("Name cannot be Blob", t =>
            {
                if (t.Name == "Blob") throw new DivideByZeroException();
            });
            
            theResults.Failures.Single().Description.ShouldBe("Name cannot be Blob");
        }
        
        [Fact]
        public void asynchronous_with_service()
        {
            var thing = new Thing{Name = "Blob"};
            theServices.AddSingleton(thing);
            
            theServices.CheckEnvironment<Thing>("Name cannot be Blob", async (t, token) =>
            {
                await Task.Delay(1.Milliseconds(), token);
                if (t.Name == "Blob") throw new DivideByZeroException();

                
            });
            
            theResults.Failures.Single().Description.ShouldBe("Name cannot be Blob");
        }

        [Fact]
        public void file_must_exist()
        {
            // Making sure we know what's going on here
            File.WriteAllText("a.txt", "something");
            File.Delete("b.txt");
            
            theServices.CheckThatFileExists("a.txt");
            theServices.CheckThatFileExists("b.txt");
            
            theResults.Successes.First().ShouldBe("File 'a.txt' can be found");
            theResults.Failures.Single().Description.ShouldBe("File 'b.txt' can be found");
        }

        public interface IMissingService
        {
            
        }
        
        [Fact]
        public void check_service_is_registered_by_generic()
        {
            var thing = new Thing{Name = "Blob"};
            theServices.AddSingleton(thing);
            
            theServices.CheckServiceIsRegistered<Thing>();
            theServices.CheckServiceIsRegistered<IMissingService>();
            
            theResults.Successes.First().ShouldBe($"Service {typeof(Thing).FullName} should be registered");
            theResults.Failures.Single().Description.ShouldBe($"Service {typeof(IMissingService).FullName} should be registered");
            
        }
        
        [Fact]
        public void check_service_is_registered_by_type()
        {
            var thing = new Thing{Name = "Blob"};
            theServices.AddSingleton(thing);
            
            theServices.CheckServiceIsRegistered(typeof(Thing));
            theServices.CheckServiceIsRegistered(typeof(IMissingService));
            
            theResults.Successes.First().ShouldBe($"Service {typeof(Thing).FullName} should be registered");
            theResults.Failures.Single().Description.ShouldBe($"Service {typeof(IMissingService).FullName} should be registered");
            
        }
    }
}