﻿namespace JasperFx.CommandLine;

public class CommandRun
{
    public IJasperFxCommand Command { get; set; }
    public object Input { get; set; }

    public Task<bool> Execute()
    {
        return Command.Execute(Input);
    }
}