using BetterGenshinImpact.GameTask.FarmingPlan;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace BetterGenshinImpact.Persistence.Runtime;

/// <summary>
/// 锄地统计仓储。
/// DailyFarmingData 的汇总字段和 records 列表拆成日汇总表 + 明细记录表。
/// </summary>
internal static class FarmingStatsRuntimeRepository
{
    private const string LegacyMigrationMetaKey = "farming_stats_legacy_imported";

    internal static DailyFarmingData Load(string statDate)
    {
        EnsureReady();

        using var connection = RuntimePersistenceDatabase.OpenConnection();
        return LoadInternal(connection, statDate);
    }

    internal static void AppendSession(string statDate, FarmingSession session, FarmingRouteInfo route, DateTime timestamp)
    {
        EnsureReady();

        using var connection = RuntimePersistenceDatabase.OpenConnection();
        using var transaction = connection.BeginTransaction();

        EnsureDailyRowExists(connection, statDate, transaction);
        var inserted = InsertRecord(connection, statDate, session, route, timestamp, transaction);
        if (inserted && session.AllowFarmingCount)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                                   UPDATE {RuntimePersistenceDatabase.FarmingDailyTableName}
                                   SET total_normal_mob_count = total_normal_mob_count + $normalMobCount,
                                       total_elite_mob_count = total_elite_mob_count + $eliteMobCount,
                                       updated_utc = $updatedUtc
                                   WHERE stat_date = $statDate;
                                   """;
            command.Parameters.AddWithValue("$normalMobCount", session.NormalMobCount);
            command.Parameters.AddWithValue("$eliteMobCount", session.EliteMobCount);
            command.Parameters.AddWithValue("$updatedUtc", RuntimePersistenceDatabase.ToRoundtrip(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$statDate", statDate);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    internal static void SaveSummary(string statDate, DailyFarmingData data)
    {
        EnsureReady();

        using var connection = RuntimePersistenceDatabase.OpenConnection();
        UpsertDaily(connection, statDate, data, DateTimeOffset.UtcNow);
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
        var directory = RuntimePersistenceDatabase.LegacyFarmingDirectory;
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
                var data = JsonConvert.DeserializeObject<DailyFarmingData>(json, GetLegacyJsonSettings());
                if (data == null)
                {
                    continue;
                }

                var statDate = Path.GetFileNameWithoutExtension(file);
                var updatedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                UpsertDaily(connection, statDate, data, updatedUtc, transaction);
                ReplaceRecords(connection, statDate, data.Records, transaction);
            }
            catch
            {
                // 兼容迁移时跳过损坏文件，避免影响新存储可用性。
            }
        }

        transaction.Commit();
    }

    private static DailyFarmingData LoadInternal(SqliteConnection connection, string statDate)
    {
        var data = new DailyFarmingData();

        using (var summary = connection.CreateCommand())
        {
            summary.CommandText = $"""
                                   SELECT total_normal_mob_count, total_elite_mob_count,
                                          miyoushe_total_normal_mob_count, miyoushe_total_elite_mob_count,
                                          last_miyoushe_update_time, travels_diary_detail_manager_update_time
                                   FROM {RuntimePersistenceDatabase.FarmingDailyTableName}
                                   WHERE stat_date = $statDate;
                                   """;
            summary.Parameters.AddWithValue("$statDate", statDate);
            using var reader = summary.ExecuteReader();
            if (reader.Read())
            {
                data.TotalNormalMobCount = Convert.ToDouble(reader["total_normal_mob_count"], CultureInfo.InvariantCulture);
                data.TotalEliteMobCount = Convert.ToDouble(reader["total_elite_mob_count"], CultureInfo.InvariantCulture);
                data.MiyousheTotalNormalMobCount = Convert.ToDouble(reader["miyoushe_total_normal_mob_count"], CultureInfo.InvariantCulture);
                data.MiyousheTotalEliteMobCount = Convert.ToDouble(reader["miyoushe_total_elite_mob_count"], CultureInfo.InvariantCulture);
                data.LastMiyousheUpdateTime = RuntimePersistenceDatabase.TryParseDateTime(reader["last_miyoushe_update_time"]?.ToString()) ?? DateTime.MinValue;
                data.TravelsDiaryDetailManagerUpdateTime = RuntimePersistenceDatabase.TryParseDateTime(reader["travels_diary_detail_manager_update_time"]?.ToString()) ?? DateTime.MinValue;
            }
        }

        using (var records = connection.CreateCommand())
        {
            records.CommandText = $"""
                                   SELECT group_name, project_name, folder_name,
                                          normal_mob_count, elite_mob_count, timestamp
                                   FROM {RuntimePersistenceDatabase.FarmingRecordTableName}
                                   WHERE stat_date = $statDate
                                   ORDER BY timestamp;
                                   """;
            records.Parameters.AddWithValue("$statDate", statDate);
            using var reader = records.ExecuteReader();
            while (reader.Read())
            {
                data.Records.Add(new FarmingRecord
                {
                    GroupName = reader["group_name"]?.ToString() ?? string.Empty,
                    ProjectName = reader["project_name"]?.ToString() ?? string.Empty,
                    FolderName = reader["folder_name"]?.ToString() ?? string.Empty,
                    NormalMobCount = Convert.ToDouble(reader["normal_mob_count"], CultureInfo.InvariantCulture),
                    EliteMobCount = Convert.ToDouble(reader["elite_mob_count"], CultureInfo.InvariantCulture),
                    Timestamp = RuntimePersistenceDatabase.TryParseDateTime(reader["timestamp"]?.ToString()) ?? DateTime.MinValue
                });
            }
        }

        return data;
    }

    private static void EnsureDailyRowExists(SqliteConnection connection, string statDate, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
                               INSERT INTO {RuntimePersistenceDatabase.FarmingDailyTableName} (
                                   stat_date, total_normal_mob_count, total_elite_mob_count,
                                   miyoushe_total_normal_mob_count, miyoushe_total_elite_mob_count,
                                   last_miyoushe_update_time, travels_diary_detail_manager_update_time, updated_utc
                               )
                               VALUES ($statDate, 0, 0, 0, 0, NULL, NULL, $updatedUtc)
                               ON CONFLICT(stat_date) DO NOTHING;
                               """;
        command.Parameters.AddWithValue("$statDate", statDate);
        command.Parameters.AddWithValue("$updatedUtc", RuntimePersistenceDatabase.ToRoundtrip(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private static void UpsertDaily(
        SqliteConnection connection,
        string statDate,
        DailyFarmingData data,
        DateTimeOffset updatedUtc,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
                               INSERT INTO {RuntimePersistenceDatabase.FarmingDailyTableName} (
                                   stat_date, total_normal_mob_count, total_elite_mob_count,
                                   miyoushe_total_normal_mob_count, miyoushe_total_elite_mob_count,
                                   last_miyoushe_update_time, travels_diary_detail_manager_update_time, updated_utc
                               )
                               VALUES (
                                   $statDate, $totalNormal, $totalElite,
                                   $miyousheNormal, $miyousheElite,
                                   $lastMiyousheUpdateTime, $travelsDiaryDetailManagerUpdateTime, $updatedUtc
                               )
                               ON CONFLICT(stat_date) DO UPDATE SET
                                   total_normal_mob_count = excluded.total_normal_mob_count,
                                   total_elite_mob_count = excluded.total_elite_mob_count,
                                   miyoushe_total_normal_mob_count = excluded.miyoushe_total_normal_mob_count,
                                   miyoushe_total_elite_mob_count = excluded.miyoushe_total_elite_mob_count,
                                   last_miyoushe_update_time = excluded.last_miyoushe_update_time,
                                   travels_diary_detail_manager_update_time = excluded.travels_diary_detail_manager_update_time,
                                   updated_utc = excluded.updated_utc;
                               """;
        command.Parameters.AddWithValue("$statDate", statDate);
        command.Parameters.AddWithValue("$totalNormal", data.TotalNormalMobCount);
        command.Parameters.AddWithValue("$totalElite", data.TotalEliteMobCount);
        command.Parameters.AddWithValue("$miyousheNormal", data.MiyousheTotalNormalMobCount);
        command.Parameters.AddWithValue("$miyousheElite", data.MiyousheTotalEliteMobCount);
        command.Parameters.AddWithValue("$lastMiyousheUpdateTime", data.LastMiyousheUpdateTime == DateTime.MinValue
            ? (object)DBNull.Value
            : RuntimePersistenceDatabase.ToRoundtrip(data.LastMiyousheUpdateTime));
        command.Parameters.AddWithValue("$travelsDiaryDetailManagerUpdateTime", data.TravelsDiaryDetailManagerUpdateTime == DateTime.MinValue
            ? (object)DBNull.Value
            : RuntimePersistenceDatabase.ToRoundtrip(data.TravelsDiaryDetailManagerUpdateTime));
        command.Parameters.AddWithValue("$updatedUtc", RuntimePersistenceDatabase.ToRoundtrip(updatedUtc));
        command.ExecuteNonQuery();
    }

    private static bool InsertRecord(
        SqliteConnection connection,
        string statDate,
        FarmingSession session,
        FarmingRouteInfo route,
        DateTime timestamp,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
                               INSERT INTO {RuntimePersistenceDatabase.FarmingRecordTableName} (
                                   stat_date, group_name, project_name, folder_name,
                                   normal_mob_count, elite_mob_count, timestamp
                               )
                               VALUES (
                                   $statDate, $groupName, $projectName, $folderName,
                                   $normalMobCount, $eliteMobCount, $timestamp
                               )
                               ON CONFLICT(stat_date, group_name, project_name, folder_name, timestamp) DO NOTHING;
                               """;
        command.Parameters.AddWithValue("$statDate", statDate);
        command.Parameters.AddWithValue("$groupName", route.GroupName ?? string.Empty);
        command.Parameters.AddWithValue("$projectName", route.ProjectName ?? string.Empty);
        command.Parameters.AddWithValue("$folderName", route.FolderName ?? string.Empty);
        command.Parameters.AddWithValue("$normalMobCount", session.AllowFarmingCount ? session.NormalMobCount : 0);
        command.Parameters.AddWithValue("$eliteMobCount", session.AllowFarmingCount ? session.EliteMobCount : 0);
        command.Parameters.AddWithValue("$timestamp", RuntimePersistenceDatabase.ToRoundtrip(timestamp));
        return command.ExecuteNonQuery() > 0;
    }

    private static void ReplaceRecords(
        SqliteConnection connection,
        string statDate,
        IEnumerable<FarmingRecord> records,
        SqliteTransaction transaction)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"""
                                  DELETE FROM {RuntimePersistenceDatabase.FarmingRecordTableName}
                                  WHERE stat_date = $statDate;
                                  """;
            delete.Parameters.AddWithValue("$statDate", statDate);
            delete.ExecuteNonQuery();
        }

        foreach (var record in records)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                                  INSERT INTO {RuntimePersistenceDatabase.FarmingRecordTableName} (
                                      stat_date, group_name, project_name, folder_name,
                                      normal_mob_count, elite_mob_count, timestamp
                                  )
                                  VALUES (
                                      $statDate, $groupName, $projectName, $folderName,
                                      $normalMobCount, $eliteMobCount, $timestamp
                                  )
                                  ON CONFLICT(stat_date, group_name, project_name, folder_name, timestamp) DO NOTHING;
                                  """;
            insert.Parameters.AddWithValue("$statDate", statDate);
            insert.Parameters.AddWithValue("$groupName", record.GroupName ?? string.Empty);
            insert.Parameters.AddWithValue("$projectName", record.ProjectName ?? string.Empty);
            insert.Parameters.AddWithValue("$folderName", record.FolderName ?? string.Empty);
            insert.Parameters.AddWithValue("$normalMobCount", record.NormalMobCount);
            insert.Parameters.AddWithValue("$eliteMobCount", record.EliteMobCount);
            insert.Parameters.AddWithValue("$timestamp", RuntimePersistenceDatabase.ToRoundtrip(record.Timestamp));
            insert.ExecuteNonQuery();
        }
    }

    private static JsonSerializerSettings GetLegacyJsonSettings()
    {
        return new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy()
            },
            Formatting = Formatting.Indented,
            DateFormatHandling = DateFormatHandling.IsoDateFormat
        };
    }
}
