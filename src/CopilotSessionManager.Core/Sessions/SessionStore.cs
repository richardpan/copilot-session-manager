using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CopilotSessionManager.Core.Sessions;

/// <summary>
/// SQLite-backed reader of the Copilot CLI's <c>session-store.db</c>. Opens
/// the file in <c>Mode=ReadOnly</c> with a busy timeout so concurrent writes
/// from the CLI don't cause spurious "database is locked" failures.
/// </summary>
public sealed class SessionStore : ISessionStore
{
    private const int BusyTimeoutMs = 5_000;

    private const string ListSql = """
        SELECT s.id, s.cwd, s.repository, s.branch, s.summary, s.host_type,
               s.created_at, s.updated_at,
               (SELECT COUNT(*) FROM turns t WHERE t.session_id = s.id) AS turn_count
        FROM sessions s
        ORDER BY s.updated_at DESC
        """;

    private readonly ICopilotPaths _paths;
    private readonly ILogger<SessionStore> _logger;

    public SessionStore(ICopilotPaths paths, ILogger<SessionStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SessionStoreRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var path = _paths.SessionStoreDatabasePath;
        if (!File.Exists(path))
        {
            _logger.LogDebug("Copilot session-store.db not found at {Path}; returning empty list.", path);
            return Array.Empty<SessionStoreRecord>();
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
        }.ToString();

        var results = new List<SessionStoreRecord>();

        await using var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Could not open Copilot session-store.db at {Path}.", path);
            return Array.Empty<SessionStoreRecord>();
        }

        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMs};";
            await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = ListSql;

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new SessionStoreRecord(
                    Id: reader.GetString(0),
                    Cwd: GetNullableString(reader, 1),
                    Repository: GetNullableString(reader, 2),
                    Branch: GetNullableString(reader, 3),
                    Summary: GetNullableString(reader, 4),
                    HostType: GetNullableString(reader, 5),
                    CreatedAt: ParseTimestamp(GetNullableString(reader, 6)),
                    UpdatedAt: ParseTimestamp(GetNullableString(reader, 7)),
                    TurnCount: reader.GetInt32(8)));
            }
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Failed to query Copilot session-store.db at {Path}.", path);
            return Array.Empty<SessionStoreRecord>();
        }

        return results;
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset ParseTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTimeOffset.MinValue;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var ts)
            ? ts
            : DateTimeOffset.MinValue;
    }
}
