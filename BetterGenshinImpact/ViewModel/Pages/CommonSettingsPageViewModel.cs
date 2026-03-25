using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Windows.System;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.LogParse;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Helpers.Http;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.View.Controls.Webview;
using BetterGenshinImpact.View.Converters;
using BetterGenshinImpact.View.Pages;
using BetterGenshinImpact.View.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Win32;
using Newtonsoft.Json;
using Wpf.Ui;

namespace BetterGenshinImpact.ViewModel.Pages;

public partial class CommonSettingsPageViewModel : ViewModel
{
    private readonly INavigationService _navigationService;
    private readonly IConfigService _configService;

    private readonly NotificationService _notificationService;
    private readonly TpConfig _tpConfig = TaskContext.Instance().Config.TpConfig;
    private bool _isMcpRiskPromptBusy;

    private string _selectedArea = string.Empty;


    private string _selectedCountry = string.Empty;
    [ObservableProperty] private List<string> _adventurersGuildCountry = ["无", "枫丹", "稻妻", "璃月", "蒙德"];
    
    [ObservableProperty] private List<Tuple<TimeSpan, string>> _serverTimeZones =
    [
        Tuple.Create(TimeSpan.FromHours(8), "其他 UTC+08"),
        Tuple.Create(TimeSpan.FromHours(1), "欧服 UTC+01"),
        Tuple.Create(TimeSpan.FromHours(-5), "美服 UTC-05")
    ];

    public CommonSettingsPageViewModel(IConfigService configService, INavigationService navigationService,
        NotificationService notificationService)
    {
        _configService = configService;
        Config = configService.Get();
        _navigationService = navigationService;
        _notificationService = notificationService;
        Config.McpConfig.PropertyChanged += OnMcpConfigPropertyChanged;
        Config.ScriptConfig.PropertyChanged += OnScriptConfigPropertyChanged;
        Config.WebRemoteConfig.PropertyChanged += OnWebRemoteConfigPropertyChanged;
        Config.AiConfig.PropertyChanged += OnAiConfigPropertyChanged;
        InitializeCountries();
        InitializeMiyousheCookie();
        // 初始化OCR模型选择
        SelectedPaddleOcrModelConfig = Config.OtherConfig.OcrConfig.PaddleOcrModelConfig;
        _ = EnsureNonLoopbackRiskConsentAsync();
    }

    public AllConfig Config { get; set; }
    public ObservableCollection<string> CountryList { get; } = new();
    public ObservableCollection<string> Areas { get; } = new();

    public ObservableCollection<string> MapPathingTypes { get; } = ["SIFT", "TemplateMatch"];

    private async Task EnsureNonLoopbackRiskConsentAsync()
    {
        if (_isMcpRiskPromptBusy ||
            !Config.McpConfig.AllowNonLoopbackConnections ||
            Config.McpConfig.NonLoopbackRiskAccepted ||
            IsLoopbackAddress(Config.McpConfig.ListenAddress))
        {
            return;
        }

        _isMcpRiskPromptBusy = true;
        try
        {
            var result = await ThemedMessageBox.ShowAsync(
                "你正在允许 MCP 监听非本地地址（如 0.0.0.0/局域网地址）。\n\n这会允许网络中可达主机直接调用本地 MCP 工具，可能导致配置篡改、脚本导入或任务控制等风险。\n\n是否确认承担该风险并继续？",
                "安全警告",
                MessageBoxButton.YesNo,
                ThemedMessageBox.MessageBoxIcon.Warning);
            if (result == MessageBoxResult.Yes)
            {
                Config.McpConfig.NonLoopbackRiskAccepted = true;
            }
            else
            {
                Config.McpConfig.AllowNonLoopbackConnections = false;
            }
        }
        finally
        {
            _isMcpRiskPromptBusy = false;
        }
    }

    private void OnMcpConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(McpConfig.WebSearchEnabled))
        {
            _configService.Save();
            return;
        }

        if (e.PropertyName == nameof(McpConfig.Enabled) && Config.McpConfig.Enabled)
        {
            ThemedMessageBox.Warning("你已开启“启用 MCP 外部调用”。\n这会开放 MCP 端口给外部客户端，请仅在可信网络环境下启用。");
            return;
        }

        if (e.PropertyName == nameof(McpConfig.AllowNonLoopbackConnections) && Config.McpConfig.AllowNonLoopbackConnections)
        {
            _ = EnsureNonLoopbackRiskConsentAsync();
            return;
        }

        if (e.PropertyName == nameof(McpConfig.ListenAddress))
        {
            _ = EnsureNonLoopbackRiskConsentAsync();
            return;
        }

        if (e.PropertyName == nameof(McpConfig.AllowConfigSet) && Config.McpConfig.AllowConfigSet)
        {
            ThemedMessageBox.Warning("你已开启“允许 MCP 修改配置”。\n该能力可能修改关键配置项，请仅在可信调用端与可信网络环境下启用。");
            return;
        }

        if (e.PropertyName == nameof(McpConfig.AllowStartGameAction) && Config.McpConfig.AllowStartGameAction)
        {
            ThemedMessageBox.Warning("你已开启“允许 MCP 启动游戏动作”。\n远程调用可触发本机启动行为，请确认调用端可信。");
        }
    }

    private static bool IsLoopbackAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return true;
        }

        if (!System.Net.IPAddress.TryParse(address, out var ip))
        {
            return true;
        }

        if (System.Net.IPAddress.IsLoopback(ip))
        {
            return true;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            return System.Net.IPAddress.IsLoopback(ip.MapToIPv4());
        }

        return false;
    }

    private void OnScriptConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ScriptConfig.EnableCommandLineStart) && Config.ScriptConfig.EnableCommandLineStart)
        {
            ThemedMessageBox.Warning("你已开启“允许命令行参数启动任务”。\n外部进程可通过启动参数触发自动执行，存在高风险，请仅在可信环境启用。");
            return;
        }

        if (e.PropertyName == nameof(ScriptConfig.AllowMcpImportNow) && Config.ScriptConfig.AllowMcpImportNow)
        {
            ThemedMessageBox.Warning("你已开启“允许 MCP 立即导入脚本”。\n这会允许远程调用触发本地脚本目录写入，请仅在可信环境启用。");
            return;
        }

        if (e.PropertyName == nameof(ScriptConfig.EnableShellTask) && Config.ScriptConfig.EnableShellTask)
        {
            ThemedMessageBox.Warning("你已开启“全局 Shell 任务”。\n脚本可执行 cmd 命令，存在高风险，请谨慎使用。");
        }
    }

    private void OnWebRemoteConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WebRemoteConfig.Enabled) or nameof(WebRemoteConfig.Username) or nameof(WebRemoteConfig.Password))
        {
            if (Config.WebRemoteConfig.Enabled && !IsWebRemoteAuthConfigured())
            {
                ThemedMessageBox.Warning(
                    "Web 远程控制已改为强制鉴权。\n请先设置鉴权账号和密码后，再启用 Web 远程控制。");
                Config.WebRemoteConfig.Enabled = false;
                return;
            }

            if (e.PropertyName == nameof(WebRemoteConfig.Enabled) && Config.WebRemoteConfig.Enabled)
            {
                ThemedMessageBox.Warning(
                    "你已开启“启用 Web 外部调用”。\n这会开放 Web 远程端口，请确保仅在可信网络启用并妥善保管鉴权账号密码。");
                return;
            }
        }

        if (e.PropertyName == nameof(WebRemoteConfig.AllowAdvancedConfigApi) &&
            Config.WebRemoteConfig.AllowAdvancedConfigApi)
        {
            ThemedMessageBox.Warning(
                "你已开启“Web 高级配置接口”。\n这会开放 /api/config/get 与 /api/config/set，可能被用于远程读取/修改配置。\n请仅在可信网络与可信调用端启用。");
            return;
        }

        if (e.PropertyName == nameof(WebRemoteConfig.ClusterApiEnabled) &&
            Config.WebRemoteConfig.ClusterApiEnabled)
        {
            ThemedMessageBox.Warning(
                "你已开启“集群群控 API”。\n请务必配置强随机 Token 与白名单 IP，避免被未授权主机调用。");
        }
    }

    private bool IsWebRemoteAuthConfigured()
    {
        return !string.IsNullOrWhiteSpace(Config.WebRemoteConfig.Username) &&
               !string.IsNullOrWhiteSpace(Config.WebRemoteConfig.Password);
    }

    private void OnAiConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AiConfig.AutoExecuteMcpToolCalls) &&
            Config.AiConfig.AutoExecuteMcpToolCalls)
        {
            ThemedMessageBox.Warning(
                "你已开启“自动执行 MCP 调用”。\nAI 可直接触发本地控制操作（配置修改、任务控制、脚本订阅等），请仅在可信输入场景启用。");
        }
    }

    [ObservableProperty] private FrozenDictionary<string, string> _languageDict =
        new string[] { "zh-Hans", "zh-Hant", "en"}
            .ToFrozenDictionary(
                c => c,
                c =>
                {
                    CultureInfo.CurrentUICulture = new CultureInfo(c);
                    var stringLocalizer = App.GetService<IStringLocalizer<CultureInfoNameToKVPConverter>>() ??
                                          throw new NullReferenceException();
                    return stringLocalizer["简体中文"].ToString();
                }
            );

    [RelayCommand]
    private async Task OnUpdateUiLanguageAsync()
    {
        var cultureName = Config.OtherConfig.UiCultureInfoName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            throw new InvalidOperationException("当前UI语言为空，无法更新语言文件。");
        }

        if (cultureName == "zh-Hans")
        {
            await ThemedMessageBox.InformationAsync("zh-Hans 无语言文件，无需更新。");
            return;
        }

        var urls = new[]
        {
            $"https://raw.githubusercontent.com/babalae/bettergi-i18n/refs/heads/main/i18n/{cultureName}.json",
            $"https://cnb.cool/bettergi/bettergi-i18n/-/git/raw/main/i18n/{cultureName}.json"
        };

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        byte[]? bytes = null;
        Exception? lastError = null;
        var allNotFound = true;
        foreach (var url in urls)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("BetterGenshinImpact");
                using var response = await httpClient.SendAsync(request);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    lastError = new HttpRequestException("Language file not found.", null, response.StatusCode);
                    continue;
                }

                allNotFound = false;
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync();

                var json = Encoding.UTF8.GetString(bytes);
                _ = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                    ?? throw new JsonException("翻译文件不是有效的 JSON 字典。");
                break;
            }
            catch (Exception e)
            {
                lastError = e;
                allNotFound = false;
            }
        }

        if (bytes == null)
        {
            if (allNotFound)
            {
                await ThemedMessageBox.WarningAsync($"语言文件不存在：{cultureName}.json");
                return;
            }

            throw new Exception($"下载语言文件失败：{cultureName}.json", lastError);
        }

        var dir = UserPathProvider.I18nRoot;
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{cultureName}.json");
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllBytesAsync(tmp, bytes);

        if (File.Exists(path))
        {
            File.Replace(tmp, path, null);
        }
        else
        {
            File.Move(tmp, path);
        }

        var translator = App.GetService<ITranslationService>() ?? throw new NullReferenceException();
        translator.Reload();
    }

    public string SelectedCountry
    {
        get => _selectedCountry;
        set
        {
            if (SetProperty(ref _selectedCountry, value))
            {
                UpdateAreas(value);
                SelectedArea = Areas.FirstOrDefault() ?? string.Empty;
            }
        }
    }

    public string SelectedArea
    {
        get => _selectedArea;
        set
        {
            if (SetProperty(ref _selectedArea, value))
            {
                UpdateRevivePoint(SelectedCountry, SelectedArea);
            }
        }
    }

    public ObservableCollection<PaddleOcrModelConfig> PaddleOcrModelConfigs { get; } =
        new(Enum.GetValues(typeof(PaddleOcrModelConfig)).Cast<PaddleOcrModelConfig>());

    [ObservableProperty] private PaddleOcrModelConfig _selectedPaddleOcrModelConfig;

    [RelayCommand]
    public void OnQuestionButtonOnClick()
    {
        //            Owner = this,
        WebpageWindow cookieWin = new()
        {
            Title = "日志分析",
            Width = 800,
            Height = 600,

            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        cookieWin.NavigateToHtml(TravelsDiaryDetailManager.generHtmlMessage());
        cookieWin.Show();
    }

    private void InitializeMiyousheCookie()
    {
        OtherConfig.Miyoushe mcfg = TaskContext.Instance().Config.OtherConfig.MiyousheConfig;
        if (mcfg.Cookie == string.Empty &&
            mcfg.LogSyncCookie)
        {
            var config = LogParse.LoadConfig();
            mcfg.Cookie = config.Cookie;
        }
    }

    private void InitializeCountries()
    {
        var countries = MapLazyAssets.Instance.GoddessPositions.Values
            .OrderBy(g => int.TryParse(g.Id, out var id) ? id : int.MaxValue)
            .GroupBy(g => g.Country)
            .Select(grp => grp.Key);
        CountryList.Clear();
        foreach (var country in countries)
        {
            if (!string.IsNullOrEmpty(country))
            {
                CountryList.Add(country);
            }
        }

        _selectedCountry = _tpConfig.ReviveStatueOfTheSevenCountry;
        UpdateAreas(SelectedCountry);
        _selectedArea = _tpConfig.ReviveStatueOfTheSevenArea;
        UpdateRevivePoint(SelectedCountry, SelectedArea);
    }

    private void UpdateAreas(string country)
    {
        Areas.Clear();
        SelectedArea = string.Empty;
        if (string.IsNullOrEmpty(country)) return;

        var areas = MapLazyAssets.Instance.GoddessPositions.Values
            .Where(g => g.Country == country)
            .OrderBy(g => int.TryParse(g.Id, out var id) ? id : int.MaxValue)
            .GroupBy(g => g.Level1Area)
            .Select(grp => grp.Key);
        foreach (var area in areas)
        {
            if (!string.IsNullOrEmpty(area))
            {
                Areas.Add(area);
            }
        }
    }

    // 当国家或区域改变时更新坐标
    private void UpdateRevivePoint(string country, string area)
    {
        if (string.IsNullOrEmpty(country) || string.IsNullOrEmpty(area)) return;

        var goddess = MapLazyAssets.Instance.GoddessPositions.Values
            .FirstOrDefault(g => g.Country == country && g.Level1Area == area);
        if (goddess == null) return;
        _tpConfig.ReviveStatueOfTheSevenCountry = country;
        _tpConfig.ReviveStatueOfTheSevenArea = area;
        _tpConfig.ReviveStatueOfTheSevenPointX = goddess.X;
        _tpConfig.ReviveStatueOfTheSevenPointY = goddess.Y;
        _tpConfig.ReviveStatueOfTheSeven = goddess;
    }

    [RelayCommand]
    public void OnRefreshMaskSettings()
    {
        WeakReferenceMessenger.Default.Send(
            new PropertyChangedMessage<object>(this, "RefreshSettings", new object(), "重新计算控件位置"));
    }

    [RelayCommand]
    private void OnResetMaskOverlayLayout()
    {
        var c = Config.MaskWindowConfig;
        c.StatusListLeftRatio = 20.0 / 1920;
        c.StatusListTopRatio = 807.0 / 1080;
        c.StatusListWidthRatio = 477.0 / 1920;
        c.StatusListHeightRatio = 24.0 / 1080;

        c.LogTextBoxLeftRatio = 20.0 / 1920;
        c.LogTextBoxTopRatio = 832.0 / 1080;
        c.LogTextBoxWidthRatio = 477.0 / 1920;
        c.LogTextBoxHeightRatio = 188.0 / 1080;

        OnRefreshMaskSettings();
    }

    [RelayCommand]
    private void OnSwitchMaskEnabled()
    {
        // if (Config.MaskWindowConfig.MaskEnabled)
        // {
        //     MaskWindow.Instance().Show();
        // }
        // else
        // {
        //     MaskWindow.Instance().Hide();
        // }
    }

    [RelayCommand]
    public void OnGoToHotKeyPage()
    {
        _navigationService.Navigate(typeof(HotKeyPage));
    }

    [RelayCommand]
    private void GenerateClusterApiToken()
    {
        Config.WebRemoteConfig.ClusterApiToken = WebRemoteConfig.CreateRandomToken();
        ThemedMessageBox.Information("已生成新的集群 API Token。");
    }

    [RelayCommand]
    public void OnSwitchTakenScreenshotEnabled()
    {
    }

    [RelayCommand]
    public void OnGoToFolder()
    {
        var path = UserPathProvider.ScreenshotLogRoot;
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        Process.Start("explorer.exe", path);
    }

    [RelayCommand]
    public void OnGoToLogFolder()
    {
        var path = UserPathProvider.LogRoot;
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        Process.Start("explorer.exe", path);
    }

    [RelayCommand]
    public void OnOpenConfigFolder()
    {
        var path = Global.UserDataRoot;
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        Process.Start("explorer.exe", path);
    }

    [RelayCommand]
    private async Task OnBackupConfig()
    {
        try
        {
            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "ZIP 压缩包 (*.zip)|*.zip",
                FileName = $"BetterGI_Config_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
                Title = "选择备份文件保存位置"
            };

            if (saveDialog.ShowDialog() != true)
            {
                return;
            }

            var backupPath = saveDialog.FileName;
            var includeScripts = Config.CommonConfig.BackupIncludeScripts;

            await Task.Run(() =>
            {
                using var zipArchive = ZipFile.Open(backupPath, ZipArchiveMode.Create);

                // 1. 备份数据库文件
                var dbPath = UserStorage.DatabasePath;
                if (File.Exists(dbPath))
                {
                    var tempDbPath = Path.Combine(Path.GetTempPath(), $"config_backup_{Guid.NewGuid()}.db");
                    try
                    {
                        // 使用 SQLite 的在线备份 API
                        BackupDatabase(dbPath, tempDbPath);

                        // 读取临时文件到内存，然后写入 ZIP
                        var dbBytes = File.ReadAllBytes(tempDbPath);
                        var entry = zipArchive.CreateEntry("config.db", CompressionLevel.Optimal);
                        using (var entryStream = entry.Open())
                        {
                            entryStream.Write(dbBytes, 0, dbBytes.Length);
                        }
                    }
                    finally
                    {
                        // 清理临时文件
                        if (File.Exists(tempDbPath))
                        {
                            try
                            {
                                // 等待一小段时间确保文件句柄被释放
                                System.Threading.Thread.Sleep(100);
                                File.Delete(tempDbPath);
                            }
                            catch
                            {
                                // 忽略清理失败
                            }
                        }
                    }
                }

                // 2. 如果勾选了包含脚本，备份所有文件化工作流、规则和资源目录
                if (includeScripts)
                {
                    foreach (var dirPath in UserPathProvider.BackupDirectories)
                    {
                        if (Directory.Exists(dirPath))
                        {
                            var entryName = Path.GetRelativePath(Global.UserDataRoot, dirPath)
                                .Replace(Path.DirectorySeparatorChar, '/');
                            AddDirectoryToZip(zipArchive, dirPath, entryName);
                        }
                    }

                    var legacyFileEntries = new (string FilePath, string EntryName, string? PreferredPath)[]
                    {
                        (UserPathProvider.LegacyPickExactBlacklistTextPath, "pick_black_lists.txt", UserPathProvider.PickExactBlacklistPath),
                        (UserPathProvider.LegacyPickFuzzyBlacklistTextPath, "pick_fuzzy_black_lists.txt", UserPathProvider.PickFuzzyBlacklistPath),
                        (UserPathProvider.LegacyPickWhitelistTextPath, "pick_white_lists.txt", UserPathProvider.PickWhitelistPath),
                        (UserPathProvider.LegacyPickBlacklistJsonPath, "pick_black_lists.json", UserPathProvider.PickExactBlacklistPath),
                        (UserPathProvider.LegacyPickWhitelistJsonPath, "pick_white_lists.json", UserPathProvider.PickWhitelistPath)
                    };

                    foreach (var (filePath, entryName, preferredPath) in legacyFileEntries)
                    {
                        if (!File.Exists(filePath))
                        {
                            continue;
                        }

                        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath))
                        {
                            continue;
                        }

                        AddFileToZip(zipArchive, filePath, entryName);
                    }
                }
            });

            ThemedMessageBox.Information($"配置备份成功！\n\n备份文件：{backupPath}");

            // 如果启用了远程备份，上传到远程存储
            if (Config.CommonConfig.RemoteBackupEnabled)
            {
                try
                {
                    await UploadToRemoteStorage(backupPath);
                    ThemedMessageBox.Information("远程备份成功！");
                }
                catch (Exception remoteEx)
                {
                    ThemedMessageBox.Warning($"本地备份成功，但远程备份失败：{remoteEx.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Error($"配置备份失败：{ex.Message}");
        }
    }

    private async Task UploadToRemoteStorage(string localFilePath)
    {
        var fileName = Path.GetFileName(localFilePath);

        switch (Config.CommonConfig.RemoteBackupType)
        {
            case "RemoteFolder":
                await UploadToRemoteFolder(localFilePath, fileName);
                break;
            case "OSS":
                await UploadToOSS(localFilePath, fileName);
                break;
            case "WebDAV":
                await UploadToWebDAV(localFilePath, fileName);
                break;
            default:
                throw new NotSupportedException($"不支持的远程备份类型：{Config.CommonConfig.RemoteBackupType}");
        }
    }

    private async Task UploadToRemoteFolder(string localFilePath, string fileName)
    {
        await Task.Run(() =>
        {
            var remotePath = Config.CommonConfig.RemoteBackupFolderPath;
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                throw new InvalidOperationException("远程文件夹路径未配置");
            }

            if (!Directory.Exists(remotePath))
            {
                Directory.CreateDirectory(remotePath);
            }

            var destPath = Path.Combine(remotePath, fileName);
            File.Copy(localFilePath, destPath, true);
        });
    }

    private async Task UploadToOSS(string localFilePath, string fileName)
    {
        await Task.Run(() =>
        {
            var endpoint = Config.CommonConfig.OssEndpoint;
            var accessKeyId = Config.CommonConfig.OssAccessKeyId;
            var accessKeySecret = Config.CommonConfig.OssAccessKeySecret;
            var bucketName = Config.CommonConfig.OssBucketName;
            var pathPrefix = Config.CommonConfig.OssPathPrefix;

            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(accessKeyId) ||
                string.IsNullOrWhiteSpace(accessKeySecret) || string.IsNullOrWhiteSpace(bucketName))
            {
                throw new InvalidOperationException("OSS 配置不完整");
            }

            var client = new Aliyun.OSS.OssClient(endpoint, accessKeyId, accessKeySecret);
            var objectKey = pathPrefix + fileName;

            client.PutObject(bucketName, objectKey, localFilePath);
        });
    }

    private async Task UploadToWebDAV(string localFilePath, string fileName)
    {
        var url = Config.CommonConfig.WebDavUrl;
        var username = Config.CommonConfig.WebDavUsername;
        var password = Config.CommonConfig.WebDavPassword;
        var remotePath = Config.CommonConfig.WebDavRemotePath;

        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(username) ||
            string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("WebDAV 配置不完整");
        }

        var clientParams = new WebDav.WebDavClientParams
        {
            BaseAddress = new Uri(url),
            Credentials = new System.Net.NetworkCredential(username, password)
        };

        var client = new WebDav.WebDavClient(clientParams);

        // 确保远程目录存在
        if (!string.IsNullOrWhiteSpace(remotePath))
        {
            await client.Mkcol(remotePath);
        }

        // 上传文件
        var remoteFilePath = remotePath.TrimEnd('/') + "/" + fileName;
        using var fileStream = File.OpenRead(localFilePath);
        await client.PutFile(remoteFilePath, fileStream);
    }

    private void BackupDatabase(string sourcePath, string destPath)
    {
        using (var sourceConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly"))
        using (var destConnection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={destPath}"))
        {
            sourceConnection.Open();
            destConnection.Open();

            // 使用 SQLite 的 backup API
            sourceConnection.BackupDatabase(destConnection);
        }

        // 确保连接完全关闭
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    [RelayCommand]
    private async Task OnImportConfig()
    {
        try
        {
            var result = await ThemedMessageBox.ShowAsync(
                "导入配置将覆盖当前配置！\n\n建议先备份当前配置。\n\n是否继续？",
                "警告",
                MessageBoxButton.YesNo,
                ThemedMessageBox.MessageBoxIcon.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var openDialog = new OpenFileDialog
            {
                Filter = "ZIP 压缩包 (*.zip)|*.zip",
                Title = "选择要导入的配置备份文件"
            };

            if (openDialog.ShowDialog() != true)
            {
                return;
            }

            var importPath = openDialog.FileName;

            await Task.Run(() =>
            {
                // 关闭连接池，避免导入替换数据库文件时仍持有句柄
                SqliteConnection.ClearAllPools();

                using var zipArchive = ZipFile.OpenRead(importPath);

                // 1. 导入数据库文件
                var dbEntry = zipArchive.GetEntry("config.db");
                if (dbEntry != null)
                {
                    var dbPath = UserStorage.DatabasePath;
                    var dbBackupPath = dbPath + ".backup";
                    var tempDbPath = Path.Combine(Path.GetTempPath(), $"config_import_{Guid.NewGuid()}.db");

                    try
                    {
                        // 备份当前数据库
                        if (File.Exists(dbPath))
                        {
                            File.Copy(dbPath, dbBackupPath, true);
                        }

                        // 先提取到临时文件
                        dbEntry.ExtractToFile(tempDbPath, true);

                        // 等待确保文件句柄释放
                        System.Threading.Thread.Sleep(100);

                        // 删除旧数据库
                        if (File.Exists(dbPath))
                        {
                            File.Delete(dbPath);
                        }

                        // 等待确保文件被删除
                        System.Threading.Thread.Sleep(100);

                        // 复制新数据库
                        File.Copy(tempDbPath, dbPath, true);

                        // 清理临时文件
                        if (File.Exists(tempDbPath))
                        {
                            try
                            {
                                File.Delete(tempDbPath);
                            }
                            catch
                            {
                                // 忽略清理失败
                            }
                        }
                    }
                    catch
                    {
                        // 如果导入失败，恢复备份
                        if (File.Exists(dbBackupPath))
                        {
                            if (File.Exists(dbPath))
                            {
                                File.Delete(dbPath);
                            }
                            File.Copy(dbBackupPath, dbPath, true);
                        }
                        throw;
                    }
                    finally
                    {
                        // 清理备份文件
                        if (File.Exists(dbBackupPath))
                        {
                            try
                            {
                                File.Delete(dbBackupPath);
                            }
                            catch
                            {
                                // 忽略清理失败
                            }
                        }
                    }
                }

                // 2. 导入文件化工作流、规则和资源目录，同时兼容旧备份中的旧顶层目录名
                foreach (var entry in zipArchive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }

                    if (!UserPathProvider.TryResolveBackupEntryPath(entry.FullName, out var destPath))
                    {
                        continue;
                    }

                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    entry.ExtractToFile(destPath, true);
                }
            });

            await ThemedMessageBox.InformationAsync("配置导入成功！\n\n软件将自动重启以应用新配置。");

            // 等待用户关闭对话框
            await Task.Delay(500);

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Error($"配置导入失败：{ex.Message}");
        }
    }

    private void AddDirectoryToZip(ZipArchive zipArchive, string sourcePath, string entryName)
    {
        var files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);
        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(sourcePath, file);
            var zipEntryName = Path.Combine(entryName, relativePath).Replace(Path.DirectorySeparatorChar, '/');
            zipArchive.CreateEntryFromFile(file, zipEntryName, CompressionLevel.Optimal);
        }
    }

    private void AddFileToZip(ZipArchive zipArchive, string filePath, string entryName)
    {
        zipArchive.CreateEntryFromFile(filePath, entryName.Replace(Path.DirectorySeparatorChar, '/'), CompressionLevel.Optimal);
    }

    [RelayCommand]
    private async Task ImportLocalScriptsRepoZip()
    {
        Directory.CreateDirectory(ScriptRepoUpdater.ReposPath);

        var dialog = new OpenFileDialog
        {
            Title = "选择脚本仓库压缩包",
            Filter = "Zip Files (*.zip)|*.zip",
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                await ScriptRepoUpdater.Instance.ImportLocalRepoZip(dialog.FileName);
                ThemedMessageBox.Information("脚本仓库离线包导入成功！");
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Error($"脚本仓库离线包导入失败：{ex.Message}");
            }
        }
    }

    [RelayCommand]
    private void OpenAboutWindow()
    {
        var aboutWindow = new AboutWindow();
        aboutWindow.Owner = Application.Current.MainWindow;
        aboutWindow.ShowDialog();
    }

    [RelayCommand]
    private void OpenKeyBindingsWindow()
    {
        var keyBindingsWindow = KeyBindingsWindow.Instance;
        keyBindingsWindow.Owner = Application.Current.MainWindow;
        keyBindingsWindow.ShowDialog();
    }


    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        await App.GetService<IUpdateService>()!.CheckUpdateAsync(new UpdateOption
        {
            Trigger = UpdateTrigger.Manual,
            Channel = UpdateChannel.Stable
        });
    }

    [RelayCommand]
    private async Task CheckUpdateAlphaAsync()
    {
        var result = await ThemedMessageBox.ShowAsync("测试版本非常不稳定！\n测试版本非常不稳定！\n测试版本非常不稳定！\n\n是否继续检查更新？", "警告", MessageBoxButton.YesNo, ThemedMessageBox.MessageBoxIcon.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }
        
        await App.GetService<IUpdateService>()!.CheckUpdateAsync(new UpdateOption
        {
            Trigger = UpdateTrigger.Manual,
            Channel = UpdateChannel.Alpha,
        });
    }

    // [RelayCommand]
    // private async Task GotoGithubActionAsync()
    // {
    //     await Launcher.LaunchUriAsync(
    //         new Uri("https://github.com/babalae/better-genshin-impact/actions/workflows/publish.yml"));
    // }

    [RelayCommand]
    private async Task OnGameLangSelectionChanged(KeyValuePair<string, string> type)
    {
        await App.ServiceProvider.GetRequiredService<OcrFactory>().Unload();
    }

    [RelayCommand]
    private async Task OnPaddleOcrModelConfigChanged(PaddleOcrModelConfig value)
    {
        Config.OtherConfig.OcrConfig.PaddleOcrModelConfig = value;
        await App.ServiceProvider.GetRequiredService<OcrFactory>().Unload();
    }
}
