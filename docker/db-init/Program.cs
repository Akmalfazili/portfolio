// D28 (one-shot init service) + D29 (least-privilege app login) in one small program.
//
// Runs once, as a compose service that exits, gated on the db healthcheck and gating the api
// service in turn (service_completed_successfully). It is the ONLY thing in this stack that
// connects as `sa`, and it only does three things with that privilege:
//   1. create the `portfolio_app` SQL login at the server level, if it doesn't already exist;
//   2. apply every pending EF Core migration (PortfolioDbContext.Database.MigrateAsync) —
//      this also creates the `Portfolio` database itself on a first run, against an empty
//      mssql-data volume, which is the whole point of D28;
//   3. create the `portfolio_app` database user inside `Portfolio` and grant it db_datareader +
//      db_datawriter — DML only, never DDL. The api container never sees MSSQL_SA_PASSWORD.
//
// Idempotent by design (every step checks existence first), so re-running this service against
// an already-migrated database — e.g. every `docker compose up` after the first — is a no-op
// past the migration check, not an error.

using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Portfolio.Infrastructure.Persistence;

var host = Environment.GetEnvironmentVariable("SQL_HOST") ?? "db";
var port = Environment.GetEnvironmentVariable("SQL_PORT") ?? "1433";
var database = Environment.GetEnvironmentVariable("SQL_DATABASE") ?? "Portfolio";
var appUser = Environment.GetEnvironmentVariable("MSSQL_APP_USER") ?? "portfolio_app";

var saPassword = Environment.GetEnvironmentVariable("MSSQL_SA_PASSWORD")
    ?? throw new InvalidOperationException("MSSQL_SA_PASSWORD is not set — cannot bootstrap the database.");
var appPassword = Environment.GetEnvironmentVariable("MSSQL_APP_PASSWORD")
    ?? throw new InvalidOperationException("MSSQL_APP_PASSWORD is not set — cannot provision the app login.");

// Encrypt=True;TrustServerCertificate=True: SqlClient 6.x defaults Encrypt=true, and the
// mssql/server image serves a self-signed certificate no client trusts by default. Omitting
// TrustServerCertificate here fails with a certificate-chain error that reads like a networking
// fault, not a TLS one — see D29.
string MasterConnectionString() =>
    $"Server={host},{port};Database=master;User Id=sa;Password={saPassword};Encrypt=True;TrustServerCertificate=True";

string TargetDatabaseConnectionString(string user, string password) =>
    $"Server={host},{port};Database={database};User Id={user};Password={password};Encrypt=True;TrustServerCertificate=True";

// sa is the only login with the rights to run CREATE DATABASE (which MigrateAsync performs
// implicitly on a first run against an empty volume) as well as every subsequent DDL migration.
string MigrationConnectionString() => TargetDatabaseConnectionString("sa", saPassword);

Console.WriteLine($"[db-init] Connecting to {host}:{port} as sa (bootstrap only)...");
await WaitForServerAsync();

Console.WriteLine($"[db-init] Ensuring server login '{appUser}' exists...");
await EnsureServerLoginAsync();

Console.WriteLine($"[db-init] Applying EF Core migrations to '{database}' (creates the database on a first run)...");
await MigrateAsync();

Console.WriteLine($"[db-init] Ensuring database user '{appUser}' exists in '{database}' with db_datareader + db_datawriter (no DDL)...");
await EnsureDatabaseUserAsync();

Console.WriteLine("[db-init] Done. Migrations applied; portfolio_app provisioned with DML-only rights.");
return 0;

async Task WaitForServerAsync()
{
    // The db service's own healthcheck already gates this container's start via
    // depends_on: condition: service_healthy, so this is a short belt-and-braces retry for the
    // rare race rather than the primary readiness mechanism.
    const int maxAttempts = 10;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await using var connection = new SqlConnection(MasterConnectionString());
            await connection.OpenAsync();
            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            Console.WriteLine($"[db-init] Server not ready yet (attempt {attempt}/{maxAttempts}): {ex.Message}");
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }

    // Let the final attempt's exception propagate with the real stack trace.
    await using var final = new SqlConnection(MasterConnectionString());
    await final.OpenAsync();
}

async Task EnsureServerLoginAsync()
{
    await using var connection = new SqlConnection(MasterConnectionString());
    await connection.OpenAsync();

    // CREATE LOGIN cannot be parameterized (it is DDL, not a value expression), so the password
    // is embedded in dynamic SQL via sp_executesql — both the login name and password come only
    // from our own .env, never from user input, and the single quote is escaped defensively.
    var escapedPassword = appPassword.Replace("'", "''");
    var sql = $"""
        IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = @loginName)
        BEGIN
            EXEC('CREATE LOGIN [' + @loginName + '] WITH PASSWORD = ''{escapedPassword}'', CHECK_POLICY = ON');
        END
        """;

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.AddWithValue("@loginName", appUser);
    await command.ExecuteNonQueryAsync();
}

async Task MigrateAsync()
{
    var optionsBuilder = new DbContextOptionsBuilder<PortfolioDbContext>();
    optionsBuilder.UseSqlServer(
        MigrationConnectionString(),
        sql => sql.MigrationsAssembly(typeof(PortfolioDbContext).Assembly.FullName));

    await using var context = new PortfolioDbContext(optionsBuilder.Options);
    // Migrate() creates the database itself if it does not exist (sa holds sysadmin, so this
    // has the rights to do so), then applies every migration not yet in __EFMigrationsHistory.
    await context.Database.MigrateAsync();
}

async Task EnsureDatabaseUserAsync()
{
    await using var connection = new SqlConnection(TargetDatabaseConnectionString("sa", saPassword));
    await connection.OpenAsync();

    const string sql = """
        IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = @userName)
        BEGIN
            EXEC('CREATE USER [' + @userName + '] FOR LOGIN [' + @userName + ']');
            EXEC('ALTER ROLE db_datareader ADD MEMBER [' + @userName + ']');
            EXEC('ALTER ROLE db_datawriter ADD MEMBER [' + @userName + ']');
        END
        """;

    await using var command = new SqlCommand(sql, connection);
    command.Parameters.AddWithValue("@userName", appUser);
    await command.ExecuteNonQueryAsync();
}
