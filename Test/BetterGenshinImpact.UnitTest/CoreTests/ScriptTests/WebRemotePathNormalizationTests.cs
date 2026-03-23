using System.Reflection;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class WebRemotePathNormalizationTests
{
    [Fact]
    public void TryNormalizeRelativePathUnderRoot_ShouldReject_PathTraversal()
    {
        var method = GetNormalizeMethod();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bgi-webremote-test-root"));

        object?[] args = [root, @"..\outside.json", null];
        var ok = (bool)method.Invoke(null, args)!;

        Assert.False(ok);
        Assert.True(string.IsNullOrEmpty(args[2] as string));
    }

    [Fact]
    public void TryNormalizeRelativePathUnderRoot_ShouldNormalize_ValidPath()
    {
        var method = GetNormalizeMethod();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bgi-webremote-test-root-safe"));

        object?[] args = [root, @"folder\demo.json", null];
        var ok = (bool)method.Invoke(null, args)!;

        Assert.True(ok);
        Assert.Equal("folder/demo.json", args[2] as string);
    }

    private static MethodInfo GetNormalizeMethod()
    {
        var coreAssembly = typeof(BetterGenshinImpact.Core.Config.Global).Assembly;
        var type = coreAssembly.GetType("BetterGenshinImpact.Service.Remote.WebRemoteService");
        Assert.NotNull(type);

        var method = type!.GetMethod(
            "TryNormalizeRelativePathUnderRoot",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        Assert.NotNull(method);
        return method!;
    }
}
