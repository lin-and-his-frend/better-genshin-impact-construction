using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Semver;

namespace BetterGenshinImpact.Core.Config;

public class Global
{
    public static string Version { get; } = Assembly.GetEntryAssembly()?.
        GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.
        InformationalVersion!;

    public static string StartUpPath { get; set; } = AppContext.BaseDirectory;
    public static string UserDataRoot => Path.Combine(StartUpPath, "User");

    public static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static string Absolute(string relativePath)
    {
        if (UserPathProvider.TryResolveVirtualPath(relativePath, out var managedPath))
        {
            return managedPath;
        }

        return Path.IsPathRooted(relativePath)
            ? Path.GetFullPath(relativePath)
            : Path.Combine(StartUpPath, relativePath);
    }

    public static string ScriptPath()
    {
        return UserPathProvider.JsScriptsRoot;
    }

    public static string? ReadAllTextIfExist(string relativePath)
    {
        var fullPath = Absolute(relativePath);
        return UserFileService.ReadAllTextIfExists(fullPath);
    }

    /// <summary>
    ///     新获取到的版本号与当前版本号比较，判断是否为新版本
    /// </summary>
    /// <param name="currentVersion">新获取到的版本</param>
    /// <returns></returns>
    public static bool IsNewVersion(string currentVersion)
    {
        return IsNewVersion(Version, currentVersion);
    }

    /// <summary>
    ///     新获取到的版本号与当前版本号比较，判断是否为新版本
    /// </summary>
    /// <param name="oldVersion">老版本</param>
    /// <param name="currentVersion">新获取到的版本</param>
    /// <returns>是否需要更新</returns>
    public static bool IsNewVersion(string oldVersion, string currentVersion)
    {
        try
        {
            var oldVersionX = SemVersion.Parse(oldVersion);
            var currentVersionX = SemVersion.Parse(currentVersion);

            if (currentVersionX.CompareSortOrderTo(oldVersionX) > 0)
                // 需要更新
                return true;
        }
        catch
        {
            ///
        }

        // 不需要更新
        return false;
    }

    public static void WriteAllText(string relativePath, string content)
    {
        var fullPath = Absolute(relativePath);
        UserFileService.WriteAllText(fullPath, content);
    }

    public static byte[]? ReadAllBytesIfExist(string relativePath)
    {
        var fullPath = Absolute(relativePath);
        return UserFileService.ReadAllBytesIfExists(fullPath);
    }

    public static void WriteAllBytes(string relativePath, byte[] content, bool isText = false)
    {
        var fullPath = Absolute(relativePath);
        UserFileService.WriteAllBytes(fullPath, content);
    }

    public static bool DeleteUserPath(string relativePath)
    {
        if (!UserPathProvider.TryResolveManagedPath(relativePath, out var fullPath))
        {
            return false;
        }

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string AbsoluteUserData(string relativePath)
    {
        if (UserPathProvider.TryResolveManagedPath(Path.Combine("User", relativePath), out var fullPath))
        {
            return fullPath;
        }

        return Path.Combine(UserDataRoot, relativePath);
    }

}
