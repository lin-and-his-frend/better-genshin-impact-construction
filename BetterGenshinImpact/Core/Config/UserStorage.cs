using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;

namespace BetterGenshinImpact.Core.Config;

internal static class UserStorage
{
    private const string DatabaseFileName = "config.db";
    private const string LegacyConfigFileName = "config.json";
    private const string UserFilesTable = "user_files";
    private const string MetaTable = "user_meta";
    private const string AppConfigTable = "app_config";
    private const string AppConfigKeyColumn = "config_key";
    private const string AppConfigValueColumn = "config_value";
    private const string AppConfigUpdatedColumn = "updated_utc";
    private const string MainConfigKey = "main";
    private const string ConfigGeneralTable = "config_general";
    private const string ConfigAutomationTable = "config_automation";
    private const string ConfigHotkeysTable = "config_hotkeys";
    private const string ConfigNotificationTable = "config_notification";
    private const string ConfigAiTable = "config_ai";
    private const string ConfigHardwareTable = "config_hardware";
    private const string ConfigOneDragonTable = "config_onedragon";
    private const string ConfigSectionValueColumn = "config_value";
    private const string ConfigSectionUpdatedColumn = "updated_utc";
    private const string OneDragonNameColumn = "config_name";
    private const string OneDragonValueColumn = "config_value";
    private const string OneDragonUpdatedColumn = "updated_utc";
    private const string ConfigEntriesTable = "config_entries";
    private const string ConfigEntriesKeyColumn = "config_key";
    private const string IgnoreLegacyConfigMetaKey = "ignore_legacy_config";
    private const string MigratedMainConfigMetaKey = "migrated_main_config";
    private const string MigratedSplitConfigMetaKey = "migrated_split_config";
    private const string MigratedOneDragonConfigMetaKey = "migrated_onedragon_config";
    private static readonly object InitLock = new();
    private static readonly ReaderWriterLockSlim Lock = new();
    private static bool _initialized;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".json", ".json5", ".js", ".mjs", ".ts", ".lua", ".md", ".html", ".htm", ".css", ".xml", ".csv", ".yaml", ".yml", ".ini", ".config"
    };

    private static readonly JsonSerializerOptions JsonWriteIndentedOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonDocumentOptions JsonReadDocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly ConfigSection[] SplitSections =
    [
        ConfigSection.General,
        ConfigSection.Automation,
        ConfigSection.Hotkeys,
        ConfigSection.Notification,
        ConfigSection.Ai,
        ConfigSection.Hardware
    ];

    private static readonly HashSet<string> AutomationKeys = new(StringComparer.Ordinal)
    {
        "autoPickConfig",
        "autoSkipConfig",
        "autoFishingConfig",
        "quickTeleportConfig",
        "autoCookConfig",
        "autoGeniusInvokationConfig",
        "autoWoodConfig",
        "autoFightConfig",
        "autoMusicGameConfig",
        "autoDomainConfig",
        "autoStygianOnslaughtConfig",
        "autoArtifactSalvageConfig",
        "autoEatConfig",
        "autoLeyLineOutcropConfig",
        "mapMaskConfig",
        "skillCdConfig",
        "autoRedeemCodeConfig",
        "getGridIconsConfig"
    };

    private static readonly HashSet<string> HotkeysKeys = new(StringComparer.Ordinal)
    {
        "hotKeyConfig",
        "keyBindingsConfig"
    };

    private static readonly HashSet<string> NotificationKeys = new(StringComparer.Ordinal)
    {
        "notificationConfig"
    };

    private static readonly HashSet<string> AiKeys = new(StringComparer.Ordinal)
    {
        "aiConfig",
        "mcpConfig",
        "webRemoteConfig"
    };

    private static readonly HashSet<string> HardwareKeys = new(StringComparer.Ordinal)
    {
        "hardwareAccelerationConfig"
    };

    public static string DatabasePath => Path.Combine(Global.UserDataRoot, DatabaseFileName);

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
            MigrateFromLegacyTable(connection);
            MigrateFromDisk(connection);
            MigrateMainConfig(connection);
            MigrateSplitConfig(connection);
            MigrateOneDragonConfig(connection);
            DeleteIgnoredEntries(connection);
            _initialized = true;
        }
    }

    public static bool TryReadText(string path, out string? content)
    {
        content = null;
        if (!TryNormalizeUserPath(path, out var normalized))
        {
            return false;
        }

        if (IsIgnoredPath(normalized))
        {
            return false;
        }

        Initialize();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT content, is_text FROM {UserFilesTable} WHERE path = $path;";
            command.Parameters.AddWithValue("$path", normalized);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            var isText = reader.GetInt32(1) == 1;
            if (!isText)
            {
                return false;
            }

            var bytes = (byte[])reader["content"];
            content = Encoding.UTF8.GetString(bytes);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static bool TryReadBytes(string path, out byte[]? content, out DateTimeOffset? updatedUtc)
    {
        content = null;
        updatedUtc = null;
        if (!TryNormalizeUserPath(path, out var normalized))
        {
            return false;
        }

        if (IsIgnoredPath(normalized))
        {
            return false;
        }

        Initialize();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT content, updated_utc FROM {UserFilesTable} WHERE path = $path;";
            command.Parameters.AddWithValue("$path", normalized);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return false;
            }

            content = (byte[])reader["content"];
            updatedUtc = TryParseDateTimeOffset(reader["updated_utc"]?.ToString());
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static bool TryWriteText(string path, string content, DateTimeOffset? updatedUtc = null)
    {
        return TryWriteBytes(path, Encoding.UTF8.GetBytes(content), true, updatedUtc);
    }

    public static bool TryWriteBytes(string path, byte[] content, bool isText, DateTimeOffset? updatedUtc = null)
    {
        if (!TryNormalizeUserPath(path, out var normalized))
        {
            return false;
        }

        if (IsIgnoredPath(normalized))
        {
            return false;
        }

        Initialize();
        Lock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            UpsertEntry(connection, normalized, content, isText, updatedUtc);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitWriteLock();
        }
    }

    public static bool TryReadMainConfig(out string? content)
    {
        content = null;
        Initialize();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            string? rawMainJson = null;
            if (TryReadRawMainConfigInternal(connection, out var mainJson, out _))
            {
                rawMainJson = mainJson;
            }

            if (TryReadSplitConfigAsMainInternal(connection, out var splitJson))
            {
                if (!string.IsNullOrWhiteSpace(rawMainJson) &&
                    !string.IsNullOrWhiteSpace(splitJson) &&
                    TryMergeJsonObjects(rawMainJson, splitJson, out var merged))
                {
                    content = merged;
                    return true;
                }

                content = splitJson;
                return !string.IsNullOrWhiteSpace(content);
            }

            content = rawMainJson;
            return !string.IsNullOrWhiteSpace(content);
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static bool TryWriteMainConfig(string content, DateTimeOffset? updatedUtc = null)
    {
        Initialize();
        Lock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            UpsertMainConfig(connection, content, updatedUtc);
            UpsertSplitSectionsFromMainJson(connection, content, updatedUtc);
            SetMeta(connection, MigratedSplitConfigMetaKey, "1");
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitWriteLock();
        }
    }

    public static bool MainConfigExists()
    {
        Initialize();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            return SplitConfigExistsInternal(connection) || MainConfigExistsInternal(connection);
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static DateTimeOffset? GetMainConfigUpdatedUtc()
    {
        Initialize();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            DateTimeOffset? latest = GetSplitConfigLatestUpdatedUtcInternal(connection);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {AppConfigUpdatedColumn} FROM {AppConfigTable} WHERE {AppConfigKeyColumn} = $key;";
            command.Parameters.AddWithValue("$key", MainConfigKey);
            var mainUpdated = TryParseDateTimeOffset(command.ExecuteScalar()?.ToString());
            if (mainUpdated == null)
            {
                return latest;
            }

            if (latest == null || mainUpdated > latest)
            {
                return mainUpdated;
            }

            return latest;
        }
        catch
        {
            return null;
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static bool TryReadConfigSection(ConfigSection section, out string? content)
    {
        content = null;
        Initialize();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            return TryReadConfigSectionInternal(connection, section, out content, out _);
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static bool TryWriteConfigSection(ConfigSection section, string content, DateTimeOffset? updatedUtc = null)
    {
        Initialize();
        Lock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            UpsertConfigSection(connection, section, content, updatedUtc);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitWriteLock();
        }
    }

    public static bool SplitConfigExists()
    {
        Initialize();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            return SplitConfigExistsInternal(connection);
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static bool HasOneDragonConfig()
    {
        Initialize();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT 1 FROM {ConfigOneDragonTable} LIMIT 1;";
            return command.ExecuteScalar() != null;
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static IReadOnlyList<NamedConfigEntry> ListOneDragonConfigs()
    {
        Initialize();
        var configs = new List<NamedConfigEntry>();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   SELECT {OneDragonNameColumn}, {OneDragonValueColumn}, {OneDragonUpdatedColumn}
                                   FROM {ConfigOneDragonTable}
                                   ORDER BY {OneDragonUpdatedColumn};
                                   """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var name = reader[OneDragonNameColumn]?.ToString();
                var value = reader[OneDragonValueColumn]?.ToString();
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                configs.Add(new NamedConfigEntry(name, value, TryParseDateTimeOffset(reader[OneDragonUpdatedColumn]?.ToString())));
            }
        }
        catch
        {
            return configs;
        }
        finally
        {
            Lock.ExitReadLock();
        }

        return configs;
    }

    public static bool TryReadOneDragonConfig(string name, out string? content)
    {
        content = null;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        Initialize();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   SELECT {OneDragonValueColumn}
                                   FROM {ConfigOneDragonTable}
                                   WHERE {OneDragonNameColumn} = $name COLLATE NOCASE;
                                   """;
            command.Parameters.AddWithValue("$name", name);
            var value = command.ExecuteScalar()?.ToString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            content = value;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static bool TryWriteOneDragonConfig(string name, string content, DateTimeOffset? updatedUtc = null)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        Initialize();
        Lock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            UpsertOneDragonConfig(connection, name, content, updatedUtc);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitWriteLock();
        }
    }

    public static bool DeleteOneDragonConfig(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        Initialize();
        Lock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   DELETE FROM {ConfigOneDragonTable}
                                   WHERE {OneDragonNameColumn} = $name COLLATE NOCASE;
                                   """;
            command.Parameters.AddWithValue("$name", name);
            command.ExecuteNonQuery();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitWriteLock();
        }
    }

    public static DateTimeOffset? GetOneDragonLatestUpdatedUtc()
    {
        Initialize();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT MAX({OneDragonUpdatedColumn}) FROM {ConfigOneDragonTable};";
            return TryParseDateTimeOffset(command.ExecuteScalar()?.ToString());
        }
        catch
        {
            return null;
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static bool Exists(string path)
    {
        if (!TryNormalizeUserPath(path, out var normalized))
        {
            return false;
        }

        if (IsIgnoredPath(normalized))
        {
            return false;
        }

        Initialize();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            return ExistsInternal(connection, normalized);
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static bool Delete(string path)
    {
        if (!TryNormalizeUserPath(path, out var normalized))
        {
            return false;
        }

        if (IsIgnoredPath(normalized))
        {
            return false;
        }

        Initialize();
        Lock.EnterWriteLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"DELETE FROM {UserFilesTable} WHERE path = $path;";
            command.Parameters.AddWithValue("$path", normalized);
            command.ExecuteNonQuery();
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Lock.ExitWriteLock();
        }
    }

    public static DateTimeOffset? GetLastWriteTimeUtc(string path)
    {
        if (!TryNormalizeUserPath(path, out var normalized))
        {
            return null;
        }

        if (IsIgnoredPath(normalized))
        {
            return null;
        }

        Initialize();
        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT updated_utc FROM {UserFilesTable} WHERE path = $path;";
            command.Parameters.AddWithValue("$path", normalized);
            var result = command.ExecuteScalar()?.ToString();
            return TryParseDateTimeOffset(result);
        }
        catch
        {
            return null;
        }
        finally
        {
            Lock.ExitReadLock();
        }
    }

    public static IReadOnlyList<UserFileEntry> ListEntries(string prefix = "", bool recursive = true)
    {
        Initialize();
        var entries = new List<UserFileEntry>();
        if (!TryNormalizeUserPrefix(prefix, out var normalizedPrefix))
        {
            return entries;
        }

        Lock.EnterReadLock();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            if (string.IsNullOrEmpty(normalizedPrefix))
            {
                command.CommandText = $"SELECT path, length(content) as size, is_text, updated_utc FROM {UserFilesTable};";
            }
            else
            {
                var like = normalizedPrefix + "%";
                command.CommandText = $"SELECT path, length(content) as size, is_text, updated_utc FROM {UserFilesTable} WHERE path LIKE $prefix;";
                command.Parameters.AddWithValue("$prefix", like);
            }

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader["path"].ToString() ?? string.Empty;
                if (IsIgnoredPath(path))
                {
                    continue;
                }

                if (!ShouldInclude(path, normalizedPrefix, recursive))
                {
                    continue;
                }

                var size = reader["size"] is long len ? len : 0;
                var isText = reader["is_text"] is long textFlag && textFlag == 1;
                var updated = TryParseDateTimeOffset(reader["updated_utc"]?.ToString());
                entries.Add(new UserFileEntry(path, size, isText, updated));
            }
        }
        catch
        {
            return entries;
        }
        finally
        {
            Lock.ExitReadLock();
        }

        return entries;
    }

    public static bool TryNormalizeUserPath(string path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var userRoot = Global.UserDataRoot;
            string fullPath;
            if (Path.IsPathRooted(path))
            {
                fullPath = Path.GetFullPath(path);
                if (!fullPath.StartsWith(userRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else
            {
                var trimmed = path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (trimmed.StartsWith($"User{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("User/", StringComparison.OrdinalIgnoreCase))
                {
                    trimmed = trimmed.Substring(5);
                }

                fullPath = Path.GetFullPath(Path.Combine(userRoot, trimmed));
                if (!fullPath.StartsWith(userRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            normalized = Path.GetRelativePath(userRoot, fullPath)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return !string.IsNullOrEmpty(normalized) && normalized != ".";
        }
        catch
        {
            return false;
        }

    }

    public static bool IsTextFile(string path)
    {
        var extension = Path.GetExtension(path);
        return TextExtensions.Contains(extension);
    }

    internal static bool IsTemporaryPath(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        var tempPrefix = $"Temp{Path.DirectorySeparatorChar}";
        var altPrefix = $"Temp{Path.AltDirectorySeparatorChar}";
        return normalizedPath.Equals("Temp", StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(tempPrefix, StringComparison.OrdinalIgnoreCase)
               || normalizedPath.StartsWith(altPrefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsIgnoredPath(string normalizedPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return true;
        }

        // 忽略临时文件和数据库文件
        if (IsTemporaryPath(normalizedPath) ||
            string.Equals(normalizedPath, DatabaseFileName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 忽略特定的配置文件 - 这些文件应该直接存储在磁盘上
        var ignoredFiles = new[]
        {
            "pick_black_lists.json",
            "pick_white_lists.json",
            "avatar_macro_default.json"
        };

        foreach (var file in ignoredFiles)
        {
            if (normalizedPath.Equals(file, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // 忽略脚本文件目录 - 这些文件应该直接存储在磁盘上，不需要数据库管理
        var scriptDirs = new[]
        {
            "JsScript",
            "KeyMouseScript",
            "AutoFight",
            "AutoGeniusInvokation",
            "AutoPathing",
            "ScriptGroup",
            "OneDragon",
            "Images"
        };

        foreach (var dir in scriptDirs)
        {
            var dirPrefix = $"{dir}{Path.DirectorySeparatorChar}";
            var altDirPrefix = $"{dir}{Path.AltDirectorySeparatorChar}";
            if (normalizedPath.Equals(dir, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase) ||
                normalizedPath.StartsWith(altDirPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool LegacyConfigFileExists()
    {
        var path = Path.Combine(Global.UserDataRoot, LegacyConfigFileName);
        return File.Exists(path);
    }

    internal static void DeleteLegacyConfigFile()
    {
        var path = Path.Combine(Global.UserDataRoot, LegacyConfigFileName);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore cleanup failures.
        }
    }

    internal static void MarkLegacyConfigIgnored()
    {
        using var connection = OpenConnection();
        EnsureSchema(connection);
        SetMeta(connection, IgnoreLegacyConfigMetaKey, "1");
    }

    private static bool TryNormalizeUserPrefix(string path, out string normalizedPrefix)
    {
        normalizedPrefix = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        if (!TryNormalizeUserPath(path, out normalizedPrefix))
        {
            return false;
        }

        if (!normalizedPrefix.EndsWith(Path.DirectorySeparatorChar))
        {
            normalizedPrefix += Path.DirectorySeparatorChar;
        }

        return true;
    }

    private static bool ShouldInclude(string path, string prefix, bool recursive)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return true;
        }

        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (recursive)
        {
            return true;
        }

        var remainder = path.Substring(prefix.Length);
        return !remainder.Contains(Path.DirectorySeparatorChar);
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               CREATE TABLE IF NOT EXISTS {UserFilesTable} (
                                   path TEXT PRIMARY KEY,
                                   content BLOB NOT NULL,
                                   is_text INTEGER NOT NULL,
                                   updated_utc TEXT NOT NULL
                               );
                               CREATE TABLE IF NOT EXISTS {MetaTable} (
                                   meta_key TEXT PRIMARY KEY,
                                   meta_value TEXT NOT NULL
                               );
                               CREATE TABLE IF NOT EXISTS {AppConfigTable} (
                                   {AppConfigKeyColumn} TEXT PRIMARY KEY,
                                   {AppConfigValueColumn} TEXT NOT NULL,
                                   {AppConfigUpdatedColumn} TEXT NOT NULL
                               );
                               CREATE TABLE IF NOT EXISTS {ConfigGeneralTable} (
                                   {AppConfigKeyColumn} TEXT PRIMARY KEY,
                                   {ConfigSectionValueColumn} TEXT NOT NULL,
                                   {ConfigSectionUpdatedColumn} TEXT NOT NULL
                               );
                               CREATE TABLE IF NOT EXISTS {ConfigAutomationTable} (
                                   {AppConfigKeyColumn} TEXT PRIMARY KEY,
                                   {ConfigSectionValueColumn} TEXT NOT NULL,
                                   {ConfigSectionUpdatedColumn} TEXT NOT NULL
                               );
                               CREATE TABLE IF NOT EXISTS {ConfigHotkeysTable} (
                                   {AppConfigKeyColumn} TEXT PRIMARY KEY,
                                   {ConfigSectionValueColumn} TEXT NOT NULL,
                                   {ConfigSectionUpdatedColumn} TEXT NOT NULL
                               );
                               CREATE TABLE IF NOT EXISTS {ConfigNotificationTable} (
                                   {AppConfigKeyColumn} TEXT PRIMARY KEY,
                                   {ConfigSectionValueColumn} TEXT NOT NULL,
                                   {ConfigSectionUpdatedColumn} TEXT NOT NULL
                               );
                               CREATE TABLE IF NOT EXISTS {ConfigAiTable} (
                                   {AppConfigKeyColumn} TEXT PRIMARY KEY,
                                   {ConfigSectionValueColumn} TEXT NOT NULL,
                                   {ConfigSectionUpdatedColumn} TEXT NOT NULL
                               );
                               CREATE TABLE IF NOT EXISTS {ConfigHardwareTable} (
                                   {AppConfigKeyColumn} TEXT PRIMARY KEY,
                                   {ConfigSectionValueColumn} TEXT NOT NULL,
                                   {ConfigSectionUpdatedColumn} TEXT NOT NULL
                               );
                               CREATE TABLE IF NOT EXISTS {ConfigOneDragonTable} (
                                   {OneDragonNameColumn} TEXT PRIMARY KEY,
                                   {OneDragonValueColumn} TEXT NOT NULL,
                                   {OneDragonUpdatedColumn} TEXT NOT NULL
                               );
                               """;
        command.ExecuteNonQuery();
    }

    private static void MigrateFromLegacyTable(SqliteConnection connection)
    {
        if (GetMeta(connection, "migrated_config_entries") == "1")
        {
            return;
        }

        if (!TableExists(connection, ConfigEntriesTable))
        {
            SetMeta(connection, "migrated_config_entries", "1");
            return;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {ConfigEntriesKeyColumn}, value FROM {ConfigEntriesTable};";
            using var reader = command.ExecuteReader();
            var ignoreLegacyConfig = GetMeta(connection, IgnoreLegacyConfigMetaKey) == "1";
            while (reader.Read())
            {
                var key = reader[ConfigEntriesKeyColumn]?.ToString();
                var value = reader["value"]?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (!TryNormalizeUserPath(key, out var normalized))
                {
                    continue;
                }

                if (IsIgnoredPath(normalized))
                {
                    continue;
                }

                if (ignoreLegacyConfig && normalized.Equals(LegacyConfigFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ExistsInternal(connection, normalized))
                {
                    continue;
                }

                UpsertEntry(connection, normalized, Encoding.UTF8.GetBytes(value), true, DateTimeOffset.UtcNow);
            }
        }
        finally
        {
            SetMeta(connection, "migrated_config_entries", "1");
        }
    }

    private static void MigrateFromDisk(SqliteConnection connection)
    {
        if (GetMeta(connection, "migrated_disk") == "1")
        {
            return;
        }

        var userRoot = Global.UserDataRoot;
        if (!Directory.Exists(userRoot))
        {
            SetMeta(connection, "migrated_disk", "1");
            return;
        }

        var files = Directory.GetFiles(userRoot, "*", SearchOption.AllDirectories);
        var ignoreLegacyConfig = GetMeta(connection, IgnoreLegacyConfigMetaKey) == "1";
        foreach (var file in files)
        {
            if (string.Equals(Path.GetFileName(file), DatabaseFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = Path.GetRelativePath(userRoot, file);
            if (!TryNormalizeUserPath(relative, out var normalized))
            {
                continue;
            }

            if (IsIgnoredPath(normalized))
            {
                continue;
            }

            if (ignoreLegacyConfig && normalized.Equals(LegacyConfigFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            byte[] bytes;
            DateTimeOffset updatedUtc;
            try
            {
                bytes = File.ReadAllBytes(file);
                updatedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
            }
            catch
            {
                continue;
            }

            var existingUpdatedUtc = GetEntryUpdatedUtc(connection, normalized);
            if (existingUpdatedUtc == null || updatedUtc > existingUpdatedUtc)
            {
                UpsertEntry(connection, normalized, bytes, IsTextFile(file), updatedUtc);
            }
        }

        SetMeta(connection, "migrated_disk", "1");
    }

    private static void MigrateMainConfig(SqliteConnection connection)
    {
        if (GetMeta(connection, MigratedMainConfigMetaKey) == "1")
        {
            return;
        }

        try
        {
            if (MainConfigExistsInternal(connection))
            {
                DeleteUserFileEntry(connection, LegacyConfigFileName);
                SetMeta(connection, IgnoreLegacyConfigMetaKey, "1");
                return;
            }

            var ignoreLegacyConfig = GetMeta(connection, IgnoreLegacyConfigMetaKey) == "1";
            if (ignoreLegacyConfig)
            {
                DeleteUserFileEntry(connection, LegacyConfigFileName);
                return;
            }

            if (TryReadUserFileText(connection, LegacyConfigFileName, out var json, out var updatedUtc) &&
                !string.IsNullOrWhiteSpace(json))
            {
                UpsertMainConfig(connection, json, updatedUtc);
                DeleteUserFileEntry(connection, LegacyConfigFileName);
                SetMeta(connection, IgnoreLegacyConfigMetaKey, "1");
            }
        }
        finally
        {
            SetMeta(connection, MigratedMainConfigMetaKey, "1");
        }
    }

    private static void MigrateSplitConfig(SqliteConnection connection)
    {
        if (GetMeta(connection, MigratedSplitConfigMetaKey) == "1")
        {
            return;
        }

        var markMigrated = false;
        try
        {
            if (SplitConfigExistsInternal(connection))
            {
                markMigrated = true;
                return;
            }

            if (TryReadRawMainConfigInternal(connection, out var json, out var updatedUtc) &&
                !string.IsNullOrWhiteSpace(json))
            {
                markMigrated = UpsertSplitSectionsFromMainJson(connection, json, updatedUtc);
                return;
            }

            markMigrated = true;
        }
        finally
        {
            if (markMigrated)
            {
                SetMeta(connection, MigratedSplitConfigMetaKey, "1");
            }
        }
    }

    private static void MigrateOneDragonConfig(SqliteConnection connection)
    {
        if (GetMeta(connection, MigratedOneDragonConfigMetaKey) == "1")
        {
            return;
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   SELECT path, content, updated_utc
                                   FROM {UserFilesTable}
                                   WHERE is_text = 1
                                     AND (path LIKE $prefix OR path LIKE $altPrefix)
                                     AND lower(path) LIKE $suffix;
                                   """;
            command.Parameters.AddWithValue("$prefix", $"OneDragon{Path.DirectorySeparatorChar}%");
            command.Parameters.AddWithValue("$altPrefix", "OneDragon/%");
            command.Parameters.AddWithValue("$suffix", "%.json");

            var migratedPaths = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var path = reader["path"]?.ToString();
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                if (reader["content"] is not byte[] bytes || bytes.Length == 0)
                {
                    continue;
                }

                var json = Encoding.UTF8.GetString(bytes);
                if (string.IsNullOrWhiteSpace(json))
                {
                    continue;
                }

                var name = ResolveOneDragonConfigName(path, json);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var updatedUtc = TryParseDateTimeOffset(reader["updated_utc"]?.ToString());
                UpsertOneDragonConfig(connection, name, json, updatedUtc);
                migratedPaths.Add(path);
            }

            foreach (var path in migratedPaths)
            {
                DeleteUserFileEntry(connection, path);
            }
        }
        finally
        {
            SetMeta(connection, MigratedOneDragonConfigMetaKey, "1");
        }
    }

    private static bool ExistsInternal(SqliteConnection connection, string normalizedPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {UserFilesTable} WHERE path = $path;";
        command.Parameters.AddWithValue("$path", normalizedPath);
        return command.ExecuteScalar() != null;
    }

    private static bool MainConfigExistsInternal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT 1 FROM {AppConfigTable} WHERE {AppConfigKeyColumn} = $key;";
        command.Parameters.AddWithValue("$key", MainConfigKey);
        return command.ExecuteScalar() != null;
    }

    private static bool SplitConfigExistsInternal(SqliteConnection connection)
    {
        foreach (var section in SplitSections)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   SELECT 1
                                   FROM {GetConfigSectionTableName(section)}
                                   WHERE {AppConfigKeyColumn} = $key;
                                   """;
            command.Parameters.AddWithValue("$key", MainConfigKey);
            if (command.ExecuteScalar() != null)
            {
                return true;
            }
        }

        return false;
    }

    private static DateTimeOffset? GetEntryUpdatedUtc(SqliteConnection connection, string normalizedPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT updated_utc FROM {UserFilesTable} WHERE path = $path;";
        command.Parameters.AddWithValue("$path", normalizedPath);
        var result = command.ExecuteScalar()?.ToString();
        return TryParseDateTimeOffset(result);
    }

    private static void UpsertEntry(SqliteConnection connection, string normalizedPath, byte[] content, bool isText, DateTimeOffset? updatedUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               INSERT INTO {UserFilesTable} (path, content, is_text, updated_utc)
                               VALUES ($path, $content, $isText, $updated)
                               ON CONFLICT(path) DO UPDATE SET
                                   content = excluded.content,
                                   is_text = excluded.is_text,
                                   updated_utc = excluded.updated_utc;
                               """;
        command.Parameters.AddWithValue("$path", normalizedPath);
        command.Parameters.Add("$content", SqliteType.Blob).Value = content;
        command.Parameters.AddWithValue("$isText", isText ? 1 : 0);
        command.Parameters.AddWithValue("$updated", (updatedUtc ?? DateTimeOffset.UtcNow).ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void UpsertMainConfig(SqliteConnection connection, string content, DateTimeOffset? updatedUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               INSERT INTO {AppConfigTable} ({AppConfigKeyColumn}, {AppConfigValueColumn}, {AppConfigUpdatedColumn})
                               VALUES ($key, $value, $updated)
                               ON CONFLICT({AppConfigKeyColumn}) DO UPDATE SET
                                   {AppConfigValueColumn} = excluded.{AppConfigValueColumn},
                                   {AppConfigUpdatedColumn} = excluded.{AppConfigUpdatedColumn};
                               """;
        command.Parameters.AddWithValue("$key", MainConfigKey);
        command.Parameters.AddWithValue("$value", content);
        command.Parameters.AddWithValue("$updated", (updatedUtc ?? DateTimeOffset.UtcNow).ToString("O"));
        command.ExecuteNonQuery();
    }

    private static bool TryReadRawMainConfigInternal(SqliteConnection connection, out string? content, out DateTimeOffset? updatedUtc)
    {
        content = null;
        updatedUtc = null;
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT {AppConfigValueColumn}, {AppConfigUpdatedColumn}
                               FROM {AppConfigTable}
                               WHERE {AppConfigKeyColumn} = $key;
                               """;
        command.Parameters.AddWithValue("$key", MainConfigKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        content = reader[AppConfigValueColumn]?.ToString();
        updatedUtc = TryParseDateTimeOffset(reader[AppConfigUpdatedColumn]?.ToString());
        return !string.IsNullOrWhiteSpace(content);
    }

    private static bool TryReadSplitConfigAsMainInternal(SqliteConnection connection, out string? content)
    {
        content = null;
        var merged = new JsonObject();
        var hasSection = false;
        foreach (var section in SplitSections)
        {
            if (!TryReadConfigSectionInternal(connection, section, out var sectionJson, out _) ||
                string.IsNullOrWhiteSpace(sectionJson))
            {
                continue;
            }

            JsonObject? sectionObject;
            try
            {
                sectionObject = JsonNode.Parse(sectionJson, documentOptions: JsonReadDocumentOptions) as JsonObject;
            }
            catch
            {
                continue;
            }

            if (sectionObject == null)
            {
                continue;
            }

            foreach (var kvp in sectionObject)
            {
                merged[kvp.Key] = kvp.Value?.DeepClone();
            }

            hasSection = true;
        }

        if (!hasSection)
        {
            return false;
        }

        content = merged.ToJsonString(JsonWriteIndentedOptions);
        return true;
    }

    private static bool TryReadConfigSectionInternal(
        SqliteConnection connection,
        ConfigSection section,
        out string? content,
        out DateTimeOffset? updatedUtc)
    {
        content = null;
        updatedUtc = null;
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT {ConfigSectionValueColumn}, {ConfigSectionUpdatedColumn}
                               FROM {GetConfigSectionTableName(section)}
                               WHERE {AppConfigKeyColumn} = $key;
                               """;
        command.Parameters.AddWithValue("$key", MainConfigKey);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        content = reader[ConfigSectionValueColumn]?.ToString();
        updatedUtc = TryParseDateTimeOffset(reader[ConfigSectionUpdatedColumn]?.ToString());
        return !string.IsNullOrWhiteSpace(content);
    }

    private static void UpsertConfigSection(
        SqliteConnection connection,
        ConfigSection section,
        string content,
        DateTimeOffset? updatedUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               INSERT INTO {GetConfigSectionTableName(section)} ({AppConfigKeyColumn}, {ConfigSectionValueColumn}, {ConfigSectionUpdatedColumn})
                               VALUES ($key, $value, $updated)
                               ON CONFLICT({AppConfigKeyColumn}) DO UPDATE SET
                                   {ConfigSectionValueColumn} = excluded.{ConfigSectionValueColumn},
                                   {ConfigSectionUpdatedColumn} = excluded.{ConfigSectionUpdatedColumn};
                               """;
        command.Parameters.AddWithValue("$key", MainConfigKey);
        command.Parameters.AddWithValue("$value", content);
        command.Parameters.AddWithValue("$updated", (updatedUtc ?? DateTimeOffset.UtcNow).ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void UpsertOneDragonConfig(SqliteConnection connection, string name, string content, DateTimeOffset? updatedUtc)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               INSERT INTO {ConfigOneDragonTable} ({OneDragonNameColumn}, {OneDragonValueColumn}, {OneDragonUpdatedColumn})
                               VALUES ($name, $value, $updated)
                               ON CONFLICT({OneDragonNameColumn}) DO UPDATE SET
                                   {OneDragonValueColumn} = excluded.{OneDragonValueColumn},
                                   {OneDragonUpdatedColumn} = excluded.{OneDragonUpdatedColumn};
                               """;
        command.Parameters.AddWithValue("$name", name.Trim());
        command.Parameters.AddWithValue("$value", content);
        command.Parameters.AddWithValue("$updated", (updatedUtc ?? DateTimeOffset.UtcNow).ToString("O"));
        command.ExecuteNonQuery();
    }

    private static DateTimeOffset? GetSplitConfigLatestUpdatedUtcInternal(SqliteConnection connection)
    {
        DateTimeOffset? latest = null;
        foreach (var section in SplitSections)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                                   SELECT {ConfigSectionUpdatedColumn}
                                   FROM {GetConfigSectionTableName(section)}
                                   WHERE {AppConfigKeyColumn} = $key;
                                   """;
            command.Parameters.AddWithValue("$key", MainConfigKey);
            var updated = TryParseDateTimeOffset(command.ExecuteScalar()?.ToString());
            if (updated == null)
            {
                continue;
            }

            if (latest == null || updated > latest)
            {
                latest = updated;
            }
        }

        return latest;
    }

    private static bool UpsertSplitSectionsFromMainJson(SqliteConnection connection, string mainJson, DateTimeOffset? updatedUtc)
    {
        if (!TrySplitMainJson(mainJson, out var sectionPayloads))
        {
            return false;
        }

        foreach (var section in SplitSections)
        {
            if (!sectionPayloads.TryGetValue(section, out var payload))
            {
                continue;
            }

            UpsertConfigSection(connection, section, payload, updatedUtc);
        }

        return true;
    }

    private static bool TryMergeJsonObjects(string baseJson, string overlayJson, out string? merged)
    {
        merged = null;
        JsonObject? baseObject;
        JsonObject? overlayObject;
        try
        {
            baseObject = JsonNode.Parse(baseJson, documentOptions: JsonReadDocumentOptions) as JsonObject;
            overlayObject = JsonNode.Parse(overlayJson, documentOptions: JsonReadDocumentOptions) as JsonObject;
        }
        catch
        {
            return false;
        }

        if (baseObject == null || overlayObject == null)
        {
            return false;
        }

        foreach (var kvp in overlayObject)
        {
            baseObject[kvp.Key] = kvp.Value?.DeepClone();
        }

        merged = baseObject.ToJsonString(JsonWriteIndentedOptions);
        return true;
    }

    private static bool TrySplitMainJson(string json, out Dictionary<ConfigSection, string> sectionPayloads)
    {
        sectionPayloads = new Dictionary<ConfigSection, string>();
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: JsonReadDocumentOptions) as JsonObject;
        }
        catch
        {
            return false;
        }

        if (root == null)
        {
            return false;
        }

        var sectionObjects = new Dictionary<ConfigSection, JsonObject>();
        foreach (var section in SplitSections)
        {
            sectionObjects[section] = new JsonObject();
        }

        foreach (var kvp in root)
        {
            var section = ResolveSection(kvp.Key);
            sectionObjects[section][kvp.Key] = kvp.Value?.DeepClone();
        }

        foreach (var section in SplitSections)
        {
            sectionPayloads[section] = sectionObjects[section].ToJsonString(JsonWriteIndentedOptions);
        }

        return true;
    }

    private static string? ResolveOneDragonConfigName(string path, string json)
    {
        try
        {
            if (JsonNode.Parse(json, documentOptions: JsonReadDocumentOptions) is JsonObject node)
            {
                var name = node["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name.Trim();
                }
            }
        }
        catch
        {
        }

        var fallbackName = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(fallbackName) ? null : fallbackName.Trim();
    }

    private static ConfigSection ResolveSection(string propertyName)
    {
        if (AutomationKeys.Contains(propertyName))
        {
            return ConfigSection.Automation;
        }

        if (HotkeysKeys.Contains(propertyName))
        {
            return ConfigSection.Hotkeys;
        }

        if (NotificationKeys.Contains(propertyName))
        {
            return ConfigSection.Notification;
        }

        if (AiKeys.Contains(propertyName))
        {
            return ConfigSection.Ai;
        }

        if (HardwareKeys.Contains(propertyName))
        {
            return ConfigSection.Hardware;
        }

        return ConfigSection.General;
    }

    private static string GetConfigSectionTableName(ConfigSection section)
    {
        return section switch
        {
            ConfigSection.General => ConfigGeneralTable,
            ConfigSection.Automation => ConfigAutomationTable,
            ConfigSection.Hotkeys => ConfigHotkeysTable,
            ConfigSection.Notification => ConfigNotificationTable,
            ConfigSection.Ai => ConfigAiTable,
            ConfigSection.Hardware => ConfigHardwareTable,
            _ => ConfigGeneralTable
        };
    }

    private static bool TryReadUserFileText(SqliteConnection connection, string normalizedPath, out string? content, out DateTimeOffset? updatedUtc)
    {
        content = null;
        updatedUtc = null;
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT content, is_text, updated_utc FROM {UserFilesTable} WHERE path = $path;";
        command.Parameters.AddWithValue("$path", normalizedPath);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        var isText = reader.GetInt32(1) == 1;
        if (!isText)
        {
            return false;
        }

        var bytes = (byte[])reader["content"];
        content = Encoding.UTF8.GetString(bytes);
        updatedUtc = TryParseDateTimeOffset(reader["updated_utc"]?.ToString());
        return true;
    }

    private static void DeleteUserFileEntry(SqliteConnection connection, string normalizedPath)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {UserFilesTable} WHERE path = $path;";
        command.Parameters.AddWithValue("$path", normalizedPath);
        command.ExecuteNonQuery();
    }

    private static string? GetMeta(SqliteConnection connection, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT meta_value FROM {MetaTable} WHERE meta_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar()?.ToString();
    }

    private static void SetMeta(SqliteConnection connection, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               INSERT INTO {MetaTable} (meta_key, meta_value)
                               VALUES ($key, $value)
                               ON CONFLICT(meta_key) DO UPDATE SET meta_value = excluded.meta_value;
                               """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void DeleteIgnoredEntries(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        var tempPrefix = $"Temp{Path.DirectorySeparatorChar}%";
        var altPrefix = $"Temp{Path.AltDirectorySeparatorChar}%";
        command.CommandText = $"""
                               DELETE FROM {UserFilesTable}
                               WHERE path = $temp
                                  OR path LIKE $prefix
                                  OR path LIKE $altPrefix
                                  OR path = $db;
                               """;
        command.Parameters.AddWithValue("$temp", "Temp");
        command.Parameters.AddWithValue("$prefix", tempPrefix);
        command.Parameters.AddWithValue("$altPrefix", altPrefix);
        command.Parameters.AddWithValue("$db", DatabaseFileName);
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name = $table;";
        command.Parameters.AddWithValue("$table", tableName);
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
            DataSource = DatabasePath,
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
                             """;
        pragma.ExecuteNonQuery();

        return connection;
    }

    private static DateTimeOffset? TryParseDateTimeOffset(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result))
        {
            return result;
        }

        return null;
    }
}

internal enum ConfigSection
{
    General,
    Automation,
    Hotkeys,
    Notification,
    Ai,
    Hardware
}

internal readonly record struct NamedConfigEntry(string Name, string Content, DateTimeOffset? UpdatedUtc);
internal readonly record struct UserFileEntry(string Path, long Size, bool IsText, DateTimeOffset? UpdatedUtc);
