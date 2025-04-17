using JasperFx.Environment;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CommandLineTests.Environment
{
    public class EnvironmentCheckerTests
    {
        private readonly ServiceCollection Services = new ServiceCollection();

        private EnvironmentCheckResults _results;

        private EnvironmentCheckResults theResults
        {
            get
            {
                if (_results == null)
                    _results = EnvironmentChecker.ExecuteAllEnvironmentChecks(Services.BuildServiceProvider())
                        .GetAwaiter().GetResult();

                return _results;
            }
        }

        [Fact]
        public void happy_path_with_good_checks()
        {
            Services.CheckEnvironment("Ok", s => { });
            Services.CheckEnvironment("Fine", s => { });
            Services.CheckEnvironment("Not bad", s => { });

            theResults.Assert();
        }

        [Fact]
        public void sad_path_with_some_failures()
        {
            Services.CheckEnvironment("Ok", s => { });
            Services.CheckEnvironment("Fine", s => { });
            Services.CheckEnvironment("Not bad", s => { });
            Services.CheckEnvironment("Bad!", s => throw new NotImplementedException());
            Services.CheckEnvironment("Worse!", s => throw new NotImplementedException());

            theResults.Succeeded().ShouldBeFalse();
        }
        
    }
}