using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Infrastructure;

public sealed class SqliteConnectionFactory(AppPaths paths)
{
    public SqliteConnection Create()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        return new SqliteConnection(builder.ConnectionString);
    }
}

public sealed class DatabaseInitializer(AppPaths paths, SqliteConnectionFactory connections)
{
    private const string Migration = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            applied_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS providers (
            provider_key TEXT PRIMARY KEY,
            type TEXT NOT NULL CHECK (type IN ('xtream','stalker')),
            display_name TEXT NOT NULL,
            server_url TEXT NOT NULL,
            portal_path TEXT NOT NULL DEFAULT '/portal.php',
            secret_reference TEXT NOT NULL,
            enabled INTEGER NOT NULL DEFAULT 0 CHECK (enabled IN (0,1)),
            status TEXT NOT NULL DEFAULT 'ready' CHECK (status IN ('draft','ready','error')),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS provider_categories (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,
            catalog_type TEXT NOT NULL CHECK (catalog_type IN ('live','vod','series')),
            remote_id TEXT NOT NULL,
            category_name TEXT NOT NULL,
            normalized_name TEXT NOT NULL,
            selected INTEGER NOT NULL DEFAULT 1 CHECK (selected IN (0,1)),
            present INTEGER NOT NULL DEFAULT 1 CHECK (present IN (0,1)),
            needs_review INTEGER NOT NULL DEFAULT 0 CHECK (needs_review IN (0,1)),
            technical INTEGER NOT NULL DEFAULT 0 CHECK (technical IN (0,1)),
            last_seen_at TEXT,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE(provider_key, catalog_type, remote_id)
        );
        CREATE INDEX IF NOT EXISTS ix_categories_name ON provider_categories(provider_key,catalog_type,normalized_name);

        CREATE TABLE IF NOT EXISTS provider_category_policy (
            provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,
            catalog_type TEXT NOT NULL CHECK (catalog_type IN ('live','vod','series')),
            mode TEXT NOT NULL DEFAULT 'all' CHECK (mode IN ('all','none','custom')),
            last_synced_at TEXT,
            selection_updated_at TEXT NOT NULL,
            index_synced_at TEXT,
            index_dirty INTEGER NOT NULL DEFAULT 0 CHECK (index_dirty IN (0,1)),
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(provider_key,catalog_type)
        );

        CREATE TABLE IF NOT EXISTS provider_health (
            provider_key TEXT PRIMARY KEY REFERENCES providers(provider_key) ON DELETE CASCADE,
            state TEXT NOT NULL,
            latency_ms INTEGER,
            safe_message TEXT,
            checked_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS provider_onboarding (
            draft_id TEXT PRIMARY KEY,
            display_name TEXT NOT NULL,
            requested_type TEXT NOT NULL,
            server_url TEXT NOT NULL,
            secret_reference TEXT,
            detected_type TEXT,
            diagnostic_json TEXT NOT NULL DEFAULT '{}',
            created_at TEXT NOT NULL,
            expires_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS rebuild_jobs (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,
            status TEXT NOT NULL CHECK (status IN ('queued','running','completed','failed','interrupted')),
            progress_current INTEGER NOT NULL DEFAULT 0,
            progress_total INTEGER NOT NULL DEFAULT 0,
            progress_label TEXT NOT NULL DEFAULT '',
            vod_items INTEGER NOT NULL DEFAULT 0,
            series_items INTEGER NOT NULL DEFAULT 0,
            safe_error TEXT,
            created_at TEXT NOT NULL,
            started_at TEXT,
            finished_at TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_active_provider ON rebuild_jobs(provider_key) WHERE status IN ('queued','running');
        CREATE INDEX IF NOT EXISTS ix_jobs_queue ON rebuild_jobs(status,id);

        CREATE TABLE IF NOT EXISTS playback_history (
            provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,
            catalog_type TEXT NOT NULL,
            media_id TEXT NOT NULL,
            series_id TEXT,
            title TEXT NOT NULL DEFAULT '',
            series_title TEXT,
            season_number INTEGER,
            episode_number INTEGER,
            poster_url TEXT,
            extension TEXT,
            position_seconds REAL NOT NULL DEFAULT 0,
            duration_seconds REAL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY(provider_key,catalog_type,media_id)
        );
        CREATE INDEX IF NOT EXISTS ix_playback_history_provider_updated ON playback_history(provider_key,updated_at DESC);

        CREATE TABLE IF NOT EXISTS playback_preferences (
            provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,
            series_id TEXT NOT NULL,
            preferred_audio TEXT,
            secondary_audio TEXT,
            preferred_subtitle TEXT,
            subtitle_mode TEXT NOT NULL DEFAULT 'off',
            updated_at TEXT NOT NULL,
            PRIMARY KEY(provider_key,series_id)
        );

        CREATE TABLE IF NOT EXISTS app_settings (
            setting_key TEXT PRIMARY KEY,
            value_json TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        INSERT OR IGNORE INTO schema_migrations(version,applied_at) VALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ','now'));
        """;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        await using var connection = connections.Create();
        await connection.OpenAsync(cancellationToken);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.CommandText = Migration;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "provider_categories", "technical", "INTEGER NOT NULL DEFAULT 0 CHECK (technical IN (0,1))", cancellationToken);
        await using var version = connection.CreateCommand(); version.CommandText = "INSERT OR IGNORE INTO schema_migrations(version,applied_at) VALUES(2,strftime('%Y-%m-%dT%H:%M:%fZ','now'))"; await version.ExecuteNonQueryAsync(cancellationToken);
        await EnsureColumnAsync(connection, "playback_history", "title", "TEXT NOT NULL DEFAULT ''", cancellationToken);
        await EnsureColumnAsync(connection, "playback_history", "series_title", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "playback_history", "season_number", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, "playback_history", "episode_number", "INTEGER", cancellationToken);
        await EnsureColumnAsync(connection, "playback_history", "poster_url", "TEXT", cancellationToken);
        await EnsureColumnAsync(connection, "playback_history", "extension", "TEXT", cancellationToken);
        await using (var historyIndex = connection.CreateCommand())
        {
            historyIndex.CommandText = "CREATE INDEX IF NOT EXISTS ix_playback_history_provider_updated ON playback_history(provider_key,updated_at DESC)";
            await historyIndex.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var playbackVersion = connection.CreateCommand())
        {
            playbackVersion.CommandText = "INSERT OR IGNORE INTO schema_migrations(version,applied_at) VALUES(3,strftime('%Y-%m-%dT%H:%M:%fZ','now'))";
            if (await playbackVersion.ExecuteNonQueryAsync(cancellationToken) == 1)
            {
                await using var invalidateIndexes = connection.CreateCommand();
                invalidateIndexes.CommandText = "UPDATE provider_category_policy SET index_dirty=1,updated_at=strftime('%Y-%m-%dT%H:%M:%fZ','now') WHERE catalog_type IN ('vod','series')";
                await invalidateIndexes.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column, string declaration, CancellationToken cancellationToken)
    {
        await using var info = connection.CreateCommand(); info.CommandText = $"PRAGMA table_info({table})";
        var found = false;
        await using (var reader = await info.ExecuteReaderAsync(cancellationToken)) while (await reader.ReadAsync(cancellationToken)) if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal)) found = true;
        if (found) return;
        await using var alter = connection.CreateCommand(); alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}"; await alter.ExecuteNonQueryAsync(cancellationToken);
    }
}
