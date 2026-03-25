using BetterGenshinImpact.Core.Config;
using Microsoft.Data.Sqlite;
using System;
using System.Globalization;
using System.IO;

namespace BetterGenshinImpact.Persistence.Runtime;

/// <summary>
/// 运行时业务记录的 SQLite 基础设施。
/// 这部分数据直接写入 User/config.db，不再经过 UserCache 文件镜像层。
/// </summary>
internal static class RuntimePersistenceDatabase
{
    private const string MetaTableName = "runtime_store_meta";
    private static readonly object InitLock = new();
    private static bool _initialized;

    internal const string ExecutionRecordTableName = "runtime_execution_record";
    internal const string TaskProgressSessionTableName = "runtime_task_progress_session";
    internal const string TaskProgressGroupTableName = "runtime_task_progress_group";
    internal const string TaskProgressHistoryTableName = "runtime_task_progress_history";
    internal const string FarmingDailyTableName = "runtime_farming_daily";
    internal const string FarmingRecordTableName = "runtime_farming_record";

    internal static readonly string LegacyExecutionRecordDirectory =
        Path.Combine(Global.Absolute(@"log"), "ExecutionRecords");

    internal static readonly string LegacyTaskProgressDirectory =
        Global.Absolute(@"log\task_progress");

    internal static readonly string LegacyFarmingDirectory =
        Path.Combine(Global.Absolute(@"log"), "FarmingPlan");

    internal static void Initialize()
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
            EnsureMetaTable(connection);
            EnsureSchema(connection);
            _initialized = true;
        }
    }

    /// <summary>
    /// 打开运行时持久化连接，并应用最小必要的 SQLite PRAGMA。
    /// 与主配置共用同一个 config.db，但拥有独立的运行时业务表。
    /// </summary>
    internal static SqliteConnection OpenConnection()
    {
        var dbPath = UserStorage.DatabasePath;
        var dbDirectory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dbDirectory) && !Directory.Exists(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
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

    internal static void EnsureMetaTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               CREATE TABLE IF NOT EXISTS {MetaTableName} (
                                   meta_key TEXT PRIMARY KEY,
                                   meta_value TEXT NOT NULL
                               );
                               """;
        command.ExecuteNonQuery();
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               CREATE TABLE IF NOT EXISTS {ExecutionRecordTableName} (
                                   id TEXT PRIMARY KEY,
                                   date_key TEXT NOT NULL,
                                   group_name TEXT NOT NULL,
                                   project_name TEXT NOT NULL,
                                   folder_name TEXT NOT NULL,
                                   type TEXT NOT NULL,
                                   server_start_time TEXT NULL,
                                   start_time_local TEXT NOT NULL,
                                   server_end_time TEXT NULL,
                                   end_time_local TEXT NOT NULL,
                                   is_successful INTEGER NOT NULL,
                                   updated_utc TEXT NOT NULL
                               );
                               CREATE INDEX IF NOT EXISTS idx_{ExecutionRecordTableName}_date_key
                               ON {ExecutionRecordTableName}(date_key, start_time_local DESC);

                               CREATE TABLE IF NOT EXISTS {TaskProgressSessionTableName} (
                                   session_id TEXT PRIMARY KEY,
                                   start_time TEXT NOT NULL,
                                   end_time TEXT NULL,
                                   loop_enabled INTEGER NOT NULL,
                                   loop_count INTEGER NOT NULL,
                                   last_script_group_name TEXT NULL,
                                   current_script_group_name TEXT NULL,
                                   current_group_name TEXT NULL,
                                   current_project_index INTEGER NULL,
                                   current_project_name TEXT NULL,
                                   current_project_folder_name TEXT NULL,
                                   current_project_start_time TEXT NULL,
                                   current_project_end_time TEXT NULL,
                                   current_project_status INTEGER NULL,
                                   current_project_task_end INTEGER NULL,
                                   last_success_group_name TEXT NULL,
                                   last_success_project_index INTEGER NULL,
                                   last_success_project_name TEXT NULL,
                                   last_success_project_folder_name TEXT NULL,
                                   last_success_project_start_time TEXT NULL,
                                   last_success_project_end_time TEXT NULL,
                                   last_success_project_status INTEGER NULL,
                                   last_success_project_task_end INTEGER NULL,
                                   updated_utc TEXT NOT NULL
                               );
                               CREATE INDEX IF NOT EXISTS idx_{TaskProgressSessionTableName}_updated_utc
                               ON {TaskProgressSessionTableName}(updated_utc DESC);

                               CREATE TABLE IF NOT EXISTS {TaskProgressGroupTableName} (
                                   session_id TEXT NOT NULL,
                                   position INTEGER NOT NULL,
                                   group_name TEXT NOT NULL,
                                   PRIMARY KEY (session_id, position),
                                   FOREIGN KEY (session_id) REFERENCES {TaskProgressSessionTableName}(session_id) ON DELETE CASCADE
                               );

                               CREATE TABLE IF NOT EXISTS {TaskProgressHistoryTableName} (
                                   session_id TEXT NOT NULL,
                                   position INTEGER NOT NULL,
                                   group_name TEXT NOT NULL,
                                   task_end INTEGER NOT NULL,
                                   project_index INTEGER NOT NULL,
                                   project_name TEXT NOT NULL,
                                   folder_name TEXT NOT NULL,
                                   start_time TEXT NOT NULL,
                                   end_time TEXT NULL,
                                   status INTEGER NOT NULL,
                                   PRIMARY KEY (session_id, position),
                                   FOREIGN KEY (session_id) REFERENCES {TaskProgressSessionTableName}(session_id) ON DELETE CASCADE
                               );

                               CREATE TABLE IF NOT EXISTS {FarmingDailyTableName} (
                                   stat_date TEXT PRIMARY KEY,
                                   total_normal_mob_count REAL NOT NULL,
                                   total_elite_mob_count REAL NOT NULL,
                                   miyoushe_total_normal_mob_count REAL NOT NULL,
                                   miyoushe_total_elite_mob_count REAL NOT NULL,
                                   last_miyoushe_update_time TEXT NULL,
                                   travels_diary_detail_manager_update_time TEXT NULL,
                                   updated_utc TEXT NOT NULL
                               );

                               CREATE TABLE IF NOT EXISTS {FarmingRecordTableName} (
                                   record_id INTEGER PRIMARY KEY AUTOINCREMENT,
                                   stat_date TEXT NOT NULL,
                                   group_name TEXT NOT NULL,
                                   project_name TEXT NOT NULL,
                                   folder_name TEXT NOT NULL,
                                   normal_mob_count REAL NOT NULL,
                                   elite_mob_count REAL NOT NULL,
                                   timestamp TEXT NOT NULL,
                                   FOREIGN KEY (stat_date) REFERENCES {FarmingDailyTableName}(stat_date) ON DELETE CASCADE,
                                   UNIQUE(stat_date, group_name, project_name, folder_name, timestamp)
                               );
                               CREATE INDEX IF NOT EXISTS idx_{FarmingRecordTableName}_stat_date
                               ON {FarmingRecordTableName}(stat_date, timestamp);
                               """;
        command.ExecuteNonQuery();
    }

    internal static string? GetMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT meta_value FROM {MetaTableName} WHERE meta_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar()?.ToString();
    }

    internal static void SetMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               INSERT INTO {MetaTableName} (meta_key, meta_value)
                               VALUES ($key, $value)
                               ON CONFLICT(meta_key) DO UPDATE SET meta_value = excluded.meta_value;
                               """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    internal static string FormatUtc(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    internal static string FormatDateTime(DateTime value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    internal static string? FormatNullableDateTime(DateTime? value)
    {
        return value.HasValue ? FormatDateTime(value.Value) : null;
    }

    internal static string? FormatNullableOffset(DateTimeOffset? value)
    {
        return value.HasValue ? FormatUtc(value.Value) : null;
    }

    internal static DateTimeOffset? ParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    internal static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    internal static string ToRoundtrip(DateTime value)
    {
        return FormatDateTime(value);
    }

    internal static string ToRoundtrip(DateTimeOffset value)
    {
        return FormatUtc(value);
    }

    internal static DateTime? TryParseDateTime(string? value)
    {
        return ParseDateTime(value);
    }

    internal static DateTimeOffset? TryParseDateTimeOffset(string? value)
    {
        return ParseDateTimeOffset(value);
    }
}
