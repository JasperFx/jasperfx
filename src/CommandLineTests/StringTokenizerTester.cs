using JasperFx.CommandLine.Parsing;
using Shouldly;

namespace CommandLineTests
{
    
    public class StringTokenizerTester
    {
        [Fact]
        public void empty_array_for_all_whitespace()
        {
            StringTokenizer.Tokenize("").Any().ShouldBeFalse();
            StringTokenizer.Tokenize(" ").Any().ShouldBeFalse();
            StringTokenizer.Tokenize("\t").Any().ShouldBeFalse();
            StringTokenizer.Tokenize("\n").Any().ShouldBeFalse();
        }

        [Fact]
        public void tokenize_simple_strings()
        {
            StringTokenizer.Tokenize("name age state").ShouldHaveTheSameElementsAs("name", "age", "state");
            StringTokenizer.Tokenize("name   age          state").ShouldHaveTheSameElementsAs("name", "age", "state");
            StringTokenizer.Tokenize("name age\nstate").ShouldHaveTheSameElementsAs("name", "age", "state");
            StringTokenizer.Tokenize("name\tage state").ShouldHaveTheSameElementsAs("name", "age", "state");
            StringTokenizer.Tokenize("name  age state").ShouldHaveTheSameElementsAs("name", "age", "state");
        }

        [Fact]
        public void tokenize_string_with_only_one_token()
        {
            StringTokenizer.Tokenize("name").ShouldHaveTheSameElementsAs("name");
            StringTokenizer.Tokenize(" name        ").ShouldHaveTheSameElementsAs("name");
            StringTokenizer.Tokenize("\nname").ShouldHaveTheSameElementsAs("name");
            StringTokenizer.Tokenize("name\n").ShouldHaveTheSameElementsAs("name");
        }

        [Fact]
        public void tokenize_string_marked_with_parantheses()
        {
            StringTokenizer.Tokenize("name \"jeremy miller\" age").ShouldHaveTheSameElementsAs("name", "jeremy miller", "age");
            StringTokenizer.Tokenize("name \" jeremy miller\" age").ShouldHaveTheSameElementsAs("name", " jeremy miller", "age");
            StringTokenizer.Tokenize("name   \"jeremy miller\" age").ShouldHaveTheSameElementsAs("name", "jeremy miller", "age");
            StringTokenizer.Tokenize("name \"jeremy miller\"      age \"Texas\"").ShouldHaveTheSameElementsAs("name", "jeremy miller", "age", "Texas");
        }
    }
}