using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class GenshinStartArgsValidatorTests
{
    [Fact]
    public void TryNormalize_ShouldPass_ForWhitelistedArgs()
    {
        var ok = GenshinStartArgsValidator.TryNormalize(
            "-popupwindow -screen-width 1920 -screen-height 1080 -monitor 1",
            out var normalized,
            out var error
        );

        Assert.True(ok);
        Assert.Equal(string.Empty, error);
        Assert.Equal("-popupwindow -screen-width 1920 -screen-height 1080 -monitor 1", normalized);
    }

    [Theory]
    [InlineData("-screen-width 1920 & start \"\" cmd.exe")]
    [InlineData("-unknown-flag 1")]
    [InlineData("-screen-height")]
    [InlineData("-window-mode borderless")]
    [InlineData("-platform_type CLOUD_THIRD_PARTY_PC")]
    public void TryNormalize_ShouldReject_ForInvalidArgs(string rawArgs)
    {
        var ok = GenshinStartArgsValidator.TryNormalize(rawArgs, out var normalized, out var error);

        Assert.False(ok);
        Assert.Equal(string.Empty, normalized);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("-screen-fullscreen")]
    [InlineData("-screen-fullscreen 0")]
    [InlineData("-screen-fullscreen 1")]
    public void TryNormalize_ShouldAccept_ForScreenFullscreenVariants(string rawArgs)
    {
        var ok = GenshinStartArgsValidator.TryNormalize(rawArgs, out var normalized, out var error);

        Assert.True(ok);
        Assert.Equal(string.Empty, error);
        Assert.Equal(rawArgs, normalized);
    }
}
