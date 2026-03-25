using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Persistence.Runtime;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.TaskProgress;

public class TaskProgressManager
{
    public static ILogger Logger { get; } = App.GetLogger<TaskProgressManager>();

    /// <summary>
    /// 将调度器继续执行进度直接保存到 SQLite。
    /// 旧的 log/task_progress/*.json 仅在首次读取时导入。
    /// </summary>
    public static void SaveTaskProgress(TaskProgress taskProgress)
    {
        TaskProgressRuntimeRepository.Save(taskProgress);
    }

    public static List<TaskProgress> LoadAllTaskProgress()
    {
        return TaskProgressRuntimeRepository.LoadAllActive();
    }

    public static void GenerNextProjectInfo(
        TaskProgress taskProgress,
        List<ScriptGroup> scriptGroups)
    {
        var currentGroupIndex = 0;
        var currentProjectIndex = -1;

        if (taskProgress.LastScriptGroupName != null)
        {
            currentGroupIndex = scriptGroups.FindIndex(g => g.Name == taskProgress.LastScriptGroupName);
            if (currentGroupIndex == -1)
            {
                return;
            }
        }

        var currentGroup = scriptGroups[currentGroupIndex];
        var isLastInGroup = false;
        if (taskProgress.LastSuccessScriptGroupProjectInfo != null)
        {
            var currentProjectInfo = taskProgress.LastSuccessScriptGroupProjectInfo;

            currentProjectIndex = currentGroup.Projects.ToList().FindIndex(p =>
                p.Name == currentProjectInfo.Name &&
                p.FolderName == currentProjectInfo.FolderName);

            if (currentProjectIndex == -1)
            {
                return;
            }

            isLastInGroup = currentProjectIndex == currentGroup.Projects.Count - 1;
        }

        if (isLastInGroup)
        {
            for (var i = currentGroupIndex + 1; i < scriptGroups.Count; i++)
            {
                var group = scriptGroups[i];
                if (group.Projects != null && group.Projects.Any())
                {
                    var project = group.Projects.First();

                    taskProgress.Next = new TaskProgress.Progress
                    {
                        GroupName = group.Name,
                        Index = 0,
                        ProjectName = project.Name,
                        FolderName = project.FolderName
                    };
                    return;
                }
            }

            if (taskProgress.Loop)
            {
                for (var i = 0; i < currentGroupIndex; i++)
                {
                    var group = scriptGroups[i];
                    if (group.Projects != null && group.Projects.Any())
                    {
                        var project = group.Projects.First();
                        taskProgress.Next = new TaskProgress.Progress
                        {
                            GroupName = group.Name,
                            Index = 0,
                            ProjectName = project.Name,
                            FolderName = project.FolderName
                        };
                        return;
                    }
                }
            }

            return;
        }

        currentProjectIndex++;
        var currentProject = currentGroup.Projects[currentProjectIndex];
        taskProgress.Next = new TaskProgress.Progress
        {
            GroupName = currentGroup.Name,
            Index = currentProjectIndex,
            ProjectName = currentProject.Name,
            FolderName = currentProject.FolderName
        };
    }
}
