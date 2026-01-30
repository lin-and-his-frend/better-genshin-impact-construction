using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Service.Interface;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using OpenCvSharp.Extensions;

namespace BetterGenshinImpact.Service.Remote;

internal sealed class McpRequestHandler : IMcpRequestHandler
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IConfigService _configService;
    private readonly ILogger<McpRequestHandler> _logger;

    public McpRequestHandler(IConfigService configService, ILogger<McpRequestHandler> logger)
    {
        _configService = configService;
        _logger = logger;
    }

    public async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        var reader = new McpMessageReader(stream);
        var writer = new McpMessageWriter(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            string? json;
            try
            {
                json = await reader.ReadAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP 读取请求失败");
                return;
            }

            if (json == null)
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("method", out var methodElement))
                {
                    await writer.WriteErrorAsync(ExtractId(root), -32600, "Invalid Request", cancellationToken);
                    continue;
                }

                var method = methodElement.GetString() ?? string.Empty;
                var id = ExtractId(root);

                switch (method)
                {
                    case "initialize":
                        await writer.WriteResultAsync(id, BuildInitializeResult(), cancellationToken);
                        break;
                    case "ping":
                        await writer.WriteResultAsync(id, new { }, cancellationToken);
                        break;
                    case "tools/list":
                        await writer.WriteResultAsync(id, new { tools = BuildToolsList() }, cancellationToken);
                        break;
                    case "tools/call":
                        await HandleToolCallAsync(root, id, writer, cancellationToken);
                        break;
                    case "notifications/initialized":
                        break;
                    default:
                        if (id == null)
                        {
                            break;
                        }

                        await writer.WriteErrorAsync(id, -32601, $"Method not found: {method}", cancellationToken);
                        break;
                }
            }
            catch (JsonException)
            {
                await writer.WriteErrorAsync(null, -32700, "Parse error", cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP 处理请求失败");
                await writer.WriteErrorAsync(null, -32603, "Internal error", cancellationToken);
            }
        }
    }

    private async Task HandleToolCallAsync(JsonElement root, object? id, McpMessageWriter writer, CancellationToken ct)
    {
        if (!root.TryGetProperty("params", out var paramsElement))
        {
            await writer.WriteErrorAsync(id, -32602, "Missing params", ct);
            return;
        }

        var name = paramsElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            await writer.WriteErrorAsync(id, -32602, "Missing tool name", ct);
            return;
        }

        var argsElement = paramsElement.TryGetProperty("arguments", out var args) ? args : default;

        switch (name)
        {
            case "bgi.get_status":
                await writer.WriteResultAsync(id, ToolTextResult(JsonSerializer.Serialize(BuildStatus(), JsonOptions)), ct);
                return;
            case "bgi.get_features":
                await writer.WriteResultAsync(id, ToolTextResult(JsonSerializer.Serialize(BuildBasicFeatureState(), JsonOptions)), ct);
                return;
            case "bgi.set_features":
                var patch = ParseFeaturePatch(argsElement);
                if (patch == null)
                {
                    await writer.WriteResultAsync(id, ToolTextResult("Invalid arguments", true), ct);
                    return;
                }
                ApplyBasicFeaturePatch(patch);
                await writer.WriteResultAsync(id, ToolTextResult(JsonSerializer.Serialize(BuildBasicFeatureState(), JsonOptions)), ct);
                return;
            case "bgi.config.get":
                await writer.WriteResultAsync(id, ToolTextResult(GetConfigValue(argsElement)), ct);
                return;
            case "bgi.config.set":
                var setResult = SetConfigValue(argsElement);
                await writer.WriteResultAsync(id, ToolTextResult(setResult.text, setResult.isError), ct);
                return;
            case "bgi.get_logs":
                await writer.WriteResultAsync(id, ToolTextResult(GetLogs(argsElement)), ct);
                return;
            case "bgi.capture_screen":
                await writer.WriteResultAsync(id, await CaptureScreenResultAsync(ct), ct);
                return;
            case "bgi.script.groups":
                await writer.WriteResultAsync(id, ToolTextResult(GetScriptGroups()), ct);
                return;
            case "bgi.script.run":
                var runResult = await RunScriptGroupsAsync(argsElement);
                await writer.WriteResultAsync(id, ToolTextResult(runResult.text, runResult.isError), ct);
                return;
            case "bgi.task.cancel":
                CancellationContext.Instance.ManualCancel();
                await writer.WriteResultAsync(id, ToolTextResult("{\"ok\":true}"), ct);
                return;
            case "bgi.task.pause":
                RunnerContext.Instance.IsSuspend = true;
                await writer.WriteResultAsync(id, ToolTextResult("{\"ok\":true}"), ct);
                return;
            case "bgi.task.resume":
                RunnerContext.Instance.IsSuspend = false;
                await writer.WriteResultAsync(id, ToolTextResult("{\"ok\":true}"), ct);
                return;
            case "bgi.action.start_dispatcher":
                await StartDispatcherAsync();
                await writer.WriteResultAsync(id, ToolTextResult("{\"ok\":true}"), ct);
                return;
            case "bgi.action.stop_dispatcher":
                StopDispatcher();
                await writer.WriteResultAsync(id, ToolTextResult("{\"ok\":true}"), ct);
                return;
            case "bgi.action.start_game":
                await StartGameAsync();
                await writer.WriteResultAsync(id, ToolTextResult("{\"ok\":true}"), ct);
                return;
            case "bgi.task.status":
                await writer.WriteResultAsync(id, ToolTextResult(GetTaskStatus()), ct);
                return;
            default:
                await writer.WriteResultAsync(id, ToolTextResult($"Unknown tool: {name}", true), ct);
                return;
        }
    }

    private static object ToolTextResult(string text, bool isError = false)
    {
        return new
        {
            content = new[]
            {
                new { type = "text", text }
            },
            isError
        };
    }

    private async Task<object> CaptureScreenResultAsync(CancellationToken ct)
    {
        var config = _configService.Get().WebRemoteConfig;
        if (!config.ScreenStreamEnabled)
        {
            return ToolTextResult("Screen stream disabled", true);
        }

        var bytes = await Task.Run(CaptureScreenPng, ct);
        if (bytes == null || bytes.Length == 0)
        {
            return ToolTextResult("Screen capture unavailable", true);
        }

        var base64 = Convert.ToBase64String(bytes);
        return new
        {
            content = new[]
            {
                new
                {
                    type = "image",
                    data = base64,
                    mimeType = "image/png"
                }
            }
        };
    }

    private static byte[]? CaptureScreenPng()
    {
        if (!TaskContext.Instance().IsInitialized)
        {
            return null;
        }

        Mat? mat = null;
        try
        {
            mat = TaskControl.CaptureGameImageNoRetry(TaskTriggerDispatcher.GlobalGameCapture);
            if (mat == null || mat.Empty())
            {
                return null;
            }

            using var bitmap = mat.ToBitmap();
            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            mat?.Dispose();
        }
    }

    private string GetLogs(JsonElement argsElement)
    {
        var config = _configService.Get().WebRemoteConfig;
        if (!config.LogStreamEnabled)
        {
            return "Log stream disabled";
        }

        var maxLines = 100;
        if (argsElement.ValueKind == JsonValueKind.Object &&
            argsElement.TryGetProperty("maxLines", out var maxLinesElement) &&
            maxLinesElement.ValueKind == JsonValueKind.Number &&
            maxLinesElement.TryGetInt32(out var parsed))
        {
            maxLines = Math.Clamp(parsed, 1, 500);
        }

        var lines = LogRelayHub.GetSnapshot(maxLines);
        return JsonSerializer.Serialize(lines, JsonOptions);
    }

    private string GetConfigValue(JsonElement argsElement)
    {
        var path = argsElement.ValueKind == JsonValueKind.Object && argsElement.TryGetProperty("path", out var pathElement)
            ? pathElement.GetString()
            : null;

        var config = _configService.Get();
        if (!ConfigPathAccessor.TryGetValue(config, path, out var value, out var error))
        {
            return JsonSerializer.Serialize(new { error }, JsonOptions);
        }

        return JsonSerializer.Serialize(value ?? new { }, JsonOptions);
    }

    private static string GetScriptGroups()
    {
        var groups = LoadScriptGroupInfos();
        return JsonSerializer.Serialize(groups, JsonOptions);
    }

    private async Task<(string text, bool isError)> RunScriptGroupsAsync(JsonElement argsElement)
    {
        var names = new List<string>();
        if (argsElement.ValueKind == JsonValueKind.Object)
        {
            if (argsElement.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                var name = nameElement.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            if (argsElement.TryGetProperty("names", out var namesElement) && namesElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in namesElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var name = item.GetString();
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            names.Add(name);
                        }
                    }
                }
            }
        }

        if (names.Count == 0)
        {
            return ("Missing script group names", true);
        }

        await RunOnUiThreadAsync(async () =>
        {
            var vm = App.GetService<BetterGenshinImpact.ViewModel.Pages.ScriptControlViewModel>();
            if (vm != null)
            {
                await vm.OnStartMultiScriptGroupWithNamesAsync(names.ToArray());
            }
        });

        return ("{\"ok\":true}", false);
    }

    private (string text, bool isError) SetConfigValue(JsonElement argsElement)
    {
        if (argsElement.ValueKind != JsonValueKind.Object)
        {
            return ("Invalid arguments", true);
        }

        if (!argsElement.TryGetProperty("path", out var pathElement))
        {
            return ("Missing path", true);
        }

        var path = pathElement.GetString();
        if (string.IsNullOrWhiteSpace(path))
        {
            return ("Missing path", true);
        }

        if (!argsElement.TryGetProperty("value", out var valueElement))
        {
            return ("Missing value", true);
        }

        var success = false;
        string? error = null;
        RunOnUiThread(() =>
        {
            var config = _configService.Get();
            success = ConfigPathAccessor.TrySetValue(config, path, valueElement, out error);
        });

        if (!success)
        {
            return (error ?? "Set failed", true);
        }

        return ("{\"ok\":true}", false);
    }

    private Task StartDispatcherAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(async () =>
        {
            try
            {
                var homeVm = App.GetService<BetterGenshinImpact.ViewModel.Pages.HomePageViewModel>();
                if (homeVm != null)
                {
                    await homeVm.OnStartTriggerAsync();
                }
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private Task StartGameAsync()
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(async () =>
        {
            try
            {
                await BetterGenshinImpact.Service.ScriptService.StartGameTask(false);
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private void StopDispatcher()
    {
        RunOnUiThread(() =>
        {
            var homeVm = App.GetService<BetterGenshinImpact.ViewModel.Pages.HomePageViewModel>();
            homeVm?.StopTriggerCommand.Execute(null);
        });
    }

    private static string GetTaskStatus()
    {
        var running = TaskControl.TaskSemaphore.CurrentCount == 0;
        var payload = new
        {
            isRunning = running,
            isSuspended = RunnerContext.Instance.IsSuspend,
            taskProgress = RunnerContext.Instance.taskProgress
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private static object[] BuildToolsList()
    {
        return
        [
            new
            {
                name = "bgi.get_status",
                description = "获取 BetterGI 当前运行状态",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.get_features",
                description = "获取基础任务开关状态",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.set_features",
                description = "设置基础任务开关状态",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        autoPick = new { type = "boolean" },
                        autoSkip = new { type = "boolean" },
                        autoFishing = new { type = "boolean" },
                        autoCook = new { type = "boolean" },
                        autoEat = new { type = "boolean" },
                        quickTeleport = new { type = "boolean" },
                        mapMask = new { type = "boolean" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.config.get",
                description = "读取配置（路径为空时返回完整配置）",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.config.set",
                description = "设置配置（path + value）",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        path = new { type = "string" },
                        value = new { }
                    },
                    required = new[] { "path", "value" },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.get_logs",
                description = "获取遮罩日志内容",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        maxLines = new { type = "integer", minimum = 1, maximum = 500 }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.script.groups",
                description = "列出脚本配置组",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.script.run",
                description = "执行脚本配置组（按名称）",
                inputSchema = new
                {
                    type = "object",
                    properties = new
                    {
                        name = new { type = "string" },
                        names = new { type = "array", items = new { type = "string" } }
                    },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.task.cancel",
                description = "取消当前任务",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.task.pause",
                description = "暂停当前任务",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.task.resume",
                description = "继续当前任务",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.task.status",
                description = "获取任务运行状态",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.action.start_dispatcher",
                description = "启动截图器与遮罩（等同于点击开始）",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.action.stop_dispatcher",
                description = "停止截图器与遮罩",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.action.start_game",
                description = "启动原神并开启截图器（如果已配置）",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            },
            new
            {
                name = "bgi.capture_screen",
                description = "获取当前画面截图（PNG）",
                inputSchema = new
                {
                    type = "object",
                    properties = new { },
                    additionalProperties = false
                }
            }
        ];
    }

    private static object BuildInitializeResult()
    {
        return new
        {
            protocolVersion = "2024-11-05",
            serverInfo = new
            {
                name = "BetterGI",
                version = Global.Version
            },
            capabilities = new
            {
                tools = new { listChanged = false },
                resources = new { },
                prompts = new { }
            }
        };
    }

    private object BuildStatus()
    {
        return new
        {
            version = Global.Version,
            serverTime = DateTimeOffset.Now,
            isInitialized = TaskContext.Instance().IsInitialized,
            isSuspended = RunnerContext.Instance.IsSuspend
        };
    }

    private BasicFeatureState BuildBasicFeatureState()
    {
        var config = _configService.Get();
        return new BasicFeatureState
        {
            AutoPick = config.AutoPickConfig.Enabled,
            AutoSkip = config.AutoSkipConfig.Enabled,
            AutoFishing = config.AutoFishingConfig.Enabled,
            AutoCook = config.AutoCookConfig.Enabled,
            AutoEat = config.AutoEatConfig.Enabled,
            QuickTeleport = config.QuickTeleportConfig.Enabled,
            MapMask = config.MapMaskConfig.Enabled
        };
    }

    private void ApplyBasicFeaturePatch(BasicFeaturePatch patch)
    {
        RunOnUiThread(() =>
        {
            var config = _configService.Get();

            if (patch.AutoPick.HasValue)
            {
                config.AutoPickConfig.Enabled = patch.AutoPick.Value;
            }

            if (patch.AutoSkip.HasValue)
            {
                config.AutoSkipConfig.Enabled = patch.AutoSkip.Value;
            }

            if (patch.AutoFishing.HasValue)
            {
                config.AutoFishingConfig.Enabled = patch.AutoFishing.Value;
            }

            if (patch.AutoCook.HasValue)
            {
                config.AutoCookConfig.Enabled = patch.AutoCook.Value;
            }

            if (patch.AutoEat.HasValue)
            {
                config.AutoEatConfig.Enabled = patch.AutoEat.Value;
            }

            if (patch.QuickTeleport.HasValue)
            {
                config.QuickTeleportConfig.Enabled = patch.QuickTeleport.Value;
            }

            if (patch.MapMask.HasValue)
            {
                config.MapMaskConfig.Enabled = patch.MapMask.Value;
            }
        });
    }

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Invoke(action);
        }
    }

    private static Task RunOnUiThreadAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnUiThread(async () =>
        {
            try
            {
                await action();
                tcs.TrySetResult(true);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        return tcs.Task;
    }

    private static BasicFeaturePatch? ParseFeaturePatch(JsonElement argsElement)
    {
        if (argsElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return JsonSerializer.Deserialize<BasicFeaturePatch>(argsElement.GetRawText(), JsonOptions);
    }

    private static object? ExtractId(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var idElement))
        {
            return null;
        }

        return idElement.ValueKind switch
        {
            JsonValueKind.String => idElement.GetString(),
            JsonValueKind.Number => idElement.TryGetInt64(out var v) ? v : idElement.GetDouble(),
            JsonValueKind.Null => null,
            _ => null
        };
    }

    private sealed class BasicFeatureState
    {
        public bool AutoPick { get; set; }
        public bool AutoSkip { get; set; }
        public bool AutoFishing { get; set; }
        public bool AutoCook { get; set; }
        public bool AutoEat { get; set; }
        public bool QuickTeleport { get; set; }
        public bool MapMask { get; set; }
    }

    private sealed class BasicFeaturePatch
    {
        public bool? AutoPick { get; set; }
        public bool? AutoSkip { get; set; }
        public bool? AutoFishing { get; set; }
        public bool? AutoCook { get; set; }
        public bool? AutoEat { get; set; }
        public bool? QuickTeleport { get; set; }
        public bool? MapMask { get; set; }
    }

    private sealed class ScriptGroupInfo
    {
        public string Name { get; set; } = string.Empty;
        public int Index { get; set; }
        public int ProjectCount { get; set; }
    }

    private static IReadOnlyList<ScriptGroupInfo> LoadScriptGroupInfos()
    {
        try
        {
            var folder = Global.Absolute(@"User\ScriptGroup");
            if (!Directory.Exists(folder))
            {
                return Array.Empty<ScriptGroupInfo>();
            }

            var files = Directory.GetFiles(folder, "*.json");
            var list = new List<ScriptGroupInfo>();
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var group = ScriptGroup.FromJson(json);
                    list.Add(new ScriptGroupInfo
                    {
                        Name = group.Name,
                        Index = group.Index,
                        ProjectCount = group.Projects?.Count ?? 0
                    });
                }
                catch
                {
                }
            }

            return list.OrderBy(g => g.Index).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch
        {
            return Array.Empty<ScriptGroupInfo>();
        }
    }
}

internal sealed class McpMessageReader
{
    private readonly Stream _stream;

    public McpMessageReader(Stream stream)
    {
        _stream = stream;
    }

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        var headerBytes = new MemoryStream();
        var buffer = new byte[1];
        while (true)
        {
            var read = await _stream.ReadAsync(buffer, 0, 1, ct);
            if (read == 0)
            {
                return null;
            }

            headerBytes.WriteByte(buffer[0]);
            if (headerBytes.Length < 4)
            {
                continue;
            }

            var data = headerBytes.GetBuffer();
            var len = headerBytes.Length;
            if (data[len - 4] == '\r' && data[len - 3] == '\n' && data[len - 2] == '\r' && data[len - 1] == '\n')
            {
                break;
            }
        }

        var headerText = Encoding.ASCII.GetString(headerBytes.GetBuffer(), 0, (int)headerBytes.Length - 4);
        var contentLength = ParseContentLength(headerText);
        if (contentLength <= 0)
        {
            return null;
        }

        var payload = new byte[contentLength];
        var offset = 0;
        while (offset < contentLength)
        {
            var read = await _stream.ReadAsync(payload, offset, contentLength - offset, ct);
            if (read == 0)
            {
                return null;
            }

            offset += read;
        }

        return Encoding.UTF8.GetString(payload, 0, payload.Length);
    }

    private static int ParseContentLength(string headerText)
    {
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            if (!parts[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (int.TryParse(parts[1].Trim(), out var len))
            {
                return len;
            }
        }

        return -1;
    }
}

internal sealed class McpMessageWriter
{
    private readonly Stream _stream;

    public McpMessageWriter(Stream stream)
    {
        _stream = stream;
    }

    public Task WriteResultAsync(object? id, object result, CancellationToken ct)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id,
            result
        };

        return WritePayloadAsync(payload, ct);
    }

    public Task WriteErrorAsync(object? id, int code, string message, CancellationToken ct)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id,
            error = new
            {
                code,
                message
            }
        };

        return WritePayloadAsync(payload, ct);
    }

    private async Task WritePayloadAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, McpRequestHandler.JsonOptions);
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await _stream.WriteAsync(header, 0, header.Length, ct);
        await _stream.WriteAsync(body, 0, body.Length, ct);
        await _stream.FlushAsync(ct);
    }
}
