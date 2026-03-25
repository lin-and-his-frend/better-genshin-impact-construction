using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BetterGenshinImpact.ViewModel.Windows;

public class AutoPickWhiteListViewModel : FormViewModel<string>
{
    public AutoPickWhiteListViewModel()
    {
        var whitelistText = UserFileService.ReadFirstAvailableText(
            [
                UserPathProvider.PickWhitelistPath,
                UserPathProvider.LegacyPickWhitelistTextPath
            ]);
        if (!string.IsNullOrWhiteSpace(whitelistText))
        {
            var whiteList = whitelistText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToList();
            AddRange(whiteList);
            return;
        }

        var legacyWhiteListJson = UserFileService.ReadAllTextIfExists(UserPathProvider.LegacyPickWhitelistJsonPath);
        if (!string.IsNullOrWhiteSpace(legacyWhiteListJson))
        {
            var whiteList = JsonSerializer.Deserialize<List<string>>(legacyWhiteListJson) ?? [];
            AddRange(whiteList);
        }
    }

    public new void OnSave()
    {
        var whiteListText = string.Join(Environment.NewLine, List);
        UserFileService.WriteAllText(UserPathProvider.PickWhitelistPath, whiteListText);
        GameTaskManager.RefreshTriggerConfigs();
    }
}
