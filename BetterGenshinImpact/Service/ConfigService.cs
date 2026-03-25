using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service.Interface;
using OpenCvSharp;
using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace BetterGenshinImpact.Service;

public class ConfigService : IConfigService
{
    private readonly object _locker = new(); // 只有UI线程会调用这个方法，lock好像意义不大，而且浪费了下面的读写锁hhh
    private readonly ReaderWriterLockSlim _rwLock = new();
    private bool _suppressSave;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new OpenCvPointJsonConverter(),
            new OpenCvRectJsonConverter(),
        },
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// 写入只有UI线程会调用
    /// 多线程只会读，放心用static，不会丢失数据
    /// </summary>
    public static AllConfig? Config { get; private set; }

    public AllConfig Get()
    {
        lock (_locker)
        {
            if (Config == null)
            {
                Config = Read();
                Config.OnAnyChangedAction = Save; // 略微影响性能
                Config.InitEvent();
            }

            return Config;
        }
    }

    public void Save()
    {
        if (_suppressSave)
        {
            return;
        }

        if (Config != null)
        {
            Write(Config);
        }
    }

    public AllConfig Read()
    {
        _rwLock.EnterReadLock();
        try
        {
            if (!AppConfigStore.TryRead(out var config) || config == null)
            {
                return new AllConfig();
            }

            Config = config;
            return config;
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine(e.StackTrace);
            return new AllConfig();
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    public void Write(AllConfig config)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (!AppConfigStore.TryWrite(config))
            {
                Console.WriteLine("Failed to persist settings to the direct SQLite store.");
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
            Console.WriteLine(e.StackTrace);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    public bool ReloadFromStorage()
    {
        lock (_locker)
        {
            if (!AppConfigStore.TryRead(out var incoming) || incoming == null)
            {
                return false;
            }

            if (Config == null)
            {
                Config = incoming;
                Config.OnAnyChangedAction = Save;
                Config.InitEvent();
                return true;
            }

            _suppressSave = true;
            try
            {
                ApplyConfig(Config, incoming);
            }
            finally
            {
                _suppressSave = false;
            }

            return true;
        }
    }

    private static void ApplyConfig(object target, object source)
    {
        if (target == null || source == null)
        {
            return;
        }

        var type = target.GetType();
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
        {
            if (!prop.CanRead || !prop.CanWrite)
            {
                continue;
            }

            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var srcValue = prop.GetValue(source);
            var dstValue = prop.GetValue(target);
            var propType = prop.PropertyType;

            if (IsSimpleType(propType))
            {
                prop.SetValue(target, srcValue);
                continue;
            }

            if (typeof(System.Collections.IDictionary).IsAssignableFrom(propType))
            {
                if (srcValue is System.Collections.IDictionary srcDict)
                {
                    if (dstValue is System.Collections.IDictionary dstDict && !dstDict.IsReadOnly)
                    {
                        dstDict.Clear();
                        foreach (var key in srcDict.Keys)
                        {
                            dstDict[key] = srcDict[key];
                        }
                    }
                    else
                    {
                        prop.SetValue(target, srcValue);
                    }
                }
                else
                {
                    prop.SetValue(target, srcValue);
                }

                continue;
            }

            if (typeof(System.Collections.IList).IsAssignableFrom(propType))
            {
                if (srcValue is System.Collections.IList srcList)
                {
                    if (dstValue is System.Collections.IList dstList && !dstList.IsReadOnly && !dstList.IsFixedSize)
                    {
                        dstList.Clear();
                        foreach (var item in srcList)
                        {
                            dstList.Add(item);
                        }
                    }
                    else
                    {
                        prop.SetValue(target, srcValue);
                    }
                }
                else
                {
                    prop.SetValue(target, srcValue);
                }

                continue;
            }

            if (propType.IsClass)
            {
                if (srcValue == null)
                {
                    prop.SetValue(target, null);
                    continue;
                }

                if (dstValue == null)
                {
                    prop.SetValue(target, srcValue);
                    continue;
                }

                ApplyConfig(dstValue, srcValue);
            }
        }
    }

    private static bool IsSimpleType(Type type)
    {
        if (type.IsPrimitive || type.IsEnum)
        {
            return true;
        }

        if (type == typeof(string) || type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
            type == typeof(TimeSpan) || type == typeof(Guid))
        {
            return true;
        }

        var underlying = Nullable.GetUnderlyingType(type);
        return underlying != null && IsSimpleType(underlying);
    }
}

public class OpenCvRectJsonConverter : JsonConverter<Rect>
{
    public override unsafe Rect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        RectHelper helper = JsonSerializer.Deserialize<RectHelper>(ref reader, options);
        return *(Rect*)&helper;
    }

    public override unsafe void Write(Utf8JsonWriter writer, Rect value, JsonSerializerOptions options)
    {
        RectHelper helper = *(RectHelper*)&value;
        JsonSerializer.Serialize(writer, helper, options);
    }

    // DO NOT MODIFY: Keep the layout same as OpenCvSharp.Rect
    private struct RectHelper
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
    }
}

public class OpenCvPointJsonConverter : JsonConverter<Point>
{
    public override unsafe Point Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        PointHelper helper = JsonSerializer.Deserialize<PointHelper>(ref reader, options);
        return *(Point*)&helper;
    }

    public override unsafe void Write(Utf8JsonWriter writer, Point value, JsonSerializerOptions options)
    {
        PointHelper helper = *(PointHelper*)&value;
        JsonSerializer.Serialize(writer, helper, options);
    }

    // DO NOT MODIFY: Keep the layout same as OpenCvSharp.Point
    private struct PointHelper
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
}
