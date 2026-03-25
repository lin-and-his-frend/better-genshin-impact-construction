using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BetterGenshinImpact.ViewModel.Windows;

public class AutoPickBlackListViewModel : FormViewModel<string>
{
    public AutoPickBlackListViewModel()
    {
        var blacklistText = UserFileService.ReadFirstAvailableText(
            [
                UserPathProvider.PickExactBlacklistPath,
                UserPathProvider.LegacyPickExactBlacklistTextPath
            ]);
        if (!string.IsNullOrWhiteSpace(blacklistText))
        {
            var blackList = blacklistText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
            AddRange(blackList);
            return;
        }

        var legacyBlacklistJson = UserFileService.ReadAllTextIfExists(UserPathProvider.LegacyPickBlacklistJsonPath);
        if (!string.IsNullOrWhiteSpace(legacyBlacklistJson))
        {
            var blackList = JsonSerializer.Deserialize<List<string>>(legacyBlacklistJson) ?? [];
            AddRange(blackList);
        }
    }

    public new void OnSave()
    {
        var blackListText = string.Join(Environment.NewLine, List);
        UserFileService.WriteAllText(UserPathProvider.PickExactBlacklistPath, blackListText);
        GameTaskManager.RefreshTriggerConfigs();
    }
}
