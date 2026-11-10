using System.Reflection;
using JasperFx.CommandLine;
using JasperFx.CommandLine.Parsing;
using JasperFx.CommandLine.Parsing.Generated;
using Shouldly;

namespace CommandLineTests
{
    public class DictionaryFlagTester
    {
        private CommandFactory theFactory;

        public DictionaryFlagTester()
        {
            theFactory = new CommandFactory();
            theFactory.RegisterCommands(GetType().GetTypeInfo().Assembly);
        }

        private CommandRun forArgs(string args)
        {
            return theFactory.BuildRun(args);
        }

        [Fact]
        public void use_prop_flags()
        {
            var run = forArgs("dict --prop:color red --prop:age 43");

            var input = run.Input.ShouldBeOfType<DictInput>();
            input.PropFlag["color"].ShouldBe("red");
            input.PropFlag["age"].ShouldBe("43");
        }
        
        [Fact]
        public void a_key_with_colons_in_it_is_kept_whole()
        {
            // GH-1132: --config:Logging:LogLevel:Default Warning used to come out
            // with a key of just "Default"
            var run = forArgs("dict --prop:Logging:LogLevel:Default Warning");

            var input = run.Input.ShouldBeOfType<DictInput>();
            input.PropFlag["Logging:LogLevel:Default"].ShouldBe("Warning");
        }

        [Fact]
        public void a_key_with_colons_in_it_is_kept_whole_by_both_parsers()
        {
            // The test assembly runs the source generator, so BuildRun() above goes through the
            // generated parser. Drive the reflection handler and the generated handler directly
            // to prove both strip only the "--prop:" prefix
            var reflected = InputParser.GetHandlers(typeof(DictInput)).OfType<DictionaryFlag>().Single();
            var generated = GeneratedParserRegistry.TryGetHandlers(typeof(DictInput))
                .ShouldNotBeNull()
                .OfType<GeneratedDictionaryFlag>().Single();

            foreach (ITokenHandler handler in new ITokenHandler[] { reflected, generated })
            {
                var input = new DictInput();
                var tokens = new Queue<string>(["--prop:Logging:LogLevel:Default", "Warning"]);

                handler.Handle(input, tokens).ShouldBeTrue();

                input.PropFlag.Keys.ShouldBe(["Logging:LogLevel:Default"]);
                input.PropFlag["Logging:LogLevel:Default"].ShouldBe("Warning");
            }
        }

        [Fact]
        public void use_prop_flags_when_dict_has_to_be_built()
        {
            var run = forArgs("missingdict --prop:color red --prop:age 43");

            var input = run.Input.ShouldBeOfType<MissingInput>();
            input.PropFlag["color"].ShouldBe("red");
            input.PropFlag["age"].ShouldBe("43");
        }
    }

    #region sample_DictInput
    public class DictInput
    {
        public Dictionary<string, string> PropFlag = new Dictionary<string, string>();
    }
    #endregion

    public class DictCommand : JasperFxCommand<DictInput>
    {
        public override bool Execute(DictInput input)
        {
            return true;
        }
    }
    
    public class MissingInput
    {
        public Dictionary<string, string> PropFlag;
    }

    [Description("SOMETHING", Name = "missingdict")]
    public class MissingDictCommand : JasperFxCommand<MissingInput>
    {
        public override bool Execute(MissingInput input)
        {
            return true;
        }
    }
}
