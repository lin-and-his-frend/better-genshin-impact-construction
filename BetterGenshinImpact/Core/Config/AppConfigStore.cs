using BetterGenshinImpact.Service;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// Direct SQLite-backed application settings store.
///
/// Design goals for the v2 settings schema:
/// 1. The database is the single source of truth for structured application settings.
/// 2. Each top-level settings aggregate gets its own table to avoid the old "main JSON + split JSON" dual-write model.
/// 3. Writes are transactional across all settings tables so partial updates do not leave the database in a split-brain state.
/// 4. Legacy JSON/blob tables remain readable for one-time migration only; new writes never go back to the old schema.
/// </summary>
internal static class AppConfigStore
{
    private const string SchemaMetaTable = "schema_meta";
    private const string AppProfileTable = "app_profile";
    private const string CoreSettingsTable = "app_setting_core";
    private const string SettingsSchemaVersionKey = "settings_schema_version";
    private const string SettingsMigratedAtKey = "settings_migrated_at";
    private const string SettingsMigrationSourceKey = "settings_migration_source";
    private const string CurrentSchemaVersion = "2";
    private const long DefaultProfileId = 1;

    private const string LegacyConfigFileName = "config.json";
    private const string LegacyAppConfigTable = "app_config";
    private const string LegacyAppConfigKeyColumn = "config_key";
    private const string LegacyAppConfigValueColumn = "config_value";
    private const string LegacyAppConfigUpdatedColumn = "updated_utc";
    private const string LegacyMainConfigKey = "main";
    private const string LegacyUserFilesTable = "user_files";
    private const string LegacyUserFilesPathColumn = "path";
    private const string LegacyUserFilesContentColumn = "content";
    private const string LegacyUserFilesIsTextColumn = "is_text";
    private const string LegacyUserFilesUpdatedColumn = "updated_utc";

    private static readonly object InitLock = new();
    private static bool _initialized;

    private static readonly SettingSectionDefinition[] Sections =
    [
        new(nameof(AllConfig.MaskWindowConfig), "app_setting_mask_window"),
        new(nameof(AllConfig.CommonConfig), "app_setting_common"),
        new(nameof(AllConfig.WebRemoteConfig), "app_setting_web_remote"),
        new(nameof(AllConfig.McpConfig), "app_setting_mcp"),
        new(nameof(AllConfig.AiConfig), "app_setting_ai"),
        new(nameof(AllConfig.GenshinStartConfig), "app_setting_genshin_start"),
        new(nameof(AllConfig.AutoPickConfig), "app_setting_auto_pick"),
        new(nameof(AllConfig.AutoSkipConfig), "app_setting_auto_skip"),
        new(nameof(AllConfig.AutoFishingConfig), "app_setting_auto_fishing"),
        new(nameof(AllConfig.QuickTeleportConfig), "app_setting_quick_teleport"),
        new(nameof(AllConfig.AutoCookConfig), "app_setting_auto_cook"),
        new(nameof(AllConfig.AutoGeniusInvokationConfig), "app_setting_auto_genius_invokation"),
        new(nameof(AllConfig.AutoWoodConfig), "app_setting_auto_wood"),
        new(nameof(AllConfig.AutoFightConfig), "app_setting_auto_fight"),
        new(nameof(AllConfig.AutoMusicGameConfig), "app_setting_auto_music_game"),
        new(nameof(AllConfig.AutoDomainConfig), "app_setting_auto_domain"),
        new(nameof(AllConfig.AutoStygianOnslaughtConfig), "app_setting_auto_stygian_onslaught"),
        new(nameof(AllConfig.AutoArtifactSalvageConfig), "app_setting_auto_artifact_salvage"),
        new(nameof(AllConfig.AutoEatConfig), "app_setting_auto_eat"),
        new(nameof(AllConfig.AutoLeyLineOutcropConfig), "app_setting_auto_leyline_outcrop"),
        new(nameof(AllConfig.MapMaskConfig), "app_setting_map_mask"),
        new(nameof(AllConfig.SkillCdConfig), "app_setting_skill_cd"),
        new(nameof(AllConfig.AutoRedeemCodeConfig), "app_setting_auto_redeem_code"),
        new(nameof(AllConfig.GetGridIconsConfig), "app_setting_get_grid_icons"),
        new(nameof(AllConfig.MacroConfig), "app_setting_macro"),
        new(nameof(AllConfig.RecordConfig), "app_setting_record"),
        new(nameof(AllConfig.ScriptConfig), "app_setting_script"),
        new(nameof(AllConfig.PathingConditionConfig), "app_setting_pathing_condition"),
        new(nameof(AllConfig.HotKeyConfig), "app_setting_hot_key"),
        new(nameof(AllConfig.NotificationConfig), "app_setting_notification"),
        new(nameof(AllConfig.KeyBindingsConfig), "app_setting_key_bindings"),
        new(nameof(AllConfig.OtherConfig), "app_setting_other"),
        new(nameof(AllConfig.TpConfig), "app_setting_tp"),
        new(nameof(AllConfig.DevConfig), "app_setting_dev"),
        new(nameof(AllConfig.HardwareAccelerationConfig), "app_setting_hardware_acceleration")
    ];

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            using var connection = OpenConnection();
            EnsureSchema(connection);
            if (!HasSettingsData(connection))
            {
                TryMigrateLegacySettings(connection);
            }

            _initialized = true;
        }
    }

    public static bool Exists()
    {
        try
        {
            Initialize();
            using var connection = OpenConnection();
            return HasSettingsData(connection);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryRead(out AllConfig? config)
    {
        config = null;
        try
        {
            Initialize();
            using var connection = OpenConnection();
            if (!HasSettingsData(connection))
            {
                return false;
            }

            config = ReadInternal(connection);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryReadJson(out string? json)
    {
        json = null;
        if (!TryRead(out var config) || config == null)
        {
            return false;
        }

        try
        {
            json = JsonSerializer.Serialize(config, ConfigService.JsonOptions);
            return true;
        }
        catch
        {
            json = null;
            return false;
        }
    }

    public static bool TryWrite(AllConfig config, DateTimeOffset? updatedUtc = null)
    {
        try
        {
            Initialize();
            using var connection = OpenConnection();
            SaveInternal(connection, config, updatedUtc);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryWriteJson(string json, DateTimeOffset? updatedUtc = null)
    {
        try
        {
            var config = JsonSerializer.Deserialize<AllConfig>(json, ConfigService.JsonOptions);
            if (config == null)
            {
                return false;
            }

            return TryWrite(config, updatedUtc);
        }
        catch
        {
            return false;
        }
    }

    public static DateTimeOffset? GetLatestUpdatedUtc()
    {
        try
        {
            Initialize();
            using var connection = OpenConnection();
            DateTimeOffset? latest = null;
            foreach (var tableName in EnumerateSettingsTables())
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT updated_utc FROM {tableName} WHERE profile_id = $profileId;";
                command.Parameters.AddWithValue("$profileId", DefaultProfileId);
                var value = TryParseDateTimeOffset(command.ExecuteScalar()?.ToString());
                if (value != null && (latest == null || value > latest))
                {
                    latest = value;
                }
            }

            return latest;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateSettingsTables()
    {
        yield return CoreSettingsTable;
        foreach (var section in Sections)
        {
            yield return section.TableName;
        }
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        var sql = new StringBuilder();
        sql.AppendLine(
            $"""
             CREATE TABLE IF NOT EXISTS {SchemaMetaTable} (
                 meta_key TEXT PRIMARY KEY,
                 meta_value TEXT NOT NULL
             );
             CREATE TABLE IF NOT EXISTS {AppProfileTable} (
                 profile_id INTEGER PRIMARY KEY,
                 profile_name TEXT NOT NULL,
                 created_utc TEXT NOT NULL,
                 updated_utc TEXT NOT NULL
             );
             CREATE TABLE IF NOT EXISTS {CoreSettingsTable} (
                 profile_id INTEGER PRIMARY KEY,
                 payload_json TEXT NOT NULL,
                 updated_utc TEXT NOT NULL,
                 FOREIGN KEY (profile_id) REFERENCES {AppProfileTable}(profile_id) ON DELETE CASCADE
             );
             """);

        foreach (var section in Sections)
        {
            sql.AppendLine(
                $"""
                 CREATE TABLE IF NOT EXISTS {section.TableName} (
                     profile_id INTEGER PRIMARY KEY,
                     payload_json TEXT NOT NULL,
                     updated_utc TEXT NOT NULL,
                     FOREIGN KEY (profile_id) REFERENCES {AppProfileTable}(profile_id) ON DELETE CASCADE
                 );
                 """);
        }

        command.CommandText = sql.ToString();
        command.ExecuteNonQuery();
        SetMeta(connection, SettingsSchemaVersionKey, CurrentSchemaVersion);
    }

    private static bool HasSettingsData(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {CoreSettingsTable} WHERE profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", DefaultProfileId);
        return command.ExecuteScalar() != null;
    }

    /// <summary>
    /// Legacy migration is intentionally one-way:
    /// - read from the old config blob / split blob / disk fallback
    /// - write once into the new direct-write schema
    /// - never write back into legacy tables
    /// </summary>
    private static void TryMigrateLegacySettings(SqliteConnection connection)
    {
        if (!TryReadLegacyConfigJson(connection, out var json, out var updatedUtc, out var source) ||
            string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var config = JsonSerializer.Deserialize<AllConfig>(json, ConfigService.JsonOptions);
            if (config == null)
            {
                return;
            }

            SaveInternal(connection, config, updatedUtc ?? DateTimeOffset.UtcNow);
            SetMeta(connection, SettingsMigratedAtKey, (updatedUtc ?? DateTimeOffset.UtcNow).ToString("O"));
            SetMeta(connection, SettingsMigrationSourceKey, source ?? "unknown");
        }
        catch
        {
            // Migration is best-effort. Failing here should not block startup.
        }
    }

    private static bool TryReadLegacyConfigJson(
        SqliteConnection connection,
        out string? json,
        out DateTimeOffset? updatedUtc,
        out string? source)
    {
        json = null;
        updatedUtc = null;
        source = null;

        if (TryReadLegacyMainConfigJson(connection, out json, out updatedUtc))
        {
            source = "legacy_app_config";
            return true;
        }

        if (TryReadLegacySplitConfigJson(connection, out json, out updatedUtc))
        {
            source = "legacy_split_config";
            return true;
        }

        if (TryReadLegacyUserFileConfigJson(connection, out json, out updatedUtc))
        {
            source = "legacy_user_files";
            return true;
        }

        var diskConfigPath = Path.Combine(Global.UserDataRoot, LegacyConfigFileName);
        if (!File.Exists(diskConfigPath))
        {
            return false;
        }

        try
        {
            json = File.ReadAllText(diskConfigPath, Encoding.UTF8);
            updatedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(diskConfigPath), TimeSpan.Zero);
            source = "legacy_disk_config";
            return !string.IsNullOrWhiteSpace(json);
        }
        catch
        {
            json = null;
            updatedUtc = null;
            source = null;
            return false;
        }
    }

    private static bool TryReadLegacyMainConfigJson(
        SqliteConnection connection,
        out string? json,
        out DateTimeOffset? updatedUtc)
    {
        json = null;
        updatedUtc = null;
        if (!TableExists(connection, LegacyAppConfigTable))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT {LegacyAppConfigValueColumn}, {LegacyAppConfigUpdatedColumn}
                               FROM {LegacyAppConfigTable}
                               WHERE {LegacyAppConfigKeyColumn} = $key;
                               """;
        command.Parameters.AddWithValue("$key", LegacyMainConfigKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        json = reader[LegacyAppConfigValueColumn]?.ToString();
        updatedUtc = TryParseDateTimeOffset(reader[LegacyAppConfigUpdatedColumn]?.ToString());
        return !string.IsNullOrWhiteSpace(json);
    }

    private static bool TryReadLegacySplitConfigJson(
        SqliteConnection connection,
        out string? json,
        out DateTimeOffset? updatedUtc)
    {
        json = null;
        updatedUtc = null;
        var merged = new JsonObject();
        var hasData = false;

        foreach (var tableName in new[]
                 {
                     "config_general",
                     "config_automation",
                     "config_hotkeys",
                     "config_notification",
                     "config_ai",
                     "config_hardware"
                 })
        {
            if (!TableExists(connection, tableName))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT config_value, updated_utc FROM {tableName} WHERE config_key = $key;";
            command.Parameters.AddWithValue("$key", LegacyMainConfigKey);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                continue;
            }

            var sectionJson = reader["config_value"]?.ToString();
            if (string.IsNullOrWhiteSpace(sectionJson))
            {
                continue;
            }

            if (TryParseDateTimeOffset(reader["updated_utc"]?.ToString()) is { } parsedUpdated &&
                (updatedUtc == null || parsedUpdated > updatedUtc))
            {
                updatedUtc = parsedUpdated;
            }

            try
            {
                if (JsonNode.Parse(sectionJson) is not JsonObject sectionObject)
                {
                    continue;
                }

                foreach (var pair in sectionObject)
                {
                    merged[pair.Key] = pair.Value?.DeepClone();
                }

                hasData = true;
            }
            catch
            {
                // Ignore malformed legacy split sections and continue with other sections.
            }
        }

        if (!hasData)
        {
            return false;
        }

        json = merged.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return true;
    }

    private static bool TryReadLegacyUserFileConfigJson(
        SqliteConnection connection,
        out string? json,
        out DateTimeOffset? updatedUtc)
    {
        json = null;
        updatedUtc = null;
        if (!TableExists(connection, LegacyUserFilesTable))
        {
            return false;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT {LegacyUserFilesContentColumn}, {LegacyUserFilesIsTextColumn}, {LegacyUserFilesUpdatedColumn}
                               FROM {LegacyUserFilesTable}
                               WHERE {LegacyUserFilesPathColumn} = $path;
                               """;
        command.Parameters.AddWithValue("$path", LegacyConfigFileName);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader[LegacyUserFilesIsTextColumn] is not long isText || isText != 1)
        {
            return false;
        }

        if (reader[LegacyUserFilesContentColumn] is not byte[] bytes || bytes.Length == 0)
        {
            return false;
        }

        json = Encoding.UTF8.GetString(bytes);
        updatedUtc = TryParseDateTimeOffset(reader[LegacyUserFilesUpdatedColumn]?.ToString());
        return !string.IsNullOrWhiteSpace(json);
    }

    private static AllConfig ReadInternal(SqliteConnection connection)
    {
        var config = new AllConfig();
        if (TryReadPayload<RootSettingsState>(connection, CoreSettingsTable, out var rootState) && rootState != null)
        {
            ApplyRootState(config, rootState);
        }

        foreach (var section in Sections)
        {
            if (TryReadSection(connection, section, out var value) && value != null)
            {
                section.Property.SetValue(config, value);
            }
        }

        return config;
    }

    private static bool TryReadPayload<T>(SqliteConnection connection, string tableName, out T? payload)
    {
        payload = default;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload_json FROM {tableName} WHERE profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", DefaultProfileId);
        var json = command.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            payload = JsonSerializer.Deserialize<T>(json, ConfigService.JsonOptions);
            return payload != null;
        }
        catch
        {
            payload = default;
            return false;
        }
    }

    private static bool TryReadSection(SqliteConnection connection, SettingSectionDefinition section, out object? value)
    {
        value = null;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT payload_json FROM {section.TableName} WHERE profile_id = $profileId;";
        command.Parameters.AddWithValue("$profileId", DefaultProfileId);
        var json = command.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize(json, section.Property.PropertyType, ConfigService.JsonOptions);
            return value != null;
        }
        catch
        {
            value = null;
            return false;
        }
    }

    private static void SaveInternal(SqliteConnection connection, AllConfig config, DateTimeOffset? updatedUtc)
    {
        var effectiveUpdatedUtc = updatedUtc ?? DateTimeOffset.UtcNow;
        using var transaction = connection.BeginTransaction();
        EnsureProfile(connection, transaction, effectiveUpdatedUtc);

        var rootJson = JsonSerializer.Serialize(CreateRootState(config), ConfigService.JsonOptions);
        UpsertPayload(connection, transaction, CoreSettingsTable, rootJson, effectiveUpdatedUtc);

        foreach (var section in Sections)
        {
            var value = section.Property.GetValue(config) ?? Activator.CreateInstance(section.Property.PropertyType);
            var payloadJson = JsonSerializer.Serialize(value, section.Property.PropertyType, ConfigService.JsonOptions);
            UpsertPayload(connection, transaction, section.TableName, payloadJson, effectiveUpdatedUtc);
        }

        transaction.Commit();
    }

    private static void EnsureProfile(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset updatedUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
                               INSERT INTO {AppProfileTable} (profile_id, profile_name, created_utc, updated_utc)
                               VALUES ($profileId, $name, $createdUtc, $updatedUtc)
                               ON CONFLICT(profile_id) DO UPDATE SET
                                   profile_name = excluded.profile_name,
                                   updated_utc = excluded.updated_utc;
                               """;
        command.Parameters.AddWithValue("$profileId", DefaultProfileId);
        command.Parameters.AddWithValue("$name", "default");
        command.Parameters.AddWithValue("$createdUtc", updatedUtc.ToString("O"));
        command.Parameters.AddWithValue("$updatedUtc", updatedUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void UpsertPayload(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string payloadJson,
        DateTimeOffset updatedUtc)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
                               INSERT INTO {tableName} (profile_id, payload_json, updated_utc)
                               VALUES ($profileId, $payloadJson, $updatedUtc)
                               ON CONFLICT(profile_id) DO UPDATE SET
                                   payload_json = excluded.payload_json,
                                   updated_utc = excluded.updated_utc;
                               """;
        command.Parameters.AddWithValue("$profileId", DefaultProfileId);
        command.Parameters.AddWithValue("$payloadJson", payloadJson);
        command.Parameters.AddWithValue("$updatedUtc", updatedUtc.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static RootSettingsState CreateRootState(AllConfig config)
    {
        return new RootSettingsState
        {
            CaptureMode = config.CaptureMode,
            DetailedErrorLogs = config.DetailedErrorLogs,
            NotShowNewVersionNoticeEndVersion = config.NotShowNewVersionNoticeEndVersion,
            TriggerInterval = config.TriggerInterval,
            AutoFixWin11BitBlt = config.AutoFixWin11BitBlt,
            NextScheduledTask = config.NextScheduledTask,
            NextScriptGroupName = config.NextScriptGroupName,
            SelectedOneDragonFlowConfigName = config.SelectedOneDragonFlowConfigName
        };
    }

    private static void ApplyRootState(AllConfig config, RootSettingsState state)
    {
        config.CaptureMode = state.CaptureMode;
        config.DetailedErrorLogs = state.DetailedErrorLogs;
        config.NotShowNewVersionNoticeEndVersion = state.NotShowNewVersionNoticeEndVersion;
        config.TriggerInterval = state.TriggerInterval;
        config.AutoFixWin11BitBlt = state.AutoFixWin11BitBlt;
        config.NextScheduledTask = state.NextScheduledTask ?? [];
        config.NextScriptGroupName = state.NextScriptGroupName ?? string.Empty;
        config.SelectedOneDragonFlowConfigName = state.SelectedOneDragonFlowConfigName ?? string.Empty;
    }

    private static void SetMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               INSERT INTO {SchemaMetaTable} (meta_key, meta_value)
                               VALUES ($key, $value)
                               ON CONFLICT(meta_key) DO UPDATE SET meta_value = excluded.meta_value;
                               """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name = $tableName;";
        command.Parameters.AddWithValue("$tableName", tableName);
        return command.ExecuteScalar() != null;
    }

    private static SqliteConnection OpenConnection()
    {
        if (!Directory.Exists(Global.UserDataRoot))
        {
            Directory.CreateDirectory(Global.UserDataRoot);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = UserStorage.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = """
                             PRAGMA journal_mode = WAL;
                             PRAGMA synchronous = NORMAL;
                             PRAGMA busy_timeout = 3000;
                             PRAGMA foreign_keys = ON;
                             """;
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static DateTimeOffset? TryParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    private sealed class RootSettingsState
    {
        public string CaptureMode { get; set; } = new AllConfig().CaptureMode;
        public bool DetailedErrorLogs { get; set; }
        public string NotShowNewVersionNoticeEndVersion { get; set; } = string.Empty;
        public int TriggerInterval { get; set; } = 50;
        public bool AutoFixWin11BitBlt { get; set; } = true;
        public List<ValueTuple<string, int, string, string>>? NextScheduledTask { get; set; } = [];
        public string NextScriptGroupName { get; set; } = string.Empty;
        public string SelectedOneDragonFlowConfigName { get; set; } = string.Empty;
    }

    private sealed class SettingSectionDefinition
    {
        public SettingSectionDefinition(string propertyName, string tableName)
        {
            PropertyName = propertyName;
            TableName = tableName;
            Property = typeof(AllConfig).GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)
                       ?? throw new InvalidOperationException($"Unable to resolve AllConfig property '{propertyName}'.");
        }

        public string PropertyName { get; }

        public string TableName { get; }

        public PropertyInfo Property { get; }
    }
}
