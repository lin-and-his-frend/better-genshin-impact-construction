using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Persistence.Runtime;
using BetterGenshinImpact.Helpers;
using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.LogParse;

public class ExecutionRecordStorage
{
    /// <summary>
    /// 保存执行记录到 SQLite。
    /// 旧的按日期 JSON 文件仅用于首次迁移导入，不再作为正式写入目标。
    /// </summary>
    public static void SaveExecutionRecord(ExecutionRecord record)
    {
        ExecutionRecordRuntimeRepository.Save(record);
    }

    public static List<DailyExecutionRecord> GetRecentExecutionRecordsByConfig(TaskCompletionSkipRuleConfig config)
    {
        bool boundaryTimeEnable = config.BoundaryTime >= 0 && config.BoundaryTime <= 23;

        var dayCount = 1;
        if (boundaryTimeEnable)
        {
            dayCount = 2;
        }

        if (config.LastRunGapSeconds >= 0)
        {
            dayCount = ConvertSecondsToDaysUp(config.LastRunGapSeconds);
        }

        return GetRecentExecutionRecords(dayCount);
    }

    /// <summary>
    /// 读取最近 N 天的执行记录。
    /// </summary>
    public static List<DailyExecutionRecord> GetRecentExecutionRecords(int days)
    {
        return ExecutionRecordRuntimeRepository.GetRecent(days);
    }

    private static int ConvertSecondsToDaysUp(int seconds)
    {
        if (seconds <= 0)
        {
            return 0;
        }

        const int secondsPerDay = 86400;
        var days = (double)seconds / secondsPerDay;
        return (int)Math.Ceiling(days);
    }

    /// <summary>
    /// 根据自定义的一天开始时间判断日期是否属于"今天"。
    /// </summary>
    private static bool IsTodayByBoundary(int boundaryHour, DateTimeOffset targetDate, bool isBoundaryTimeBasedOnServerTime)
    {
        if (boundaryHour < 0 || boundaryHour > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(boundaryHour), "分界时间必须在0-23之间");
        }

        DateTimeOffset now = isBoundaryTimeBasedOnServerTime
            ? ServerTimeHelper.GetServerTimeNow()
            : DateTimeOffset.Now;

        DateTime todayStart;
        if (now.Hour >= boundaryHour)
        {
            todayStart = new DateTime(now.Year, now.Month, now.Day, boundaryHour, 0, 0);
        }
        else
        {
            todayStart = new DateTime(now.Year, now.Month, now.Day, boundaryHour, 0, 0).AddDays(-1);
        }

        var todayEnd = todayStart.AddDays(1);
        return targetDate >= todayStart && targetDate < todayEnd;
    }

    public static bool IsSkipTask(ScriptGroupProject project, out string message, List<DailyExecutionRecord>? dailyRecords = null)
    {
        message = "";

        var config = project.GroupInfo?.Config?.PathingConfig.TaskCompletionSkipRuleConfig;
        if (config == null ||
            !config.Enable ||
            (config.BoundaryTime < 0 || config.BoundaryTime > 23) && config.LastRunGapSeconds < 0)
        {
            return false;
        }

        bool boundaryTimeEnable = config.BoundaryTime >= 0 && config.BoundaryTime <= 23;

        var groupName = project.GroupInfo?.Name ?? "";
        var folderName = project.FolderName;
        var projectName = project.Name;
        var projectType = project.Type;

        dailyRecords ??= GetRecentExecutionRecordsByConfig(config);

        foreach (var dailyRecord in dailyRecords)
        {
            var records = dailyRecord.ExecutionRecords;
            foreach (var record in records)
            {
                if (!record.IsSuccessful)
                {
                    continue;
                }

                if (record.Type != projectType || record.ProjectName != projectName)
                {
                    continue;
                }

                var calcTime = record.EndTime;
                if (config.ReferencePoint == "StartTime")
                {
                    calcTime = record.StartTime;
                }

                if (config.LastRunGapSeconds >= 0)
                {
                    var secondsSinceLastRun = (DateTime.Now - calcTime).TotalSeconds;
                    if (secondsSinceLastRun > config.LastRunGapSeconds)
                    {
                        continue;
                    }
                }

                if (boundaryTimeEnable)
                {
                    if (!IsTodayByBoundary(
                            config.BoundaryTime,
                            record.ServerStartTime ??
                            new DateTimeOffset(record.StartTime).ToOffset(ServerTimeHelper.GetServerTimeOffset()),
                            config.IsBoundaryTimeBasedOnServerTime))
                    {
                        continue;
                    }
                }

                bool isMatchFound = false;
                string matchReason;
                if (config.SkipPolicy == "GroupPhysicalPathSkipPolicy" &&
                    groupName == record.GroupName &&
                    folderName == record.FolderName)
                {
                    matchReason = "组和物理路径匹配一致";
                    isMatchFound = true;
                }
                else if (config.SkipPolicy == "PhysicalPathSkipPolicy" &&
                         folderName == record.FolderName)
                {
                    matchReason = "物理路径相同";
                    isMatchFound = true;
                }
                else if (config.SkipPolicy == "SameNameSkipPolicy")
                {
                    matchReason = "名称相同";
                    isMatchFound = true;
                }
                else
                {
                    Console.WriteLine("ExecutionRecordStorage: 未预期的跳过策略！");
                    continue;
                }

                if (!isMatchFound)
                {
                    continue;
                }

                message = $"检查出满足跳过条件: {matchReason}";
                if (config.LastRunGapSeconds >= 0)
                {
                    var nextExecutionTime = calcTime.AddSeconds(config.LastRunGapSeconds);
                    message += $", 需在 {nextExecutionTime:yyyy-M-d H:mm:ss} 之后才能开始执行";
                }
                else if (boundaryTimeEnable)
                {
                    message += $", 需在下一日 {config.BoundaryTime} 点后才能开始执行";
                }

                message += $", 匹配记录 GUID={record.Id}";
                return true;
            }
        }

        return false;
    }
}
