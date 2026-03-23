using BetterGenshinImpact.GameTask;
using System.Diagnostics;
using System.Reflection;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class SystemControlStartInfoTests
{
    [Fact]
    public void BuildLocalStartProcessStartInfo_ShouldNotUseCmdShell()
    {
        var psi = InvokeBuildLocalStartProcessStartInfo(
            @"C:\Games\YuanShen.exe",
            @"C:\Temp\game&whoami&x",
            "-popupwindow -screen-width 1920 -screen-height 1080"
        );

        Assert.Equal(@"C:\Games\YuanShen.exe", psi.FileName);
        Assert.Equal(@"C:\Temp\game&whoami&x", psi.WorkingDirectory);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.CreateNoWindow);
        Assert.False(string.Equals(Path.GetFileName(psi.FileName), "cmd.exe", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(psi.ArgumentList, token => string.Equals(token, "/c", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(psi.ArgumentList, token => string.Equals(token, "start", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            ["-popupwindow", "-screen-width", "1920", "-screen-height", "1080"],
            psi.ArgumentList.ToArray()
        );
    }

    [Fact]
    public void BuildLocalStartProcessStartInfo_ShouldReturnEmptyArgumentList_ForEmptyArgs()
    {
        var psi = InvokeBuildLocalStartProcessStartInfo(
            @"C:\Games\YuanShen.exe",
            @"C:\Games",
            "   "
        );

        Assert.Empty(psi.ArgumentList);
    }

    private static ProcessStartInfo InvokeBuildLocalStartProcessStartInfo(string path, string workdir, string args)
    {
        var method = typeof(SystemControl).GetMethod(
            "BuildLocalStartProcessStartInfo",
            BindingFlags.NonPublic | BindingFlags.Static
        );

        Assert.NotNull(method);
        var result = method!.Invoke(null, [path, workdir, args]);
        Assert.IsType<ProcessStartInfo>(result);
        return (ProcessStartInfo)result!;
    }
}
