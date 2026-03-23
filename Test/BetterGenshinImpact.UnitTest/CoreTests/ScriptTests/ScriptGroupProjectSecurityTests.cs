using System.Reflection;
using BetterGenshinImpact.Core.Script.Group;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class ScriptGroupProjectSecurityTests
{
    [Fact]
    public void TryResolvePathUnderRoot_ShouldReject_PathTraversal()
    {
        var method = GetResolverMethod();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bgi-scriptgroup-test-root"));

        object?[] args = [root, new string?[] { @"..\outside.json" }, null];
        var ok = (bool)method.Invoke(null, args)!;

        Assert.False(ok);
        Assert.True(string.IsNullOrEmpty(args[2] as string));
    }

    [Fact]
    public void TryResolvePathUnderRoot_ShouldAllow_InRootPath()
    {
        var method = GetResolverMethod();
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bgi-scriptgroup-test-root-safe"));

        object?[] args = [root, new string?[] { "folder", "demo.json" }, null];
        var ok = (bool)method.Invoke(null, args)!;

        Assert.True(ok);
        var resolved = args[2] as string;
        Assert.False(string.IsNullOrWhiteSpace(resolved));
        Assert.StartsWith(root, resolved!, StringComparison.OrdinalIgnoreCase);
    }

    private static MethodInfo GetResolverMethod()
    {
        var method = typeof(ScriptGroupProject).GetMethod(
            "TryResolvePathUnderRoot",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        Assert.NotNull(method);
        return method!;
    }
}
