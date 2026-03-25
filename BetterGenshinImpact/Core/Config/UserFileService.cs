using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 统一处理 BetterGI 文件化资产的原子读写。
/// 所有保留文件化的配置、规则、工作流和资源都应通过这里落盘，
/// 避免直接覆盖写导致的文件损坏和部分写入。
/// </summary>
internal static class UserFileService
{
    public static string? ReadAllTextIfExists(string filePath, Encoding? encoding = null)
    {
        return File.Exists(filePath) ? File.ReadAllText(filePath, encoding ?? Encoding.UTF8) : null;
    }

    public static string? ReadFirstAvailableText(IEnumerable<string> filePaths, Encoding? encoding = null)
    {
        foreach (var filePath in filePaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var content = ReadAllTextIfExists(filePath, encoding);
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }
        }

        return null;
    }

    public static byte[]? ReadAllBytesIfExists(string filePath)
    {
        return File.Exists(filePath) ? File.ReadAllBytes(filePath) : null;
    }

    public static void WriteAllText(string filePath, string content, Encoding? encoding = null)
    {
        WriteAllBytes(filePath, (encoding ?? Encoding.UTF8).GetBytes(content));
    }

    public static void WriteAllBytes(string filePath, byte[] content)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmpPath = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllBytes(tmpPath, content);

        if (File.Exists(filePath))
        {
            File.Replace(tmpPath, filePath, null);
        }
        else
        {
            File.Move(tmpPath, filePath);
        }
    }

    public static bool DeleteFileIfExists(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
