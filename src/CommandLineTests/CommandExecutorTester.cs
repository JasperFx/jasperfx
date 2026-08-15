using System.Reflection;
using JasperFx.CommandLine;
using JasperFx.Core;
using Shouldly;
using Spectre.Console;

namespace CommandLineTests
{
    [Collection("SetConsoleOutput")]
    public class CommandExecutorTester : IDisposable
    {
        private readonly StringWriter theOutput = new StringWriter();
        private readonly TextWriter theOriginalConsoleOut;
        private readonly IAnsiConsole theOriginalAnsiConsole;


#if NET451
        private string directory = AppDomain.CurrentDomain.BaseDirectory;
#else
        private string directory = AppContext.BaseDirectory;
#endif
        private CommandExecutor executor;


        public CommandExecutorTester()
        {
            theOriginalConsoleOut = Console.Out;
            theOriginalAnsiConsole = AnsiConsole.Console;

            Console.SetOut(theOutput);

            // CommandExecutor renders failures through Spectre's AnsiConsole, which caches the
            // TextWriter it binds to on first use anywhere in the process. Console.SetOut alone
            // therefore does NOT redirect the failure path -- whichever test touched Spectre first
            // has already pinned the writer. That made run_an_async_command_that_fails pass or fail
            // purely on test ordering (it survived xunit v2's order and broke under v3's). Bind
            // Spectre explicitly so this class captures failure output regardless of order.
            AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new AnsiConsoleOutput(theOutput)
            });

            executor = CommandExecutor.For(_ =>
            {
                _.RegisterCommands(GetType().GetTypeInfo().Assembly);
            });
        }

        /// <summary>
        /// Both writers this class swaps in are process-global. Leaving them bound to a dead
        /// instance's StringWriter means any later test in the assembly that writes through
        /// Console or AnsiConsole -- CommandFactory's "Error parsing input" path, for one --
        /// keeps appending to a buffer whose owning test is long gone. Restoring them here
        /// confines each instance's capture to its own test.
        /// </summary>
        public void Dispose()
        {
            Console.SetOut(theOriginalConsoleOut);
            AnsiConsole.Console = theOriginalAnsiConsole;
        }


        [Fact]
        public void execute_happy_path()
        {
            executor.Execute("say-name Lebron James")
                .ShouldBe(0);

            theOutput.ToString().ShouldContain("Lebron James");
        }

        [Fact]
        public void execute_async_happy_path()
        {
            executor.Execute("say-async-name Lebron James")
                .ShouldBe(0);

            theOutput.ToString().ShouldContain("Lebron James");
        }




        [Fact]
        public void run_an_async_command_that_fails()
        {
            executor.Execute("throwupasync").ShouldBe(1);

            theOutput.ToString().ShouldContain("DivideByZeroException");
        }

        [Fact]
        public void run_with_options_if_the_options_file_does_not_exist()
        {
            executor.OptionsFile = "exec.opts";

            executor.Execute("say-name Lebron James")
                .ShouldBe(0);

            theOutput.ToString().ShouldContain("Lebron James");
        }

        [Fact]
        public void use_options_file_if_it_exists()
        {
            var path = directory.AppendPath("good.opts");
            File.WriteAllText(path, "say-name Klay Thompson");

            executor.OptionsFile = "good.opts";

            executor.Execute("")
                .ShouldBe(0);

            theOutput.ToString().ShouldContain("Klay Thompson");
        }

        [Fact]
        public void can_set_flags_in_combination_with_opts()
        {
            var path = directory.AppendPath("override.opts");
            File.WriteAllText(path, "option -b -n 1");

            executor.OptionsFile = "override.opts";

            executor.Execute("--number 5").ShouldBe(0);

            theOutput.ToString().ShouldContain("Big is true, Number is 5");
        }

        [Fact]
        public void execute_single_command_synchronously()
        {
            CommandExecutor.ExecuteCommand<OptionCommand>(new[] {"--big", "--number", "6"})
                .ShouldBe(0);

            // ShouldContain, not ShouldBe or ShouldEndWith. This buffer is filled through two
            // process-global statics, so it collects more than this test writes. Leading noise:
            // CommandFactory prints "Searching '<assembly>' for commands" until its *static*
            // _hasAppliedExtensions latch flips, so whichever test runs first picks up that banner
            // (#587, #589). Trailing noise: before Dispose restored the writers, output from a
            // later test could still land here -- CI on #659/#660 caught this assertion holding
            // "Big is True, Number is 6" followed by an unrelated "Error parsing input", even
            // though the command above had already returned 0 on valid input. Dispose fixes the
            // cause; asserting on containment keeps the test honest about what the buffer is.
            theOutput.ToString().ShouldContain("Big is True, Number is 6");
        }

        [Fact]
        public async Task execute_single_command_asynchronously()
        {
            (await CommandExecutor.ExecuteCommandAsync<OptionCommand>(new[] { "--big", "--number", "7" }))
                .ShouldBe(0);

            // See execute_single_command_synchronously above for why this is ShouldContain.
            theOutput.ToString().ShouldContain("Big is True, Number is 7");
        }
    }

    public class OptionInputs
    {
        public bool BigFlag;
        public int NumberFlag;
    }

    public class OptionCommand : JasperFxCommand<OptionInputs>
    {
        public override bool Execute(OptionInputs input)
        {
            Console.WriteLine($"Big is {input.BigFlag}, Number is {input.NumberFlag}");

            return true;
        }
    }


    public class SayName
    {
        public string FirstName;
        public string LastName;
    }

    [Description("Say my name", Name = "say-name")]
    public class SayNameCommand : JasperFxCommand<SayName>
    {
        public SayNameCommand()
        {
            Usage("Capture the users name").Arguments(x => x.FirstName, x => x.LastName);
        }

        public override bool Execute(SayName input)
        {
            Console.WriteLine($"{input.FirstName} {input.LastName}");
            return true;
        }
    }

    #region sample_async_command_sample
    [Description("Say my name", Name = "say-async-name")]
    public class AsyncSayNameCommand : JasperFxAsyncCommand<SayName>
    {
        public AsyncSayNameCommand()
        {
            Usage("Capture the users name").Arguments(x => x.FirstName, x => x.LastName);
        }

        public override async Task<bool> Execute(SayName input)
        {
            await Console.Out.WriteLineAsync($"{input.FirstName} {input.LastName}");

            return true;
        }
    }
    #endregion

    public class ThrowUp
    {
    }

    public class ThrowUpCommand : JasperFxCommand<ThrowUp>
    {
        public override bool Execute(ThrowUp input)
        {
            throw new DivideByZeroException("I threw up!");
        }
    }

    public class ThrowUpAsyncCommand : JasperFxAsyncCommand<ThrowUp>
    {
        public override Task<bool> Execute(ThrowUp input)
        {
            throw new DivideByZeroException("I threw up!");
        }
    }



}
