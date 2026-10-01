using System.Globalization;
using IPTVExplorer.Core;
using IPTVExplorer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace IPTVExplorer.Tests;

public sealed class V100UpgradeTests
{
    // Reconstructed from the public v1.0.0 tag, commit 8be9f8659c1b67d5560f2d0f530d7d05caaff2c6.
    private const string V100Schema = """
        CREATE TABLE schema_migrations(version INTEGER PRIMARY KEY,applied_at TEXT NOT NULL);
        CREATE TABLE providers(
          provider_key TEXT PRIMARY KEY,type TEXT NOT NULL CHECK(type IN ('xtream','stalker')),
          display_name TEXT NOT NULL,server_url TEXT NOT NULL,portal_path TEXT NOT NULL DEFAULT '/portal.php',
          secret_reference TEXT NOT NULL,enabled INTEGER NOT NULL DEFAULT 0 CHECK(enabled IN (0,1)),
          status TEXT NOT NULL DEFAULT 'ready' CHECK(status IN ('draft','ready','error')),
          created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
        CREATE TABLE provider_categories(
          id INTEGER PRIMARY KEY AUTOINCREMENT,provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,
          catalog_type TEXT NOT NULL CHECK(catalog_type IN ('live','vod','series')),remote_id TEXT NOT NULL,
          category_name TEXT NOT NULL,normalized_name TEXT NOT NULL,selected INTEGER NOT NULL DEFAULT 1 CHECK(selected IN (0,1)),
          present INTEGER NOT NULL DEFAULT 1 CHECK(present IN (0,1)),needs_review INTEGER NOT NULL DEFAULT 0 CHECK(needs_review IN (0,1)),
          technical INTEGER NOT NULL DEFAULT 0 CHECK(technical IN (0,1)),last_seen_at TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,
          UNIQUE(provider_key,catalog_type,remote_id));
        CREATE INDEX ix_categories_name ON provider_categories(provider_key,catalog_type,normalized_name);
        CREATE TABLE provider_category_policy(
          provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,
          catalog_type TEXT NOT NULL CHECK(catalog_type IN ('live','vod','series')),
          mode TEXT NOT NULL DEFAULT 'all' CHECK(mode IN ('all','none','custom')),last_synced_at TEXT,
          selection_updated_at TEXT NOT NULL,index_synced_at TEXT,index_dirty INTEGER NOT NULL DEFAULT 0 CHECK(index_dirty IN (0,1)),
          created_at TEXT NOT NULL,updated_at TEXT NOT NULL,PRIMARY KEY(provider_key,catalog_type));
        CREATE TABLE provider_health(
          provider_key TEXT PRIMARY KEY REFERENCES providers(provider_key) ON DELETE CASCADE,state TEXT NOT NULL,
          latency_ms INTEGER,safe_message TEXT,checked_at TEXT NOT NULL);
        CREATE TABLE provider_onboarding(
          draft_id TEXT PRIMARY KEY,display_name TEXT NOT NULL,requested_type TEXT NOT NULL,server_url TEXT NOT NULL,
          secret_reference TEXT,detected_type TEXT,diagnostic_json TEXT NOT NULL DEFAULT '{}',created_at TEXT NOT NULL,expires_at TEXT NOT NULL);
        CREATE TABLE rebuild_jobs(
          id INTEGER PRIMARY KEY AUTOINCREMENT,provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,
          status TEXT NOT NULL CHECK(status IN ('queued','running','completed','failed','interrupted')),
          progress_current INTEGER NOT NULL DEFAULT 0,progress_total INTEGER NOT NULL DEFAULT 0,progress_label TEXT NOT NULL DEFAULT '',
          vod_items INTEGER NOT NULL DEFAULT 0,series_items INTEGER NOT NULL DEFAULT 0,safe_error TEXT,
          created_at TEXT NOT NULL,started_at TEXT,finished_at TEXT);
        CREATE UNIQUE INDEX ux_jobs_active_provider ON rebuild_jobs(provider_key) WHERE status IN ('queued','running');
        CREATE INDEX ix_jobs_queue ON rebuild_jobs(status,id);
        CREATE TABLE playback_history(
          provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,catalog_type TEXT NOT NULL,
          media_id TEXT NOT NULL,series_id TEXT,position_seconds REAL NOT NULL DEFAULT 0,duration_seconds REAL,
          updated_at TEXT NOT NULL,PRIMARY KEY(provider_key,catalog_type,media_id));
        CREATE TABLE playback_preferences(
          provider_key TEXT NOT NULL REFERENCES providers(provider_key) ON DELETE CASCADE,series_id TEXT NOT NULL,
          preferred_audio TEXT,secondary_audio TEXT,preferred_subtitle TEXT,subtitle_mode TEXT NOT NULL DEFAULT 'off',
          updated_at TEXT NOT NULL,PRIMARY KEY(provider_key,series_id));
        CREATE TABLE app_settings(setting_key TEXT PRIMARY KEY,value_json TEXT NOT NULL,updated_at TEXT NOT NULL);
        """;

    [Fact]
    public async Task CompletePublicV100DatabaseMigratesWithoutDataLossAndIsIdempotent()
    {
        await using var fixture = await V100Fixture.CreateAsync();
        var initializer = new DatabaseInitializer(fixture.Paths, fixture.Connections);

        await initializer.InitializeAsync();
        var firstSnapshot = await fixture.ReadPreservedSnapshotAsync();

        Assert.Equal(["stalker-v1", "xtream-v1"], (await new ProviderRepository(fixture.Connections).ListAsync()).Select(provider => provider.Key).Order().ToArray());
        Assert.Equal("xtream-v1", (await new AppSettingsRepository(fixture.Connections).GetAsync()).ActiveProviderKey);
        Assert.Equal("1,2,3,4,5", await fixture.ScalarAsync("SELECT group_concat(version, ',') FROM (SELECT version FROM schema_migrations ORDER BY version)"));
        Assert.Equal("series_artwork,catalog_snapshots,catalog_items,media_details", await fixture.ExistingCurrentTablesAsync());
        Assert.Equal(
            ["provider_key", "catalog_type", "media_id", "series_id", "position_seconds", "duration_seconds", "updated_at", "title", "series_title", "season_number", "episode_number", "poster_url", "extension"],
            await fixture.ColumnsAsync("playback_history"));
        Assert.Equal("4", await fixture.ScalarAsync("SELECT COUNT(*) FROM provider_category_policy WHERE catalog_type IN ('vod','series') AND index_dirty=1"));
        Assert.Equal("2", await fixture.ScalarAsync("SELECT COUNT(*) FROM providers"));
        Assert.Equal("2", await fixture.ScalarAsync("SELECT COUNT(*) FROM provider_categories"));
        Assert.Equal("6", await fixture.ScalarAsync("SELECT COUNT(*) FROM provider_category_policy"));
        Assert.Equal("1", await fixture.ScalarAsync("SELECT COUNT(*) FROM playback_history WHERE media_id='episode-v1' AND position_seconds=321.5"));
        Assert.Equal("1", await fixture.ScalarAsync("SELECT COUNT(*) FROM playback_preferences WHERE series_id='series-v1' AND preferred_audio='fra'"));
        Assert.Equal("1", await fixture.ScalarAsync("SELECT COUNT(*) FROM rebuild_jobs WHERE status='completed' AND vod_items=12 AND series_items=7"));
        Assert.Equal("1", await fixture.ScalarAsync("SELECT COUNT(*) FROM provider_health WHERE state='available' AND latency_ms=42"));
        Assert.Equal("1", await fixture.ScalarAsync("SELECT COUNT(*) FROM provider_onboarding WHERE draft_id='draft-v1'"));
        Assert.Equal(fixture.XtreamSecretBytes, await File.ReadAllBytesAsync(fixture.XtreamSecretPath));
        Assert.Equal(fixture.StalkerSecretBytes, await File.ReadAllBytesAsync(fixture.StalkerSecretPath));

        await initializer.InitializeAsync();

        Assert.Equal(firstSnapshot, await fixture.ReadPreservedSnapshotAsync());
        Assert.Equal("1,2,3,4,5", await fixture.ScalarAsync("SELECT group_concat(version, ',') FROM (SELECT version FROM schema_migrations ORDER BY version)"));
        Assert.Equal(fixture.XtreamSecretBytes, await File.ReadAllBytesAsync(fixture.XtreamSecretPath));
        Assert.Equal(fixture.StalkerSecretBytes, await File.ReadAllBytesAsync(fixture.StalkerSecretPath));
    }

    [Fact]
    public async Task PublicV100SearchIndexCanBeReadThenAtomicallyRebuilt()
    {
        await using var fixture = await V100Fixture.CreateEmptyAsync();
        const string providerKey = "xtream-v1";
        var indexPath = fixture.Paths.SearchIndex(providerKey);
        // v1.0.0 built its temporary index with pooling disabled before the atomic move.
        var builder = new SqliteConnectionStringBuilder { DataSource = indexPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var connection = new SqliteConnection(builder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE search_documents(catalog_type TEXT NOT NULL,remote_id TEXT NOT NULL,title TEXT NOT NULL,normalized_title TEXT NOT NULL,image_url TEXT,PRIMARY KEY(catalog_type,remote_id)); CREATE INDEX ix_search_title ON search_documents(catalog_type,normalized_title); INSERT INTO search_documents VALUES('vod','old-v1','Old V1 title','OLD V1 TITLE',NULL);";
            await command.ExecuteNonQueryAsync();
        }

        var search = new SearchService(fixture.Paths);
        Assert.Equal("old-v1", Assert.Single((await search.SearchAsync(providerKey, CatalogType.Vod, "old", 1, 20)).Items).RemoteId);
        Assert.Empty(await search.RecentlyAddedAsync(providerKey, CatalogType.Vod, 20));

        await new AtomicSearchIndex(fixture.Paths).ReplaceAsync(providerKey,
        [
            new(providerKey, CatalogType.Vod, "new-current", "New current title", "https://cdn.invalid/poster.jpg", DateTimeOffset.Parse("2026-09-30T12:00:00Z", CultureInfo.InvariantCulture))
        ]);

        Assert.Empty((await search.SearchAsync(providerKey, CatalogType.Vod, "old", 1, 20)).Items);
        Assert.Equal("new-current", Assert.Single((await search.SearchAsync(providerKey, CatalogType.Vod, "new", 1, 20)).Items).RemoteId);
        Assert.Equal("new-current", Assert.Single(await search.RecentlyAddedAsync(providerKey, CatalogType.Vod, 20)).RemoteId);
        Assert.DoesNotContain(new[] { ".tmp", ".tmp-wal", ".tmp-shm", ".previous", ".previous-wal", ".previous-shm" }, suffix => File.Exists(indexPath + suffix));
    }

    private sealed class V100Fixture : IAsyncDisposable
    {
        private V100Fixture(string root, AppPaths paths, SqliteConnectionFactory connections)
        {
            Root = root;
            Paths = paths;
            Connections = connections;
            XtreamSecretPath = Path.Combine(paths.Secrets, "11111111111111111111111111111111.bin");
            StalkerSecretPath = Path.Combine(paths.Secrets, "22222222222222222222222222222222.bin");
        }

        public string Root { get; }
        public AppPaths Paths { get; }
        public SqliteConnectionFactory Connections { get; }
        public byte[] XtreamSecretBytes { get; } = [1, 0, 0, 9, 8, 7];
        public byte[] StalkerSecretBytes { get; } = [2, 0, 0, 6, 5, 4];
        public string XtreamSecretPath { get; }
        public string StalkerSecretPath { get; }

        public static async Task<V100Fixture> CreateAsync()
        {
            var fixture = await CreateEmptyAsync();
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = fixture.Paths.Database,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = V100Schema + """
                INSERT INTO schema_migrations VALUES(1,'2026-01-01T00:00:00.000Z');
                INSERT INTO schema_migrations VALUES(2,'2026-01-01T00:00:01.000Z');
                INSERT INTO providers VALUES('xtream-v1','xtream','Xtream V1','https://xtream.invalid','/portal.php','11111111111111111111111111111111',1,'ready','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
                INSERT INTO providers VALUES('stalker-v1','stalker','Stalker V1','https://stalker.invalid','/stalker_portal/server/load.php','22222222222222222222222222222222',0,'ready','2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
                INSERT INTO provider_categories(provider_key,catalog_type,remote_id,category_name,normalized_name,selected,present,needs_review,technical,last_seen_at,created_at,updated_at)
                  VALUES('xtream-v1','vod','10','Films V1','FILMS V1',1,1,0,0,'2026-01-02T00:00:00Z','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z');
                INSERT INTO provider_categories(provider_key,catalog_type,remote_id,category_name,normalized_name,selected,present,needs_review,technical,last_seen_at,created_at,updated_at)
                  VALUES('stalker-v1','live','20','Live V1','LIVE V1',0,1,0,1,'2026-01-02T00:00:00Z','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z');
                INSERT INTO provider_category_policy VALUES('xtream-v1','live','all','2026-01-02T00:00:00Z','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z',0,'2026-01-01T00:00:00Z','2026-01-02T00:00:00Z');
                INSERT INTO provider_category_policy VALUES('xtream-v1','vod','custom','2026-01-02T00:00:00Z','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z',0,'2026-01-01T00:00:00Z','2026-01-02T00:00:00Z');
                INSERT INTO provider_category_policy VALUES('xtream-v1','series','none',NULL,'2026-01-01T00:00:00Z',NULL,0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
                INSERT INTO provider_category_policy VALUES('stalker-v1','live','none','2026-01-02T00:00:00Z','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z',0,'2026-01-01T00:00:00Z','2026-01-02T00:00:00Z');
                INSERT INTO provider_category_policy VALUES('stalker-v1','vod','all',NULL,'2026-01-01T00:00:00Z',NULL,0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
                INSERT INTO provider_category_policy VALUES('stalker-v1','series','custom',NULL,'2026-01-01T00:00:00Z',NULL,0,'2026-01-01T00:00:00Z','2026-01-01T00:00:00Z');
                INSERT INTO provider_health VALUES('xtream-v1','available',42,'fixture healthy','2026-01-02T00:00:00Z');
                INSERT INTO provider_onboarding VALUES('draft-v1','Draft V1','auto','https://draft.invalid','11111111111111111111111111111111','xtream','{"fixture":true}','2026-01-01T00:00:00Z','2026-01-02T00:00:00Z');
                INSERT INTO rebuild_jobs(provider_key,status,progress_current,progress_total,progress_label,vod_items,series_items,safe_error,created_at,started_at,finished_at)
                  VALUES('xtream-v1','completed',19,19,'Completed',12,7,NULL,'2026-01-01T00:00:00Z','2026-01-01T00:01:00Z','2026-01-01T00:02:00Z');
                INSERT INTO playback_history VALUES('xtream-v1','series','episode-v1','series-v1',321.5,1800,'2026-01-02T00:00:00Z');
                INSERT INTO playback_preferences VALUES('xtream-v1','series-v1','fra','eng','fra','forced','2026-01-02T00:00:00Z');
                INSERT INTO app_settings VALUES('app.preferences','{"activeProviderKey":"xtream-v1","interfaceLanguage":"fr","theme":"dark"}','2026-01-02T00:00:00Z');
                """;
            await command.ExecuteNonQueryAsync();
            await File.WriteAllBytesAsync(fixture.XtreamSecretPath, fixture.XtreamSecretBytes);
            await File.WriteAllBytesAsync(fixture.StalkerSecretPath, fixture.StalkerSecretBytes);
            return fixture;
        }

        public static Task<V100Fixture> CreateEmptyAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "iptv-explorer-v100-tests", Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(root);
            paths.EnsureCreated();
            return Task.FromResult(new V100Fixture(root, paths, new SqliteConnectionFactory(paths)));
        }

        public async Task<string> ScalarAsync(string sql)
        {
            await using var connection = Connections.Create();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture) ?? string.Empty;
        }

        public async Task<string[]> ColumnsAsync(string table)
        {
            var columns = new List<string>();
            await using var connection = Connections.Create();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({table})";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
            return columns.ToArray();
        }

        public async Task<string> ExistingCurrentTablesAsync()
        {
            var expected = new[] { "series_artwork", "catalog_snapshots", "catalog_items", "media_details" };
            var existing = new List<string>();
            foreach (var table in expected)
                if (await ScalarAsync($"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}'") == "1") existing.Add(table);
            return string.Join(',', existing);
        }

        public async Task<string> ReadPreservedSnapshotAsync() => string.Join('|', new[]
        {
            await ScalarAsync("SELECT group_concat(provider_key || ':' || type || ':' || display_name || ':' || secret_reference, ';') FROM (SELECT * FROM providers ORDER BY provider_key)"),
            await ScalarAsync("SELECT group_concat(provider_key || ':' || catalog_type || ':' || remote_id || ':' || selected || ':' || technical, ';') FROM (SELECT * FROM provider_categories ORDER BY provider_key,catalog_type,remote_id)"),
            await ScalarAsync("SELECT group_concat(provider_key || ':' || catalog_type || ':' || mode || ':' || index_dirty || ':' || updated_at, ';') FROM (SELECT * FROM provider_category_policy ORDER BY provider_key,catalog_type)"),
            await ScalarAsync("SELECT group_concat(provider_key || ':' || state || ':' || latency_ms, ';') FROM provider_health"),
            await ScalarAsync("SELECT group_concat(draft_id || ':' || requested_type || ':' || secret_reference, ';') FROM provider_onboarding"),
            await ScalarAsync("SELECT group_concat(provider_key || ':' || status || ':' || vod_items || ':' || series_items, ';') FROM rebuild_jobs"),
            await ScalarAsync("SELECT group_concat(provider_key || ':' || catalog_type || ':' || media_id || ':' || series_id || ':' || position_seconds || ':' || duration_seconds, ';') FROM playback_history"),
            await ScalarAsync("SELECT group_concat(provider_key || ':' || series_id || ':' || preferred_audio || ':' || subtitle_mode, ';') FROM playback_preferences"),
            await ScalarAsync("SELECT group_concat(setting_key || ':' || value_json, ';') FROM app_settings")
        });

        public ValueTask DisposeAsync()
        {
            using (var main = Connections.Create()) SqliteConnection.ClearPool(main);
            if (Directory.Exists(Paths.Indexes))
            {
                foreach (var index in Directory.EnumerateFiles(Paths.Indexes, "*.sqlite", SearchOption.TopDirectoryOnly))
                {
                    using var pool = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = index, Mode = SqliteOpenMode.ReadOnly, Pooling = true }.ConnectionString);
                    SqliteConnection.ClearPool(pool);
                }
            }
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }
}
