// See https://aka.ms/new-console-template for more information

using System.Reflection;
using JasperFx.CommandLine;
using JasperFx.Core;
using JasperFx.Descriptors;
using Spectre.Console;

var options = new OptionsDescription(new SomeOptions());
OptionDescriptionWriter.Write(options);


public class SomeOptions
{
    public string Name { get; set; } = "Jeremy";
    public TimeSpan Timeout { get; set; } = 5.Minutes();
    public int Age { get; set; } = 51;
    public double Percentage { get; set; } = .35;
    public Color Color { get; set; } = Color.Aqua;
    public bool IsGood { get; set; } = true;
    public bool IsDone { get; set; }
    public Type WhatType { get; set; } = typeof(SomeOptions);
    public string[] Tags { get; set; } = ["one", "two", "three"];
    public Assembly ApplicationAssembly { get; set; } = Assembly.GetEntryAssembly();
}