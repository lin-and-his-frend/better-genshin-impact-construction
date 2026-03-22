using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask;

public static class GenshinStartArgsValidator
{
    private sealed record ArgRule(
        string Name,
        int MinValueCount,
        int MaxValueCount,
        Func<string, bool> ValueValidator,
        string Usage
    );

    private static readonly ArgRule[] Rules =
    [
        new(
            "-window-mode",
            1,
            1,
            value => value.Equals("exclusive", StringComparison.OrdinalIgnoreCase),
            "`-window-mode exclusive`（独占全屏）"
        ),
        new(
            "-screen-fullscreen",
            0,
            1,
            IsFullscreenValue,
            "`-screen-fullscreen [0|1]`（全屏开关）"
        ),
        new(
            "-popupwindow",
            0,
            0,
            _ => true,
            "`-popupwindow`（无边框窗口）"
        ),
        new(
            "-platform_type",
            1,
            1,
            value => value.Equals("CLOUD_THIRD_PARTY_MOBILE", StringComparison.OrdinalIgnoreCase),
            "`-platform_type CLOUD_THIRD_PARTY_MOBILE`（触摸屏模式）"
        ),
        new(
            "-screen-width",
            1,
            1,
            value => IsIntInRange(value, 640, 16384),
            "`-screen-width <640-16384>`（窗口宽度）"
        ),
        new(
            "-screen-height",
            1,
            1,
            value => IsIntInRange(value, 480, 16384),
            "`-screen-height <480-16384>`（窗口高度）"
        ),
        new(
            "-monitor",
            1,
            1,
            value => IsIntInRange(value, 1, 16),
            "`-monitor <1-16>`（显示器编号）"
        )
    ];

    private static readonly Dictionary<string, ArgRule> RuleMap = Rules.ToDictionary(
        rule => rule.Name,
        StringComparer.OrdinalIgnoreCase
    );

    public static bool TryNormalize(string? rawArgs, out string normalizedArgs, out string errorMessage)
    {
        normalizedArgs = string.Empty;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(rawArgs))
        {
            return true;
        }

        var tokens = rawArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var normalizedTokens = new List<string>(tokens.Length);

        for (var i = 0; i < tokens.Length; i++)
        {
            var option = tokens[i];
            if (!option.StartsWith("-", StringComparison.Ordinal))
            {
                errorMessage = $"参数 `{option}` 不是合法的启动选项。";
                return false;
            }

            if (!RuleMap.TryGetValue(option, out var rule))
            {
                errorMessage = $"参数 `{option}` 不在允许列表中。";
                return false;
            }

            normalizedTokens.Add(rule.Name);

            var valueCount = 0;
            while (valueCount < rule.MaxValueCount &&
                   i + 1 < tokens.Length &&
                   !tokens[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                var value = tokens[i + 1];
                if (!rule.ValueValidator(value))
                {
                    errorMessage = $"参数 `{rule.Name}` 的值 `{value}` 非法。";
                    return false;
                }

                normalizedTokens.Add(value);
                i++;
                valueCount++;
            }

            if (valueCount < rule.MinValueCount)
            {
                errorMessage = $"参数 `{rule.Name}` 缺少必需的值。";
                return false;
            }
        }

        normalizedArgs = string.Join(" ", normalizedTokens);
        return true;
    }

    public static string GetAllowedArgsDescription()
    {
        return string.Join(Environment.NewLine, Rules.Select(rule => $" - {rule.Usage}"));
    }

    private static bool IsFullscreenValue(string value)
    {
        return value is "0" or "1";
    }

    private static bool IsIntInRange(string value, int min, int max)
    {
        if (!int.TryParse(value, out var parsed))
        {
            return false;
        }

        return parsed >= min && parsed <= max;
    }
}
