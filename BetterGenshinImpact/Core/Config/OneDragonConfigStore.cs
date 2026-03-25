using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace BetterGenshinImpact.Core.Config;

internal static class OneDragonConfigStore
{
    // 一条龙配置改回文件化工作流资产，统一落在 User\Workflows\OneDragon。
    // 这里保留对旧磁盘目录和旧数据库表的只读迁移兼容，但新的读写不再回写数据库。
    private static string ConfigDirectory => UserPathProvider.OneDragonRoot;

    internal static IReadOnlyList<OneDragonFlowConfig> LoadAll()
    {
        var configs = LoadFromDisk();
        if (configs.Count == 0)
        {
            configs = TryMigrateFromLegacyDisk();
        }

        if (configs.Count == 0)
        {
            configs = TryMigrateFromDb();
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

        if (TryGetConfigFilePath(name, out var filePath))
        {
            try
            {
                var json = UserFileService.ReadAllTextIfExists(filePath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    return JsonConvert.DeserializeObject<OneDragonFlowConfig>(json);
                }
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
        if (string.IsNullOrWhiteSpace(config.Name) || !TryGetConfigFilePath(config.Name, out var filePath))
        {
            return false;
        }

        var json = JsonConvert.SerializeObject(config, Formatting.Indented);
        UserFileService.WriteAllText(filePath, json);
        return true;
    }

    internal static bool Delete(string name)
    {
        return TryGetConfigFilePath(name, out var filePath) && UserFileService.DeleteFileIfExists(filePath);
    }

    internal static bool Rename(string oldName, OneDragonFlowConfig config)
    {
        if (string.IsNullOrWhiteSpace(oldName) ||
            string.IsNullOrWhiteSpace(config.Name) ||
            !TryGetConfigFilePath(oldName, out var oldFilePath) ||
            !TryGetConfigFilePath(config.Name, out var newFilePath))
        {
            return false;
        }

        var json = JsonConvert.SerializeObject(config, Formatting.Indented);
        UserFileService.WriteAllText(newFilePath, json);
        if (!string.Equals(oldName, config.Name, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(oldFilePath, newFilePath, StringComparison.OrdinalIgnoreCase))
        {
            UserFileService.DeleteFileIfExists(oldFilePath);
        }

        return true;
    }

    internal static DateTimeOffset? GetLatestUpdatedUtc()
    {
        if (!Directory.Exists(ConfigDirectory))
        {
            return null;
        }

        var files = Directory.GetFiles(ConfigDirectory, "*.json", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            return null;
        }

        return files
            .Select(path => new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero))
            .Max();
    }

    internal static IReadOnlyList<string> ListNames()
    {
        return LoadAll().Select(config => config.Name).ToList();
    }

    private static List<OneDragonFlowConfig> LoadFromDisk()
    {
        Directory.CreateDirectory(ConfigDirectory);

        var configs = new List<OneDragonFlowConfig>();
        foreach (var file in Directory.GetFiles(ConfigDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => File.GetLastWriteTimeUtc(path)))
        {
            try
            {
                var json = UserFileService.ReadAllTextIfExists(file);
                var config = string.IsNullOrWhiteSpace(json)
                    ? null
                    : JsonConvert.DeserializeObject<OneDragonFlowConfig>(json);
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

    private static List<OneDragonFlowConfig> TryMigrateFromLegacyDisk()
    {
        var configs = new List<OneDragonFlowConfig>();
        var folder = UserPathProvider.LegacyOneDragonRoot;
        if (!Directory.Exists(folder))
        {
            return configs;
        }

        Directory.CreateDirectory(ConfigDirectory);
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

                if (!TryGetConfigFilePath(config.Name, out var filePath))
                {
                    continue;
                }

                UserFileService.WriteAllText(filePath, json);
                configs.Add(config);
            }
            catch
            {
            }
        }

        return configs;
    }

    private static List<OneDragonFlowConfig> TryMigrateFromDb()
    {
        var configs = new List<OneDragonFlowConfig>();
        foreach (var entry in UserStorage.ListOneDragonConfigs()
                     .OrderBy(item => item.UpdatedUtc ?? DateTimeOffset.MinValue))
        {
            try
            {
                var config = JsonConvert.DeserializeObject<OneDragonFlowConfig>(entry.Content);
                if (config == null || string.IsNullOrWhiteSpace(config.Name) || !TryGetConfigFilePath(config.Name, out var filePath))
                {
                    continue;
                }

                UserFileService.WriteAllText(filePath, entry.Content);
                configs.Add(config);
            }
            catch
            {
            }
        }

        return configs;
    }

    private static bool TryGetConfigFilePath(string name, out string filePath)
    {
        filePath = string.Empty;
        var normalizedName = name.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName) ||
            normalizedName is "." or ".." ||
            normalizedName.Contains('/') ||
            normalizedName.Contains('\\') ||
            normalizedName.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(normalizedName) ||
            normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        filePath = Path.Combine(ConfigDirectory, normalizedName + ".json");
        return true;
    }
}
