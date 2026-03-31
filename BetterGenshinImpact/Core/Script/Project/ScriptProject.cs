using BetterGenshinImpact.Core.Config;
using Microsoft.ClearScript;
using Microsoft.ClearScript.V8;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using BetterGenshinImpact.Core.Script.Dependence;
using Microsoft.ClearScript.JavaScript;

namespace BetterGenshinImpact.Core.Script.Project;

public class ScriptProject
{
    public string ProjectPath { get; set; }
    public string ManifestFile { get; set; }

    public Manifest Manifest { get; set; }

    public string FolderName { get; set; }

    public ScriptProject(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            throw new ArgumentException("脚本文件夹名称不能为空", nameof(folderName));
        }

        var scriptRoot = Path.GetFullPath(UserPathProvider.JsScriptsRoot);
        var candidatePath = Path.GetFullPath(Path.Combine(scriptRoot, folderName));
        if (!IsSubPathOf(scriptRoot, candidatePath))
        {
            throw new ArgumentException($"脚本文件夹路径越界: {folderName}", nameof(folderName));
        }

        FolderName = Path.GetRelativePath(scriptRoot, candidatePath);
        ProjectPath = candidatePath;
        if (!Directory.Exists(ProjectPath))
        {
            throw new DirectoryNotFoundException("脚本文件夹不存在:" + ProjectPath);
        }
        ManifestFile = Path.GetFullPath(Path.Combine(ProjectPath, "manifest.json"));
        if (!File.Exists(ManifestFile))
        {
            throw new FileNotFoundException("manifest.json文件不存在，请确认此脚本是JS脚本类型。" + ManifestFile);
        }

        var manifestJson = UserFileService.ReadAllTextIfExists(ManifestFile)
            ?? throw new FileNotFoundException("manifest.json文件读取失败，请确认此脚本是JS脚本类型。" + ManifestFile);
        Manifest = Manifest.FromJson(manifestJson);
        Manifest.Validate(ProjectPath);
    }

    private static bool IsSubPathOf(string rootPath, string targetPath)
    {
        var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedTarget = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(normalizedRoot, normalizedTarget, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedTarget.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    public ScrollViewer? LoadSettingUi(dynamic context)
    {
        var settingItems = Manifest.LoadSettingItems(ProjectPath);
        if (settingItems.Count == 0)
        {
            return null;
        }
        var stackPanel = new StackPanel
        {
            Margin = new Thickness(0, 0, 20, 0) // 给右侧滚动条留出位置
        };
        foreach (var item in settingItems)
        {
            var controls = item.ToControl(context);
            foreach (var control in controls)
            {
                stackPanel.Children.Add(control);
            }
        }

        var scrollViewer = new ScrollViewer
        {
            Content = stackPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 350 // 设置最大高度
        };

        return scrollViewer;
    }

    private IScriptEngine BuildScriptEngine(PathingPartyConfig? partyConfig)
    {
        V8ScriptEngine engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding | V8ScriptEngineFlags.EnableTaskPromiseConversion);
        
        // packages 依赖和资源重载
        var loader = new PackageDocumentLoader(ProjectPath);
        engine.DocumentSettings.Loader = loader;

        // 添加 packages 到搜索路径
        var libraries = new HashSet<string>(Manifest.Library ?? Array.Empty<string>())
        {
            ".",
            "./packages"
        };

        var libraryList = libraries.ToList();

        EngineExtend.InitHost(engine, ProjectPath, libraryList.ToArray(), partyConfig);
        return engine;
    }

    public async Task ExecuteAsync(dynamic? context = null, PathingPartyConfig? partyConfig=null)
    {
        // 默认值
        GlobalMethod.SetGameMetrics(1920, 1080);
        // 加载代码
        var code = await LoadCode();
        var engine = BuildScriptEngine(partyConfig);
        
        // 使用自定义加载器解析脚本文件
        var loader = (PackageDocumentLoader)engine.DocumentSettings.Loader;

        if (context != null)
        {
            // 写入配置的内容
            engine.AddHostObject("settings", context);
        }
        try
        {
            bool useModule = Manifest.Library.Length != 0 || 
                             code.Contains("import ", StringComparison.Ordinal) || 
                             code.Contains("export ", StringComparison.Ordinal);

            if (useModule)
            {
                // 清除Document缓存
                DocumentLoader.Default.DiscardCachedDocuments();

                string mainScriptPath = Path.Combine(ProjectPath, Manifest.Main);
                string runtimeCode = loader.RewriteScriptCode(code, mainScriptPath);
                
                var evaluation = engine.Evaluate(new DocumentInfo(mainScriptPath) { Category = ModuleCategory.Standard }, runtimeCode);
                if (evaluation is Task task) await task;
            }
            else
            {
                var evaluation = engine.Evaluate(code);
                if (evaluation is Task task) await task;
            }
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            throw;
        }
        finally
        {
            engine.Dispose();
        }
    }

    public async Task<string> LoadCode()
    {
        var code = await File.ReadAllTextAsync(Path.Combine(ProjectPath, Manifest.Main));
        if (string.IsNullOrEmpty(code))
        {
            throw new FileNotFoundException("main js is empty.");
        }

        return code;
    }
}
