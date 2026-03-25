using BetterGenshinImpact.GameTask.LogParse;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace BetterGenshinImpact.Persistence.Runtime;

/// <summary>
/// 执行记录仓储。
/// 记录用于“任务完成跳过”判定，因此直接写数据库，不再以每日 JSON 文件作为真源。
/// </summary>
internal static class ExecutionRecordRuntimeRepository
{
    private const string LegacyMigrationMetaKey = "execution_record_legacy_imported";

    internal static void Save(ExecutionRecord record)
    {
        EnsureReady();

        using var connection = RuntimePersistenceDatabase.OpenConnection();
        Upsert(connection, record, DateTimeOffset.UtcNow);
    }

    internal static List<DailyExecutionRecord> GetRecent(int days)
    {
        if (days <= 0)
        {
            throw new ArgumentException("Days must be a positive integer", nameof(days));
        }

        EnsureReady();

        var endDate = DateTime.Today;
        var startDate = endDate.AddDays(-days + 1);
        var startKey = startDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var endKey = endDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        using var connection = RuntimePersistenceDatabase.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT id, date_key, group_name, project_name, folder_name, type,
                                      server_start_time, start_time_local, server_end_time, end_time_local,
                                      is_successful
                               FROM {RuntimePersistenceDatabase.ExecutionRecordTableName}
                               WHERE date_key >= $startKey AND date_key <= $endKey
                               ORDER BY date_key DESC, start_time_local DESC;
                               """;
        command.Parameters.AddWithValue("$startKey", startKey);
        command.Parameters.AddWithValue("$endKey", endKey);

        var result = new List<DailyExecutionRecord>();
        using var reader = command.ExecuteReader();
        DailyExecutionRecord? current = null;
        while (reader.Read())
        {
            var dateKey = reader["date_key"]?.ToString() ?? string.Empty;
            if (current == null || !string.Equals(current.Name, dateKey, StringComparison.Ordinal))
            {
                current = new DailyExecutionRecord
                {
                    Name = dateKey
                };
                result.Add(current);
            }

            current.ExecutionRecords.Add(new ExecutionRecord
            {
                Id = Guid.TryParse(reader["id"]?.ToString(), out var id) ? id : Guid.Empty,
                GroupName = reader["group_name"]?.ToString() ?? string.Empty,
                ProjectName = reader["project_name"]?.ToString() ?? string.Empty,
                FolderName = reader["folder_name"]?.ToString() ?? string.Empty,
                Type = reader["type"]?.ToString() ?? string.Empty,
                ServerStartTime = RuntimePersistenceDatabase.TryParseDateTimeOffset(reader["server_start_time"]?.ToString()),
                StartTime = RuntimePersistenceDatabase.TryParseDateTime(reader["start_time_local"]?.ToString()) ?? DateTime.MinValue,
                ServerEndTime = RuntimePersistenceDatabase.TryParseDateTimeOffset(reader["server_end_time"]?.ToString()),
                EndTime = RuntimePersistenceDatabase.TryParseDateTime(reader["end_time_local"]?.ToString()) ?? DateTime.MinValue,
                IsSuccessful = Convert.ToInt32(reader["is_successful"], CultureInfo.InvariantCulture) == 1
            });
        }

        return result;
    }

    private static void EnsureReady()
    {
        RuntimePersistenceDatabase.Initialize();

        using var connection = RuntimePersistenceDatabase.OpenConnection();
        if (RuntimePersistenceDatabase.GetMeta(connection, LegacyMigrationMetaKey) == "1")
        {
            return;
        }

        ImportLegacyFiles(connection);
        RuntimePersistenceDatabase.SetMeta(connection, LegacyMigrationMetaKey, "1");
    }

    private static void ImportLegacyFiles(SqliteConnection connection)
    {
        var directory = RuntimePersistenceDatabase.LegacyExecutionRecordDirectory;
        if (!Directory.Exists(directory))
        {
            return;
        }

        var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        using var transaction = connection.BeginTransaction();
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var daily = JsonConvert.DeserializeObject<DailyExecutionRecord>(json);
                if (daily?.ExecutionRecords == null)
                {
                    continue;
                }

                var updatedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                foreach (var record in daily.ExecutionRecords.Where(r => r != null))
                {
                    Upsert(connection, record, updatedUtc, transaction);
                }
            }
            catch
            {
                // 兼容迁移阶段允许跳过损坏文件，避免阻塞新存储上线。
            }
        }

        transaction.Commit();
    }

    private static void Upsert(
        SqliteConnection connection,
        ExecutionRecord record,
        DateTimeOffset updatedUtc,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
                               INSERT INTO {RuntimePersistenceDatabase.ExecutionRecordTableName} (
                                   id, date_key, group_name, project_name, folder_name, type,
                                   server_start_time, start_time_local, server_end_time, end_time_local,
                                   is_successful, updated_utc
                               )
                               VALUES (
                                   $id, $dateKey, $groupName, $projectName, $folderName, $type,
                                   $serverStartTime, $startTimeLocal, $serverEndTime, $endTimeLocal,
                                   $isSuccessful, $updatedUtc
                               )
                               ON CONFLICT(id) DO UPDATE SET
                                   date_key = excluded.date_key,
                                   group_name = excluded.group_name,
                                   project_name = excluded.project_name,
                                   folder_name = excluded.folder_name,
                                   type = excluded.type,
                                   server_start_time = excluded.server_start_time,
                                   start_time_local = excluded.start_time_local,
                                   server_end_time = excluded.server_end_time,
                                   end_time_local = excluded.end_time_local,
                                   is_successful = excluded.is_successful,
                                   updated_utc = excluded.updated_utc;
                               """;
        command.Parameters.AddWithValue("$id", record.Id.ToString());
        command.Parameters.AddWithValue("$dateKey", record.StartTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$groupName", record.GroupName ?? string.Empty);
        command.Parameters.AddWithValue("$projectName", record.ProjectName ?? string.Empty);
        command.Parameters.AddWithValue("$folderName", record.FolderName ?? string.Empty);
        command.Parameters.AddWithValue("$type", record.Type ?? string.Empty);
        command.Parameters.AddWithValue("$serverStartTime", record.ServerStartTime?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$startTimeLocal", RuntimePersistenceDatabase.ToRoundtrip(record.StartTime));
        command.Parameters.AddWithValue("$serverEndTime", record.ServerEndTime?.ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$endTimeLocal", RuntimePersistenceDatabase.ToRoundtrip(record.EndTime));
        command.Parameters.AddWithValue("$isSuccessful", record.IsSuccessful ? 1 : 0);
        command.Parameters.AddWithValue("$updatedUtc", RuntimePersistenceDatabase.ToRoundtrip(updatedUtc));
        command.ExecuteNonQuery();
    }
}
