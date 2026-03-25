using BetterGenshinImpact.GameTask.TaskProgress;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace BetterGenshinImpact.Persistence.Runtime;

/// <summary>
/// 调度器继续执行进度仓储。
/// 使用 session + groups + history 的拆表结构，避免再以单个 JSON 文件承载整条进度链。
/// </summary>
internal static class TaskProgressRuntimeRepository
{
    private const string LegacyMigrationMetaKey = "task_progress_legacy_imported";
    private static readonly Regex LegacyFileNamePattern = new(@"^\d{14}\.json$", RegexOptions.Compiled);

    internal static void Save(TaskProgress taskProgress)
    {
        EnsureReady();

        using var connection = RuntimePersistenceDatabase.OpenConnection();
        using var transaction = connection.BeginTransaction();
        SaveInternal(connection, taskProgress, DateTimeOffset.UtcNow, transaction);
        transaction.Commit();
    }

    internal static List<TaskProgress> LoadAllActive()
    {
        EnsureReady();

        using var connection = RuntimePersistenceDatabase.OpenConnection();
        PruneStaleSessions(connection);

        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT *
                               FROM {RuntimePersistenceDatabase.TaskProgressSessionTableName}
                               WHERE end_time IS NULL
                               ORDER BY updated_utc DESC;
                               """;

        var result = new List<TaskProgress>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadSession(connection, reader));
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
        var directory = RuntimePersistenceDatabase.LegacyTaskProgressDirectory;
        if (!Directory.Exists(directory))
        {
            return;
        }

        var files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
            .Where(file => LegacyFileNamePattern.IsMatch(Path.GetFileName(file)))
            .ToArray();

        using var transaction = connection.BeginTransaction();
        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var progress = JsonConvert.DeserializeObject<TaskProgress>(json);
                if (progress == null)
                {
                    continue;
                }

                var updatedUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                SaveInternal(connection, progress, updatedUtc, transaction);
            }
            catch
            {
                // 兼容迁移时跳过损坏文件，避免阻断后续可用性。
            }
        }

        transaction.Commit();
    }

    private static void SaveInternal(
        SqliteConnection connection,
        TaskProgress taskProgress,
        DateTimeOffset updatedUtc,
        SqliteTransaction transaction)
    {
        UpsertSession(connection, taskProgress, updatedUtc, transaction);
        ReplaceGroups(connection, taskProgress, transaction);
        ReplaceHistory(connection, taskProgress, transaction);
    }

    private static void UpsertSession(
        SqliteConnection connection,
        TaskProgress taskProgress,
        DateTimeOffset updatedUtc,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
                               INSERT INTO {RuntimePersistenceDatabase.TaskProgressSessionTableName} (
                                   session_id, start_time, end_time, loop_enabled, loop_count,
                                   last_script_group_name, current_script_group_name,
                                   current_group_name, current_project_index, current_project_name,
                                   current_project_folder_name, current_project_start_time, current_project_end_time,
                                   current_project_status, current_project_task_end,
                                   last_success_group_name, last_success_project_index, last_success_project_name,
                                   last_success_project_folder_name, last_success_project_start_time, last_success_project_end_time,
                                   last_success_project_status, last_success_project_task_end,
                                   updated_utc
                               )
                               VALUES (
                                   $sessionId, $startTime, $endTime, $loopEnabled, $loopCount,
                                   $lastScriptGroupName, $currentScriptGroupName,
                                   $currentGroupName, $currentProjectIndex, $currentProjectName,
                                   $currentProjectFolderName, $currentProjectStartTime, $currentProjectEndTime,
                                   $currentProjectStatus, $currentProjectTaskEnd,
                                   $lastSuccessGroupName, $lastSuccessProjectIndex, $lastSuccessProjectName,
                                   $lastSuccessProjectFolderName, $lastSuccessProjectStartTime, $lastSuccessProjectEndTime,
                                   $lastSuccessProjectStatus, $lastSuccessProjectTaskEnd,
                                   $updatedUtc
                               )
                               ON CONFLICT(session_id) DO UPDATE SET
                                   start_time = excluded.start_time,
                                   end_time = excluded.end_time,
                                   loop_enabled = excluded.loop_enabled,
                                   loop_count = excluded.loop_count,
                                   last_script_group_name = excluded.last_script_group_name,
                                   current_script_group_name = excluded.current_script_group_name,
                                   current_group_name = excluded.current_group_name,
                                   current_project_index = excluded.current_project_index,
                                   current_project_name = excluded.current_project_name,
                                   current_project_folder_name = excluded.current_project_folder_name,
                                   current_project_start_time = excluded.current_project_start_time,
                                   current_project_end_time = excluded.current_project_end_time,
                                   current_project_status = excluded.current_project_status,
                                   current_project_task_end = excluded.current_project_task_end,
                                   last_success_group_name = excluded.last_success_group_name,
                                   last_success_project_index = excluded.last_success_project_index,
                                   last_success_project_name = excluded.last_success_project_name,
                                   last_success_project_folder_name = excluded.last_success_project_folder_name,
                                   last_success_project_start_time = excluded.last_success_project_start_time,
                                   last_success_project_end_time = excluded.last_success_project_end_time,
                                   last_success_project_status = excluded.last_success_project_status,
                                   last_success_project_task_end = excluded.last_success_project_task_end,
                                   updated_utc = excluded.updated_utc;
                               """;
        command.Parameters.AddWithValue("$sessionId", taskProgress.Name);
        command.Parameters.AddWithValue("$startTime", RuntimePersistenceDatabase.ToRoundtrip(taskProgress.StartTime));
        command.Parameters.AddWithValue("$endTime", taskProgress.EndTime.HasValue
            ? RuntimePersistenceDatabase.ToRoundtrip(taskProgress.EndTime.Value)
            : (object)DBNull.Value);
        command.Parameters.AddWithValue("$loopEnabled", taskProgress.Loop ? 1 : 0);
        command.Parameters.AddWithValue("$loopCount", taskProgress.LoopCount);
        command.Parameters.AddWithValue("$lastScriptGroupName", taskProgress.LastScriptGroupName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$currentScriptGroupName", taskProgress.CurrentScriptGroupName ?? (object)DBNull.Value);

        BindProjectInfo(
            command,
            taskProgress.CurrentScriptGroupProjectInfo,
            "$currentGroupName",
            "$currentProjectIndex",
            "$currentProjectName",
            "$currentProjectFolderName",
            "$currentProjectStartTime",
            "$currentProjectEndTime",
            "$currentProjectStatus",
            "$currentProjectTaskEnd");

        BindProjectInfo(
            command,
            taskProgress.LastSuccessScriptGroupProjectInfo,
            "$lastSuccessGroupName",
            "$lastSuccessProjectIndex",
            "$lastSuccessProjectName",
            "$lastSuccessProjectFolderName",
            "$lastSuccessProjectStartTime",
            "$lastSuccessProjectEndTime",
            "$lastSuccessProjectStatus",
            "$lastSuccessProjectTaskEnd");

        command.Parameters.AddWithValue("$updatedUtc", RuntimePersistenceDatabase.ToRoundtrip(updatedUtc));
        command.ExecuteNonQuery();
    }

    private static void ReplaceGroups(SqliteConnection connection, TaskProgress taskProgress, SqliteTransaction transaction)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"""
                                  DELETE FROM {RuntimePersistenceDatabase.TaskProgressGroupTableName}
                                  WHERE session_id = $sessionId;
                                  """;
            delete.Parameters.AddWithValue("$sessionId", taskProgress.Name);
            delete.ExecuteNonQuery();
        }

        for (var i = 0; i < taskProgress.ScriptGroupNames.Count; i++)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                                  INSERT INTO {RuntimePersistenceDatabase.TaskProgressGroupTableName} (
                                      session_id, position, group_name
                                  )
                                  VALUES ($sessionId, $position, $groupName);
                                  """;
            insert.Parameters.AddWithValue("$sessionId", taskProgress.Name);
            insert.Parameters.AddWithValue("$position", i);
            insert.Parameters.AddWithValue("$groupName", taskProgress.ScriptGroupNames[i]);
            insert.ExecuteNonQuery();
        }
    }

    private static void ReplaceHistory(SqliteConnection connection, TaskProgress taskProgress, SqliteTransaction transaction)
    {
        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = $"""
                                  DELETE FROM {RuntimePersistenceDatabase.TaskProgressHistoryTableName}
                                  WHERE session_id = $sessionId;
                                  """;
            delete.Parameters.AddWithValue("$sessionId", taskProgress.Name);
            delete.ExecuteNonQuery();
        }

        if (taskProgress.History == null)
        {
            return;
        }

        for (var i = 0; i < taskProgress.History.Count; i++)
        {
            var item = taskProgress.History[i];
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = $"""
                                  INSERT INTO {RuntimePersistenceDatabase.TaskProgressHistoryTableName} (
                                      session_id, position, group_name, task_end,
                                      project_index, project_name, folder_name,
                                      start_time, end_time, status
                                  )
                                  VALUES (
                                      $sessionId, $position, $groupName, $taskEnd,
                                      $projectIndex, $projectName, $folderName,
                                      $startTime, $endTime, $status
                                  );
                                  """;
            insert.Parameters.AddWithValue("$sessionId", taskProgress.Name);
            insert.Parameters.AddWithValue("$position", i);
            insert.Parameters.AddWithValue("$groupName", item.GroupName ?? string.Empty);
            insert.Parameters.AddWithValue("$taskEnd", item.TaskEnd ? 1 : 0);
            insert.Parameters.AddWithValue("$projectIndex", item.Index);
            insert.Parameters.AddWithValue("$projectName", item.Name ?? string.Empty);
            insert.Parameters.AddWithValue("$folderName", item.FolderName ?? string.Empty);
            insert.Parameters.AddWithValue("$startTime", RuntimePersistenceDatabase.ToRoundtrip(item.StartTime));
            insert.Parameters.AddWithValue("$endTime", item.EndTime.HasValue
                ? RuntimePersistenceDatabase.ToRoundtrip(item.EndTime.Value)
                : (object)DBNull.Value);
            insert.Parameters.AddWithValue("$status", item.Status);
            insert.ExecuteNonQuery();
        }
    }

    private static TaskProgress ReadSession(SqliteConnection connection, SqliteDataReader reader)
    {
        var sessionId = reader["session_id"]?.ToString() ?? string.Empty;
        var taskProgress = new TaskProgress
        {
            Name = sessionId,
            StartTime = RuntimePersistenceDatabase.TryParseDateTime(reader["start_time"]?.ToString()) ?? DateTime.MinValue,
            EndTime = RuntimePersistenceDatabase.TryParseDateTime(reader["end_time"]?.ToString()),
            Loop = Convert.ToInt32(reader["loop_enabled"], CultureInfo.InvariantCulture) == 1,
            LoopCount = Convert.ToInt32(reader["loop_count"], CultureInfo.InvariantCulture),
            LastScriptGroupName = reader["last_script_group_name"]?.ToString(),
            CurrentScriptGroupName = reader["current_script_group_name"]?.ToString(),
            ScriptGroupNames = LoadGroups(connection, sessionId),
            History = LoadHistory(connection, sessionId)
        };

        taskProgress.CurrentScriptGroupProjectInfo = ReadProjectInfo(
            reader,
            "current_group_name",
            "current_project_index",
            "current_project_name",
            "current_project_folder_name",
            "current_project_start_time",
            "current_project_end_time",
            "current_project_status",
            "current_project_task_end");

        taskProgress.LastSuccessScriptGroupProjectInfo = ReadProjectInfo(
            reader,
            "last_success_group_name",
            "last_success_project_index",
            "last_success_project_name",
            "last_success_project_folder_name",
            "last_success_project_start_time",
            "last_success_project_end_time",
            "last_success_project_status",
            "last_success_project_task_end");

        return taskProgress;
    }

    private static List<string> LoadGroups(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT group_name
                               FROM {RuntimePersistenceDatabase.TaskProgressGroupTableName}
                               WHERE session_id = $sessionId
                               ORDER BY position;
                               """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        var result = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var value = reader["group_name"]?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    private static List<TaskProgress.ScriptGroupProjectInfo> LoadHistory(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               SELECT group_name, task_end, project_index, project_name, folder_name,
                                      start_time, end_time, status
                               FROM {RuntimePersistenceDatabase.TaskProgressHistoryTableName}
                               WHERE session_id = $sessionId
                               ORDER BY position;
                               """;
        command.Parameters.AddWithValue("$sessionId", sessionId);

        var result = new List<TaskProgress.ScriptGroupProjectInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new TaskProgress.ScriptGroupProjectInfo
            {
                GroupName = reader["group_name"]?.ToString() ?? string.Empty,
                TaskEnd = Convert.ToInt32(reader["task_end"], CultureInfo.InvariantCulture) == 1,
                Index = Convert.ToInt32(reader["project_index"], CultureInfo.InvariantCulture),
                Name = reader["project_name"]?.ToString() ?? string.Empty,
                FolderName = reader["folder_name"]?.ToString() ?? string.Empty,
                StartTime = RuntimePersistenceDatabase.TryParseDateTime(reader["start_time"]?.ToString()) ?? DateTime.MinValue,
                EndTime = RuntimePersistenceDatabase.TryParseDateTime(reader["end_time"]?.ToString()),
                Status = Convert.ToInt32(reader["status"], CultureInfo.InvariantCulture)
            });
        }

        return result;
    }

    private static TaskProgress.ScriptGroupProjectInfo? ReadProjectInfo(
        SqliteDataReader reader,
        string groupNameColumn,
        string indexColumn,
        string nameColumn,
        string folderColumn,
        string startTimeColumn,
        string endTimeColumn,
        string statusColumn,
        string taskEndColumn)
    {
        var groupName = reader[groupNameColumn]?.ToString();
        var projectName = reader[nameColumn]?.ToString();
        var folderName = reader[folderColumn]?.ToString();
        if (string.IsNullOrWhiteSpace(groupName) && string.IsNullOrWhiteSpace(projectName) && string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        return new TaskProgress.ScriptGroupProjectInfo
        {
            GroupName = groupName ?? string.Empty,
            Index = reader[indexColumn] == DBNull.Value ? 0 : Convert.ToInt32(reader[indexColumn], CultureInfo.InvariantCulture),
            Name = projectName ?? string.Empty,
            FolderName = folderName ?? string.Empty,
            StartTime = RuntimePersistenceDatabase.TryParseDateTime(reader[startTimeColumn]?.ToString()) ?? DateTime.MinValue,
            EndTime = RuntimePersistenceDatabase.TryParseDateTime(reader[endTimeColumn]?.ToString()),
            Status = reader[statusColumn] == DBNull.Value ? 0 : Convert.ToInt32(reader[statusColumn], CultureInfo.InvariantCulture),
            TaskEnd = reader[taskEndColumn] != DBNull.Value && Convert.ToInt32(reader[taskEndColumn], CultureInfo.InvariantCulture) == 1
        };
    }

    private static void BindProjectInfo(
        SqliteCommand command,
        TaskProgress.ScriptGroupProjectInfo? info,
        string groupNameParameter,
        string indexParameter,
        string nameParameter,
        string folderParameter,
        string startTimeParameter,
        string endTimeParameter,
        string statusParameter,
        string taskEndParameter)
    {
        command.Parameters.AddWithValue(groupNameParameter, info?.GroupName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(indexParameter, info != null ? info.Index : (object)DBNull.Value);
        command.Parameters.AddWithValue(nameParameter, info?.Name ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(folderParameter, info?.FolderName ?? (object)DBNull.Value);
        command.Parameters.AddWithValue(startTimeParameter, info != null
            ? RuntimePersistenceDatabase.ToRoundtrip(info.StartTime)
            : (object)DBNull.Value);
        command.Parameters.AddWithValue(endTimeParameter, info?.EndTime.HasValue == true
            ? RuntimePersistenceDatabase.ToRoundtrip(info.EndTime.Value)
            : (object)DBNull.Value);
        command.Parameters.AddWithValue(statusParameter, info != null ? info.Status : (object)DBNull.Value);
        command.Parameters.AddWithValue(taskEndParameter, info != null ? (info.TaskEnd ? 1 : 0) : (object)DBNull.Value);
    }

    private static void PruneStaleSessions(SqliteConnection connection)
    {
        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-3).ToString("O", CultureInfo.InvariantCulture);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
                               DELETE FROM {RuntimePersistenceDatabase.TaskProgressSessionTableName}
                               WHERE updated_utc < $cutoffUtc;
                               """;
        command.Parameters.AddWithValue("$cutoffUtc", cutoffUtc);
        command.ExecuteNonQuery();
    }
}
