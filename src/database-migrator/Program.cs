using Azure.Core;
using Azure.Identity;
using BankingAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

const string PostgreSqlScope = "https://ossrdbms-aad.database.windows.net/.default";

var host = RequiredSetting("POSTGRESQL_HOST");
var database = RequiredSetting("POSTGRESQL_DATABASE");
var migratorUser = RequiredSetting("POSTGRESQL_MIGRATOR_USER");
var runtimePrincipalName = RequiredSetting("POSTGRESQL_RUNTIME_PRINCIPAL_NAME");
var runtimePrincipalId = RequiredSetting("POSTGRESQL_RUNTIME_PRINCIPAL_ID");
var managedIdentityClientId = RequiredSetting("AZURE_CLIENT_ID");

var credential = new ManagedIdentityCredential(
    ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId));
var token = await credential.GetTokenAsync(
    new TokenRequestContext([PostgreSqlScope]));

await EnsureRuntimePrincipalAsync(
    host,
    migratorUser,
    token.Token,
    runtimePrincipalName,
    runtimePrincipalId);

var applicationConnectionString = ConnectionString(
    host,
    database,
    migratorUser,
    token.Token);
var options = new DbContextOptionsBuilder<BankingAgentDbContext>()
    .UseNpgsql(applicationConnectionString)
    .Options;

await using (var context = new BankingAgentDbContext(options))
{
    await context.Database.MigrateAsync();
}

await GrantRuntimePrivilegesAsync(
    applicationConnectionString,
    database,
    migratorUser,
    runtimePrincipalName);

Console.WriteLine("Database migrations and runtime grants completed successfully.");
return;

static string RequiredSetting(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    return string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"{name} is required.")
        : value;
}

static string ConnectionString(
    string host,
    string database,
    string username,
    string accessToken)
{
    return new NpgsqlConnectionStringBuilder
    {
        Host = host,
        Database = database,
        Username = username,
        Password = accessToken,
        SslMode = SslMode.Require,
        Timeout = 30,
        CommandTimeout = 120,
        Pooling = false
    }.ConnectionString;
}

static async Task EnsureRuntimePrincipalAsync(
    string host,
    string migratorUser,
    string accessToken,
    string runtimePrincipalName,
    string runtimePrincipalId)
{
    await using var connection = new NpgsqlConnection(
        ConnectionString(host, "postgres", migratorUser, accessToken));
    await connection.OpenAsync();

    await using var exists = connection.CreateCommand();
    exists.CommandText = """
        SELECT EXISTS (
            SELECT 1
            FROM pg_catalog.pgaadauth_list_principals(false)
            WHERE "objectId" = @object_id
        );
        """;
    exists.Parameters.AddWithValue("object_id", runtimePrincipalId);
    if (await exists.ExecuteScalarAsync() is true)
    {
        return;
    }

    await using var create = connection.CreateCommand();
    create.CommandText = """
        SELECT *
        FROM pg_catalog.pgaadauth_create_principal_with_oid(
            @role_name,
            @object_id,
            'service',
            false,
            false
        );
        """;
    create.Parameters.AddWithValue("role_name", runtimePrincipalName);
    create.Parameters.AddWithValue("object_id", runtimePrincipalId);
    await create.ExecuteNonQueryAsync();
}

static async Task GrantRuntimePrivilegesAsync(
    string connectionString,
    string database,
    string migratorUser,
    string runtimePrincipalName)
{
    var databaseName = QuoteIdentifier(database);
    var runtimeRole = QuoteIdentifier(runtimePrincipalName);
    var migratorRole = QuoteIdentifier(migratorUser);

    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = $"""
        GRANT CONNECT ON DATABASE {databaseName} TO {runtimeRole};
        GRANT USAGE ON SCHEMA public TO {runtimeRole};
        GRANT SELECT, INSERT, UPDATE, DELETE
            ON ALL TABLES IN SCHEMA public TO {runtimeRole};
        GRANT USAGE, SELECT, UPDATE
            ON ALL SEQUENCES IN SCHEMA public TO {runtimeRole};
        ALTER DEFAULT PRIVILEGES FOR ROLE {migratorRole} IN SCHEMA public
            GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {runtimeRole};
        ALTER DEFAULT PRIVILEGES FOR ROLE {migratorRole} IN SCHEMA public
            GRANT USAGE, SELECT, UPDATE ON SEQUENCES TO {runtimeRole};
        """;
    await command.ExecuteNonQueryAsync();
}

static string QuoteIdentifier(string value) =>
    $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
