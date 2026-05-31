using JasperFx;
using JasperFx.Environment;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// An environment check that actually needs the Aspire-managed connection string. Clicking
// "Check environment" in the dashboard spawns this app with `check-env`, and this check only
// passes if the child process received the injected `ConnectionStrings__appdb` value — which is
// exactly the JasperFx.Aspire env-resolution mechanic being exercised end to end.
builder.Services.CheckEnvironment("Can connect to the Aspire-managed 'appdb' Postgres database",
    async (_, cancellationToken) =>
    {
        var connectionString = builder.Configuration.GetConnectionString("appdb")
            ?? throw new InvalidOperationException(
                "No 'appdb' connection string was provided — the Aspire-managed environment did not flow through.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
    });

var app = builder.Build();

app.MapGet("/", () => "JasperFx.Aspire sample API. Try the command buttons on this resource in the Aspire dashboard.");

return await app.RunJasperFxCommands(args);
