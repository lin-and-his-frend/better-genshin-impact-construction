using System;
using System.IO;
using System.Linq;

namespace BetterGenshinImpact.Core.Script.Utils;

public class ScriptUtils
{
    /// <summary>
    /// Normalize and validate a path.
    /// </summary>
    public static string NormalizePath(string root, string path)
    {
        // 校验空字符串
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("文件路径不能为空");

        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("根目录不能为空");

        if (Path.IsPathRooted(path))
            throw new ArgumentException($"文件路径 '{path}' 不能是绝对路径");

        // 检查是否含有非法文件名字符
        var invalidChars = Path.GetInvalidFileNameChars();
        string fileName = Path.GetFileName(path);
        if (fileName.Any(c => invalidChars.Contains(c)))
        {
            throw new ArgumentException($"文件路径 '{path}' 包含非法字符");
        }

        // 替换分隔符
        path = path.Replace('\\', '/');

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, path));

        // 防止越界访问
        if (!IsSubPathOf(fullRoot, fullPath))
        {
            throw new ArgumentException($"文件路径 '{path}' 越界访问!");
        }

        return fullPath;
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

        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedTarget.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
}
