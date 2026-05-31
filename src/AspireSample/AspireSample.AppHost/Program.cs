using JasperFx.Aspire;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
var appdb = postgres.AddDatabase("appdb");

builder.AddProject<Projects.AspireSample_Api>("api")
    .WithReference(appdb)
    .WaitFor(appdb)
    // The whole point: JasperFx command buttons on the "api" resource tile. IncludeMutatingCommands
    // also adds "Apply resources" / "Rebuild projections" / "Write generated code", each gated by a
    // confirmation prompt.
    .WithJasperFxCommands(opts =>
    {
        opts.IncludeMutatingCommands = true;
    });

builder.Build().Run();
