using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using BetterGenshinImpact.Service;

namespace BetterGenshinImpact.Service.Remote;

internal static class ConfigPathAccessor
{
    private sealed record PathToken(string Name, int? Index);

    public static bool TryGetValue(object root, string? path, out object? value, out string? error)
    {
        error = null;
        value = root;

        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        foreach (var token in Parse(path))
        {
            if (value == null)
            {
                error = $"Null encountered at '{token.Name}'";
                return false;
            }

            var prop = FindProperty(value.GetType(), token.Name);
            if (prop == null)
            {
                error = $"Property '{token.Name}' not found on {value.GetType().Name}";
                return false;
            }

            value = prop.GetValue(value);
            if (token.Index.HasValue)
            {
                if (value is not IList list)
                {
                    error = $"Property '{token.Name}' is not a list";
                    return false;
                }

                var index = token.Index.Value;
                if (index < 0 || index >= list.Count)
                {
                    error = $"Index out of range: {index}";
                    return false;
                }

                value = list[index];
            }
        }

        return true;
    }

    public static bool TrySetValue(object root, string? path, JsonElement valueElement, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "Path is required";
            return false;
        }

        var tokens = Parse(path).ToArray();
        if (tokens.Length == 0)
        {
            error = "Invalid path";
            return false;
        }

        object? current = root;
        for (var i = 0; i < tokens.Length - 1; i++)
        {
            var token = tokens[i];
            if (current == null)
            {
                error = $"Null encountered at '{token.Name}'";
                return false;
            }

            var prop = FindProperty(current.GetType(), token.Name);
            if (prop == null)
            {
                error = $"Property '{token.Name}' not found on {current.GetType().Name}";
                return false;
            }

            current = prop.GetValue(current);
            if (token.Index.HasValue)
            {
                if (current is not IList list)
                {
                    error = $"Property '{token.Name}' is not a list";
                    return false;
                }

                var index = token.Index.Value;
                if (index < 0 || index >= list.Count)
                {
                    error = $"Index out of range: {index}";
                    return false;
                }

                current = list[index];
            }
        }

        if (current == null)
        {
            error = "Target is null";
            return false;
        }

        var last = tokens[^1];
        var lastProp = FindProperty(current.GetType(), last.Name);
        if (lastProp == null)
        {
            error = $"Property '{last.Name}' not found on {current.GetType().Name}";
            return false;
        }

        if (last.Index.HasValue)
        {
            var listValue = lastProp.GetValue(current);
            if (listValue is not IList list)
            {
                error = $"Property '{last.Name}' is not a list";
                return false;
            }

            var index = last.Index.Value;
            if (index < 0 || index >= list.Count)
            {
                error = $"Index out of range: {index}";
                return false;
            }

            var itemType = list.GetType().IsArray
                ? list.GetType().GetElementType() ?? typeof(object)
                : list.GetType().GetGenericArguments().FirstOrDefault() ?? typeof(object);

            if (!TryConvertValue(valueElement, itemType, out var converted, out error))
            {
                return false;
            }

            list[index] = converted;
            return true;
        }

        if (!TryConvertValue(valueElement, lastProp.PropertyType, out var value, out error))
        {
            return false;
        }

        lastProp.SetValue(current, value);
        return true;
    }

    private static PropertyInfo? FindProperty(Type type, string name)
    {
        return type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
    }

    private static IEnumerable<PathToken> Parse(string path)
    {
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            var name = segment;
            int? index = null;
            var bracketStart = segment.IndexOf('[');
            if (bracketStart >= 0 && segment.EndsWith("]", StringComparison.Ordinal))
            {
                name = segment.Substring(0, bracketStart);
                var indexText = segment.Substring(bracketStart + 1, segment.Length - bracketStart - 2);
                if (int.TryParse(indexText, out var parsed))
                {
                    index = parsed;
                }
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                yield return new PathToken(name, index);
            }
        }
    }

    private static bool TryConvertValue(JsonElement element, Type targetType, out object? value, out string? error)
    {
        error = null;
        value = null;

        if (element.ValueKind == JsonValueKind.Null)
        {
            if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
            {
                error = $"Cannot assign null to {targetType.Name}";
                return false;
            }

            value = null;
            return true;
        }

        try
        {
            var raw = element.GetRawText();
            value = JsonSerializer.Deserialize(raw, targetType, ConfigService.JsonOptions);
            return true;
        }
        catch
        {
            // Fallback conversions for simple types
        }

        try
        {
            if (targetType.IsEnum)
            {
                if (element.ValueKind == JsonValueKind.String)
                {
                    value = Enum.Parse(targetType, element.GetString() ?? string.Empty, true);
                    return true;
                }

                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var enumValue))
                {
                    value = Enum.ToObject(targetType, enumValue);
                    return true;
                }
            }

            if (element.ValueKind == JsonValueKind.String)
            {
                var str = element.GetString();
                value = Convert.ChangeType(str, targetType);
                return true;
            }

            value = JsonSerializer.Deserialize(element.GetRawText(), targetType);
            return true;
        }
        catch (Exception ex)
        {
            error = $"Convert failed: {ex.Message}";
            return false;
        }
    }
}
