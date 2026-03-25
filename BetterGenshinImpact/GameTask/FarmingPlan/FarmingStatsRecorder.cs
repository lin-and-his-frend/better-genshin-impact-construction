using BetterGenshinImpact.Persistence.Runtime;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.LogParse;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BetterGenshinImpact.Helpers;

namespace BetterGenshinImpact.GameTask.FarmingPlan;

/// <summary>
/// 锄地统计记录器。
/// 当前实现以 SQLite 作为业务真源；旧的 log/FarmingPlan/*.json 仅用于首次迁移导入。
/// </summary>
public static class FarmingStatsRecorder
{
    public static bool debugMode = false;

    public static void debugInfo(string msg)
    {
        if (debugMode)
        {
            TaskControl.Logger.LogDebug(msg);
        }
    }

    public static bool IsDailyFarmingLimitReached(FarmingSession farmingSession, out string message)
    {
        if (!farmingSession.AllowFarmingCount || farmingSession.PrimaryTarget == "disable")
        {
            message = "";
            return false;
        }

        var dailyFarmingData = ReadDailyFarmingData();
        var config = TaskContext.Instance().Config.OtherConfig.FarmingPlanConfig;
        var mysdCfg = config.MiyousheDataConfig;
        bool mysEnable = mysdCfg.Enabled;
        var cap = dailyFarmingData.getFinalCap();
        var ft = dailyFarmingData.getFinalTotalMobCount();

        var dailyEliteCap = cap.DailyEliteCap;
        var dailyMobCap = cap.DailyMobCap;
        var totalEliteMobCount = ft.TotalEliteMobCount;
        var totalNormalMobCount = ft.TotalNormalMobCount;

        bool isEliteOverLimit = totalEliteMobCount >= dailyEliteCap;
        bool isNormalOverLimit = totalNormalMobCount >= dailyMobCap;

        var messages = new List<string>();
        if (isEliteOverLimit)
        {
            messages.Add($"精英超上限:{totalEliteMobCount}/{dailyEliteCap}");
        }

        if (isNormalOverLimit)
        {
            messages.Add($"小怪超上限:{totalNormalMobCount}/{dailyMobCap}");
        }

        debugInfo($"尝试更新米游社：{DateTime.Now} > {dailyFarmingData.TravelsDiaryDetailManagerUpdateTime.AddHours(2)}&&{DateTime.Now}> {dailyFarmingData.LastMiyousheUpdateTime.AddMinutes(20)}");
        if (mysEnable
            && DateTime.Now > dailyFarmingData.TravelsDiaryDetailManagerUpdateTime.AddHours(2)
            && DateTime.Now > dailyFarmingData.LastMiyousheUpdateTime.AddMinutes(20))
        {
            Task.Run(() => TryUpdateTravelsData());
        }

        if (isEliteOverLimit && isNormalOverLimit)
        {
            message = string.Join(",", messages);
            return true;
        }

        if (farmingSession.NormalMobCount == 0 && farmingSession.EliteMobCount == 0)
        {
            messages.Add("精英和小怪计数都为0，请确认配置");
            message = string.Join(",", messages);
            return true;
        }

        if ((farmingSession.EliteMobCount == 0 && farmingSession.PrimaryTarget == "elite")
            || (farmingSession.NormalMobCount == 0 && farmingSession.PrimaryTarget == "normal"))
        {
            messages.Add("主目标计数为0，请确认配置");
            message = string.Join(",", messages);
            return true;
        }

        bool result = false;

        if (farmingSession.PrimaryTarget == "elite" && isEliteOverLimit)
        {
            result = true;
            if (farmingSession.NormalMobCount > 0)
            {
                messages.Add("脚本主目标为精英");
            }
        }
        else if (farmingSession.PrimaryTarget == "normal" && isNormalOverLimit)
        {
            result = true;
            if (farmingSession.EliteMobCount > 0)
            {
                messages.Add("脚本主目标为小怪");
            }
        }

        if (!result)
        {
            result = (isEliteOverLimit && farmingSession.NormalMobCount == 0) ||
                     (isNormalOverLimit && farmingSession.EliteMobCount == 0);
        }

        message = string.Join(",", messages);
        return result;
    }

    /// <summary>
    /// 记录一次锄地路径的统计。
    /// </summary>
    public static void RecordFarmingSession(FarmingSession session, FarmingRouteInfo route)
    {
        try
        {
            var now = DateTime.Now;
            var statDate = GetCurrentStatDateString();
            FarmingStatsRuntimeRepository.AppendSession(statDate, session, route, now);

            var dailyData = FarmingStatsRuntimeRepository.Load(statDate);
            var ft = dailyData.getFinalTotalMobCount();
            var cap = dailyData.getFinalCap();
            TaskControl.Logger.LogInformation(
                $"锄地进度:[小怪:{ft.TotalNormalMobCount}/{cap.DailyMobCap}" +
                $",精英:{ft.TotalEliteMobCount}/{cap.DailyEliteCap}]" +
                (dailyData.EnableMiyousheStats() ? "(合并米游社数据)" : ""));
        }
        catch (Exception e)
        {
            TaskControl.Logger.LogError($"锄地进度记录失败：{e.Message}");
        }
    }

    private static bool _isUpdating = false;

    public static async Task TryUpdateTravelsData()
    {
        if (_isUpdating)
        {
            return;
        }

        try
        {
            _isUpdating = true;
            debugInfo("开始更新米游社札记");
            string cookie = TaskContext.Instance().Config.OtherConfig.MiyousheConfig.Cookie;
            DailyFarmingData? dailyFarmingData = null;
            if (TaskContext.Instance().Config.OtherConfig.FarmingPlanConfig.MiyousheDataConfig.Enabled
                && cookie != string.Empty)
            {
                try
                {
                    GameInfo gameInfo = await TravelsDiaryDetailManager.UpdateTravelsDiaryDetailManager(cookie, true);
                    List<ActionItem> actionItems = TravelsDiaryDetailManager.loadNowDayActionItems(gameInfo);
                    MoraStatistics ms = new();
                    ms.ActionItems.AddRange(actionItems);
                    dailyFarmingData = ReadDailyFarmingData();

                    if (actionItems.Count > 0)
                    {
                        dailyFarmingData.MiyousheTotalEliteMobCount = ms.EliteGameStatistics;
                        dailyFarmingData.MiyousheTotalNormalMobCount = ms.SmallMonsterStatistics;
                        dailyFarmingData.TravelsDiaryDetailManagerUpdateTime = DateTime.Parse(actionItems[^1].Time);
                        debugInfo($"札记当天数据：[精英：{dailyFarmingData.MiyousheTotalEliteMobCount},小怪：{dailyFarmingData.MiyousheTotalNormalMobCount},{dailyFarmingData.TravelsDiaryDetailManagerUpdateTime}]");
                    }
                    else
                    {
                        TaskControl.Logger.LogError("米游社旅行札记未有数据！");
                    }
                }
                catch (Exception e)
                {
                    TaskControl.Logger.LogError($"米游社数据更新失败，请检查cookie是否过期：{e.Message}");
                }
            }

            dailyFarmingData ??= ReadDailyFarmingData();
            dailyFarmingData.LastMiyousheUpdateTime = DateTime.Now;
            FarmingStatsRuntimeRepository.SaveSummary(GetCurrentStatDateString(), dailyFarmingData);
        }
        finally
        {
            _isUpdating = false;
        }
    }

    public static DailyFarmingData ReadDailyFarmingData()
    {
        var statDate = GetCurrentStatDateString();
        var dailyData = FarmingStatsRuntimeRepository.Load(statDate);
        dailyData.FilePath = string.Empty;
        return dailyData;
    }

    private static string GetCurrentStatDateString()
    {
        DateTimeOffset now = ServerTimeHelper.GetServerTimeNow();
        DateTimeOffset statsDate = CalculateStatsDate(now);
        return statsDate.ToString("yyyyMMdd");
    }

    /// <summary>
    /// 计算统计日期（凌晨4点为分界）。
    /// </summary>
    private static DateTime CalculateStatsDate(DateTimeOffset currentTime)
    {
        return currentTime.Hour < 4 ? currentTime.Date.AddDays(-1) : currentTime.Date;
    }
}
