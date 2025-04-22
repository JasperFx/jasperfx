using System;
using ExtensionCommands;
using JasperFx;
using JasperFx.CommandLine;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;

[assembly:JasperFxAssembly(typeof(ExtensionServices))]

namespace ExtensionCommands
{
    public class ExtensionServices : IServiceRegistrations
    {
        public void Configure(IServiceCollection services)
        {
            services.AddSingleton<IExtensionService, ExtensionService>();
            services.AddSingleton<IStatefulResource>(new ExtensionResource());
        }
    }
    
    public interface IExtensionService{}
    public class ExtensionService : IExtensionService{}
    
    public class ExtensionInput
    {
        
    }

    public class ExtensionResource : StatefulResourceBase
    {
        public ExtensionResource() : base("Extension", "The Extension", new Uri("extension://one"), new Uri("resource://one"))
        {
        }
    }
    
    [Description("An extension command loaded from another assembly", Name = "extension")]
    public class ExtensionCommand : JasperFxCommand<ExtensionInput>
    {
        public override bool Execute(ExtensionInput input)
        {
            Console.WriteLine("I'm an extension command");
            return true;
        }
    }
    
    [Description("A second extension command loaded from another assembly", Name = "extension2")]
    public class Extension2Command : JasperFxCommand<ExtensionInput>
    {
        public override bool Execute(ExtensionInput input)
        {
            Console.WriteLine("I'm an extension command");
            return true;
        }
    }
}