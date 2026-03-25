using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 统一管理 BetterGI 在 User 根目录下的文件化资产、规则、资源、缓存和日志路径。
/// 这里同时承担旧逻辑路径到新目录结构的兼容映射，避免各模块继续散落地拼接硬编码目录。
/// </summary>
internal static class UserPathProvider
{
    private static readonly IReadOnlyDictionary<string, string[]> LegacyUserRootAliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["JsScript"] = ["Scripts", "Js"],
            ["KeyMouseScript"] = ["Scripts", "KeyMouse"],
            ["AutoFight"] = ["Scripts", "Combat"],
            ["AutoGeniusInvokation"] = ["Scripts", "Tcg"],
            ["AutoPathing"] = ["Scripts", "Pathing"],
            ["ScriptGroup"] = ["Workflows", "ScriptGroup"],
            ["OneDragon"] = ["Workflows", "OneDragon"],
            ["I18n"] = ["ResourcePacks", "I18n"],
            ["Images"] = ["Resources", "Images"],
            ["LogParse"] = ["Settings", "LogParse"],
            ["Subscriptions"] = ["Settings", "ScriptRepo", "Subscriptions"],
            ["Temp"] = ["Cache", "Temp"],
            ["Cache"] = ["Cache"],
        };

    private static readonly string[] AllowedBackupPrefixes =
    [
        "Scripts",
        "Workflows",
        "Rules",
        "ResourcePacks",
        "Resources",
        "Settings",
        "JsScript",
        "KeyMouseScript",
        "AutoFight",
        "AutoGeniusInvokation",
        "AutoPathing",
        "ScriptGroup",
        "OneDragon",
        "I18n",
        "Images",
        "LogParse",
        "Subscriptions",
        "pick_black_lists.txt",
        "pick_fuzzy_black_lists.txt",
        "pick_white_lists.txt",
        "pick_black_lists.json",
        "pick_white_lists.json"
    ];

    public static string UserRoot => Global.UserDataRoot;

    public static string ScriptsRoot => CombineUnderUser("Scripts");

    public static string JsScriptsRoot => CombineUnderUser("Scripts", "Js");

    public static string KeyMouseScriptsRoot => CombineUnderUser("Scripts", "KeyMouse");

    public static string CombatScriptsRoot => CombineUnderUser("Scripts", "Combat");

    public static string TcgScriptsRoot => CombineUnderUser("Scripts", "Tcg");

    public static string PathingScriptsRoot => CombineUnderUser("Scripts", "Pathing");

    public static string WorkflowsRoot => CombineUnderUser("Workflows");

    public static string ScriptGroupRoot => CombineUnderUser("Workflows", "ScriptGroup");

    public static string OneDragonRoot => CombineUnderUser("Workflows", "OneDragon");

    public static string RulesRoot => CombineUnderUser("Rules");

    public static string PickRulesRoot => CombineUnderUser("Rules", "Pick");

    public static string PickExactBlacklistPath => CombineUnderUser("Rules", "Pick", "blacklist.txt");

    public static string PickFuzzyBlacklistPath => CombineUnderUser("Rules", "Pick", "fuzzy_blacklist.txt");

    public static string PickWhitelistPath => CombineUnderUser("Rules", "Pick", "whitelist.txt");

    public static string LegacyPickExactBlacklistTextPath => CombineUnderUser("pick_black_lists.txt");

    public static string LegacyPickFuzzyBlacklistTextPath => CombineUnderUser("pick_fuzzy_black_lists.txt");

    public static string LegacyPickWhitelistTextPath => CombineUnderUser("pick_white_lists.txt");

    public static string LegacyPickBlacklistJsonPath => CombineUnderUser("pick_black_lists.json");

    public static string LegacyPickWhitelistJsonPath => CombineUnderUser("pick_white_lists.json");

    public static string ResourcePacksRoot => CombineUnderUser("ResourcePacks");

    public static string I18nRoot => CombineUnderUser("ResourcePacks", "I18n");

    public static string ResourcesRoot => CombineUnderUser("Resources");

    public static string ImagesRoot => CombineUnderUser("Resources", "Images");

    public static string CustomBannerImagePath => CombineUnderUser("Resources", "Images", "custom_banner.jpg");

    public static string SettingsRoot => CombineUnderUser("Settings");

    public static string LogParseSettingsRoot => CombineUnderUser("Settings", "LogParse");

    public static string LogParseConfigPath => CombineUnderUser("Settings", "LogParse", "config.json");

    public static string ScriptRepoSettingsRoot => CombineUnderUser("Settings", "ScriptRepo");

    public static string ScriptRepoSubscriptionsRoot => CombineUnderUser("Settings", "ScriptRepo", "Subscriptions");

    public static string CacheRoot => CombineUnderUser("Cache");

    public static string ScriptRepoCacheRoot => CombineUnderUser("Cache", "ScriptRepo");

    public static string ScriptRepoRepositoriesRoot => CombineUnderUser("Cache", "ScriptRepo", "Repos");

    public static string ScriptRepoTempRoot => CombineUnderUser("Cache", "ScriptRepo", "Temp");

    public static string ScriptRepoFolderMappingPath => CombineUnderUser("Cache", "ScriptRepo", "repo_folder_mapping.json");

    public static string LogRoot => CombineUnderUser("Log");

    public static string ScreenshotLogRoot => CombineUnderUser("Log", "screenshot");

    public static string AppLogRoot => CombineUnderUser("Log", "App");

    public static string AppLogFilePath => CombineUnderUser("Log", "App", "better-genshin-impact.log");

    public static string LogParseCacheRoot => CombineUnderUser("Cache", "Remote", "Miyoushe");

    public static string LegacyOneDragonRoot => Path.Combine(UserRoot, "OneDragon");

    public static IReadOnlyList<string> BackupDirectories =>
    [
        ScriptsRoot,
        WorkflowsRoot,
        RulesRoot,
        ResourcePacksRoot,
        ResourcesRoot,
        SettingsRoot
    ];

    public static string CombineUnderUser(params string[] segments)
    {
        var combined = Path.Combine(segments);
        var fullPath = Path.GetFullPath(Path.Combine(UserRoot, combined));
        EnsureUnderUserRoot(fullPath);
        return fullPath;
    }

    public static bool TryResolveManagedPath(string path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            string userRelativePath;
            if (Path.IsPathRooted(path))
            {
                var candidate = Path.GetFullPath(path);
                if (!IsSubPathOf(UserRoot, candidate))
                {
                    return false;
                }

                userRelativePath = Path.GetRelativePath(UserRoot, candidate);
            }
            else
            {
                var trimmed = path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!trimmed.StartsWith("User" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("User/", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                userRelativePath = trimmed.Substring(5);
            }

            fullPath = ResolveUserRelativePath(userRelativePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryResolveVirtualPath(string path, out string fullPath)
    {
        if (TryResolveManagedPath(path, out fullPath))
        {
            return true;
        }

        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            return false;
        }

        var normalized = path.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        if (normalized.Equals("log", StringComparison.OrdinalIgnoreCase))
        {
            fullPath = LogRoot;
            return true;
        }

        if (normalized.StartsWith("log" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var remaining = normalized.Substring(4);
            fullPath = Path.Combine(LogRoot, remaining);
            return true;
        }

        if (normalized.Equals("Repos", StringComparison.OrdinalIgnoreCase))
        {
            fullPath = ScriptRepoRepositoriesRoot;
            return true;
        }

        if (normalized.StartsWith("Repos" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            var remaining = normalized.Substring(6);
            fullPath = Path.Combine(ScriptRepoRepositoriesRoot, remaining);
            return true;
        }

        return false;
    }

    public static string ResolveUserRelativePath(string relativePath)
    {
        var normalizedRelative = NormalizeRelativePath(relativePath);
        var parts = normalizedRelative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return UserRoot;
        }

        if (LegacyUserRootAliases.TryGetValue(parts[0], out var mappedSegments))
        {
            var merged = new string[mappedSegments.Length + parts.Length - 1];
            mappedSegments.CopyTo(merged, 0);
            Array.Copy(parts, 1, merged, mappedSegments.Length, parts.Length - 1);
            return CombineUnderUser(merged);
        }

        return CombineUnderUser(parts);
    }

    public static bool TryResolveBackupEntryPath(string entryName, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return false;
        }

        var normalized = entryName.Trim()
            .TrimStart('/', '\\')
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        if (string.IsNullOrWhiteSpace(normalized) || normalized.Contains(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return false;
        }

        var root = normalized.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(root) ||
            !AllowedBackupPrefixes.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            fullPath = ResolveUserRelativePath(normalized);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static void EnsureUnderUserRoot(string fullPath)
    {
        if (!IsSubPathOf(UserRoot, fullPath))
        {
            throw new InvalidOperationException($"User path out of root: {fullPath}");
        }
    }

    private static bool IsSubPathOf(string rootPath, string targetPath)
    {
        var normalizedRoot = Path.GetFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(targetPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedRoot, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedTarget.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
