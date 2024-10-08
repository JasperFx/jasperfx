﻿namespace JasperFx.CommandLine;

public class OaktonOptions
{
    public string OptionsFile { get; set; }
    public Action<CommandFactory> Factory { get; set; }
    public string DefaultCommand { get; set; } = "run";
}