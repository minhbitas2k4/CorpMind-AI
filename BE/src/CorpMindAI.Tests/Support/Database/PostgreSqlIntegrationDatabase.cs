using System.Text.RegularExpressions;
using CorpMindAI.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CorpMindAI.Tests.Support.Database;

internal sealed class PostgreSqlIntegrationDatabase : IAsyncDisposable
{
    internal const string ConnectionEnvironmentVariable = "CORPMIND_CHUNKING_TEST_CONNECTION";
    private static readonly Regex DatabaseNamePattern = new(
        "^corpmind_chunking_it_[a-f0-9]{32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _adminConnectionString;
    private readonly string _databaseName;
    private bool _disposed;

    private PostgreSqlIntegrationDatabase(
        string adminConnectionString,
        string databaseName,
        string connectionString)
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        ConnectionString = connectionString;
    }

    public string ConnectionString { get; }

    public CorpMindDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CorpMindDbContext>()
            .UseNpgsql(ConnectionString, builder =>
                builder.MigrationsAssembly("CorpMindAI.Infrastructure"))
            .Options;
        return new CorpMindDbContext(options);
    }

    public static async Task<PostgreSqlIntegrationDatabase> CreateAsync()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);
        Skip.If(
            string.IsNullOrWhiteSpace(adminConnectionString),
            $"Set {ConnectionEnvironmentVariable} to a PostgreSQL connection that may create temporary databases.");

        var adminBuilder = new NpgsqlConnectionStringBuilder(adminConnectionString!);
        var databaseName = $"corpmind_chunking_it_{Guid.NewGuid():N}";
        ValidateDatabaseName(databaseName);

        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        var testBuilder = new NpgsqlConnectionStringBuilder(adminBuilder.ConnectionString)
        {
            Database = databaseName,
            Pooling = false
        };
        var database = new PostgreSqlIntegrationDatabase(
            adminBuilder.ConnectionString,
            databaseName,
            testBuilder.ConnectionString);

        try
        {
            await using var context = database.CreateContext();
            await context.Database.MigrateAsync();
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        ValidateDatabaseName(_databaseName);
        NpgsqlConnection.ClearAllPools();

        var adminBuilder = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Pooling = false
        };
        await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
        await admin.OpenAsync();

        await using (var terminate = admin.CreateCommand())
        {
            terminate.CommandText = """
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = @database_name AND pid <> pg_backend_pid()
                """;
            terminate.Parameters.AddWithValue("database_name", _databaseName);
            await terminate.ExecuteNonQueryAsync();
        }

        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\"";
        await drop.ExecuteNonQueryAsync();
    }

    private static void ValidateDatabaseName(string databaseName)
    {
        if (!DatabaseNamePattern.IsMatch(databaseName))
            throw new InvalidOperationException("Refusing to manage a database outside the integration-test namespace.");
    }
}
