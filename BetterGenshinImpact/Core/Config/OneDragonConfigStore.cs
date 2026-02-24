using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace BetterGenshinImpact.Core.Config;

internal static class OneDragonConfigStore
{
    private const string ConfigFolder = "OneDragon";

    internal static IReadOnlyList<OneDragonFlowConfig> LoadAll()
    {
        var configs = LoadFromDb();
        if (configs.Count == 0)
        {
            configs = TryMigrateFromDisk();
        }

        if (configs.Count == 0)
        {
            configs.Add(new OneDragonFlowConfig { Name = "默认配置" });
        }

        return configs;
    }

    internal static OneDragonFlowConfig? LoadByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return LoadAll().FirstOrDefault();
        }

        if (UserStorage.TryReadOneDragonConfig(name, out var json) && !string.IsNullOrWhiteSpace(json))
        {
            try
            {
                return JsonConvert.DeserializeObject<OneDragonFlowConfig>(json);
            }
            catch
            {
                return null;
            }
        }

        return LoadAll().FirstOrDefault(config =>
            string.Equals(config.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool Save(OneDragonFlowConfig config)
    {
        var json = JsonConvert.SerializeObject(config, Formatting.Indented);
        return UserStorage.TryWriteOneDragonConfig(config.Name, json);
    }

    internal static bool Delete(string name)
    {
        return UserStorage.DeleteOneDragonConfig(name);
    }

    internal static bool Rename(string oldName, OneDragonFlowConfig config)
    {
        var json = JsonConvert.SerializeObject(config, Formatting.Indented);
        var saved = UserStorage.TryWriteOneDragonConfig(config.Name, json);
        if (saved && !string.Equals(oldName, config.Name, StringComparison.OrdinalIgnoreCase))
        {
            UserStorage.DeleteOneDragonConfig(oldName);
        }

        return saved;
    }

    internal static DateTimeOffset? GetLatestUpdatedUtc()
    {
        return UserStorage.GetOneDragonLatestUpdatedUtc();
    }

    internal static IReadOnlyList<string> ListNames()
    {
        return LoadAll().Select(config => config.Name).ToList();
    }

    private static List<OneDragonFlowConfig> LoadFromDb()
    {
        var entries = UserStorage.ListOneDragonConfigs()
            .OrderBy(entry => entry.UpdatedUtc ?? DateTimeOffset.MinValue);

        var configs = new List<OneDragonFlowConfig>();
        foreach (var entry in entries)
        {
            try
            {
                var config = JsonConvert.DeserializeObject<OneDragonFlowConfig>(entry.Content);
                if (config != null)
                {
                    configs.Add(config);
                }
            }
            catch
            {
            }
        }

        return configs;
    }

    private static List<OneDragonFlowConfig> TryMigrateFromDisk()
    {
        var configs = new List<OneDragonFlowConfig>();
        var folder = Path.Combine(Global.UserDataRoot, ConfigFolder);
        if (!Directory.Exists(folder))
        {
            return configs;
        }

        foreach (var file in Directory.GetFiles(folder, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var config = JsonConvert.DeserializeObject<OneDragonFlowConfig>(json);
                if (config == null || string.IsNullOrWhiteSpace(config.Name))
                {
                    continue;
                }

                if (UserStorage.TryWriteOneDragonConfig(config.Name, json))
                {
                    configs.Add(config);
                }
            }
            catch
            {
            }
        }

        return configs;
    }
}
