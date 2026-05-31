using JasperFx.Aspire;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres");
var appdb = postgres.AddDatabase("appdb");

builder.AddProject<Projects.AspireSample_Api>("api")
    .WithReference(appdb)
    .WaitFor(appdb)
    // A1 — on-demand JasperFx command buttons on the "api" resource tile. IncludeMutatingCommands
    // also adds "Apply resources" / "Rebuild projections" / "Write generated code", each gated by a
    // confirmation prompt.
    .WithJasperFxCommands(opts =>
    {
        opts.IncludeMutatingCommands = true;
    })
    // A2 — startup gates: run-to-completion resources that finish BEFORE the api starts. References
    // and WaitFor above are declared first so each gate inherits them (connection string + wait-for-db).
    .WithJasperFxStartup(c =>
    {
        c.Run("resources", "setup"); // provision stateful resources before the api boots
        c.Check();                   // verify the DB connection (blocking) before the api boots
    });

builder.Build().Run();
