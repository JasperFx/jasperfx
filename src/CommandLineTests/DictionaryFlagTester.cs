using System.Reflection;
using JasperFx.CommandLine;
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
