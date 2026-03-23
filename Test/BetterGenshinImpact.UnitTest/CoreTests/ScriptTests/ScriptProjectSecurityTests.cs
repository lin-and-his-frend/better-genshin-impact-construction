using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Project;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class ScriptProjectSecurityTests
{
    [Fact]
    public void Constructor_ShouldReject_PathTraversalOutsideScriptRoot()
    {
        var scriptRoot = Path.GetFullPath(Global.ScriptPath());
        Directory.CreateDirectory(scriptRoot);

        var marker = Guid.NewGuid().ToString("N");
        var outsideFolderName = $"outside-{marker}";
        var outsideFolder = Path.Combine(Directory.GetParent(scriptRoot)!.FullName, outsideFolderName);
        var safeFolder = Path.Combine(scriptRoot, $"safe-{marker}");

        try
        {
            CreateMinimalScript(outsideFolder, $"outside-{marker}");
            CreateMinimalScript(safeFolder, $"safe-{marker}");

            var ex = Assert.Throws<ArgumentException>(() => new ScriptProject($@"..\{outsideFolderName}"));
            Assert.Contains("路径越界", ex.Message);

            var safeProject = new ScriptProject(Path.GetFileName(safeFolder));
            Assert.Equal(Path.GetFileName(safeFolder), safeProject.FolderName);
        }
        finally
        {
            TryDeleteDirectory(outsideFolder);
            TryDeleteDirectory(safeFolder);
        }
    }

    private static void CreateMinimalScript(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "manifest.json"),
            $$"""
              {
                "name": "{{name}}",
                "version": "1.0.0",
                "main": "main.js"
              }
              """
        );
        File.WriteAllText(Path.Combine(folder, "main.js"), "console.log('ok');");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // ignore cleanup failures in tests
        }
    }
}
