using CorpMindAI.Application.Interfaces;
using Npgsql;

namespace CorpMindAI.Infrastructure.Services;

// Uses a PostgreSQL session advisory lock to serialize chunking by document.
// The dedicated connection remains open for the complete execution.
public sealed class PostgreSqlChunkingExecutionLock : IChunkingExecutionLock
{
    private const int LockNamespace = 0x434D4348; // "CMCH"
    private readonly string _connectionString;

    public PostgreSqlChunkingExecutionLock(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString));

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Multiplexing = false
        };
        _connectionString = builder.ConnectionString;
    }

    public async Task<IAsyncDisposable> AcquireAsync(
        int documentId,
        CancellationToken cancellationToken = default)
    {
        if (documentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(documentId));

        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_lock(@namespace, @document_id)";
            command.Parameters.AddWithValue("namespace", LockNamespace);
            command.Parameters.AddWithValue("document_id", documentId);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new AdvisoryLockHandle(connection, documentId);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class AdvisoryLockHandle : IAsyncDisposable
    {
        private readonly int _documentId;
        private NpgsqlConnection? _connection;

        public AdvisoryLockHandle(NpgsqlConnection connection, int documentId)
        {
            _connection = connection;
            _documentId = documentId;
        }

        public async ValueTask DisposeAsync()
        {
            var connection = Interlocked.Exchange(ref _connection, null);
            if (connection is null)
                return;

            try
            {
                if (connection.State == System.Data.ConnectionState.Open)
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT pg_advisory_unlock(@namespace, @document_id)";
                    command.Parameters.AddWithValue("namespace", LockNamespace);
                    command.Parameters.AddWithValue("document_id", _documentId);
                    await command.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                await connection.DisposeAsync();
            }
        }
    }
}
