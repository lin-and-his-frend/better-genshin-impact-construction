using BetterGenshinImpact.Core.Config;
using System.Reflection;
using System.Text;

namespace BetterGenshinImpact.UnitTest.CoreTests.ConfigTests;

public class PersistenceCompatibilityTests
{
    [Fact]
    public void AppConfigStoreCoverage_ShouldInclude_AllWritableAllConfigProperties()
    {
        var appConfigStoreType = GetCoreType("BetterGenshinImpact.Core.Config.AppConfigStore");
        var sectionsField = appConfigStoreType.GetField("Sections", BindingFlags.NonPublic | BindingFlags.Static);
        var rootStateType = appConfigStoreType.GetNestedType("RootSettingsState", BindingFlags.NonPublic);

        Assert.NotNull(sectionsField);
        Assert.NotNull(rootStateType);

        var sectionPropertyNames = ((Array?)sectionsField!.GetValue(null))!
            .Cast<object>()
            .Select(item => item.GetType().GetProperty("PropertyName")?.GetValue(item) as string)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        var rootPropertyNames = rootStateType!
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);

        var ignoredPropertyNames = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(AllConfig.OnAnyChangedAction)
        };

        var uncoveredProperties = typeof(AllConfig)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.CanRead && property.CanWrite)
            .Select(property => property.Name)
            .Where(name => !ignoredPropertyNames.Contains(name))
            .Where(name => !sectionPropertyNames.Contains(name) && !rootPropertyNames.Contains(name))
            .ToArray();

        Assert.Empty(uncoveredProperties);
    }

    [Theory]
    [InlineData("pick_black_lists.txt")]
    [InlineData("pick_fuzzy_black_lists.txt")]
    [InlineData("pick_white_lists.txt")]
    [InlineData("pick_black_lists.json")]
    [InlineData("pick_white_lists.json")]
    public void TryResolveBackupEntryPath_ShouldAccept_LegacyPickRuleFiles(string entryName)
    {
        var method = GetCoreType("BetterGenshinImpact.Core.Config.UserPathProvider").GetMethod(
            "TryResolveBackupEntryPath",
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);

        object?[] args = [entryName, null];
        var ok = (bool)method!.Invoke(null, args)!;

        Assert.True(ok);
        Assert.NotNull(args[1]);
        Assert.EndsWith(
            Path.Combine("User", entryName),
            args[1] as string,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadFirstAvailableText_ShouldReturn_FirstNonEmptyExistingFile()
    {
        var method = GetCoreType("BetterGenshinImpact.Core.Config.UserFileService").GetMethod(
            "ReadFirstAvailableText",
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);

        var root = Path.Combine(Path.GetTempPath(), "bgi-user-file-service-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var emptyFile = Path.Combine(root, "empty.txt");
            var targetFile = Path.Combine(root, "target.txt");
            File.WriteAllText(emptyFile, "");
            File.WriteAllText(targetFile, "legacy-compatible");

            var result = method!.Invoke(null, [new[] { emptyFile, targetFile }, Encoding.UTF8]) as string;

            Assert.Equal("legacy-compatible", result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Type GetCoreType(string typeName)
    {
        var type = typeof(Global).Assembly.GetType(typeName);
        Assert.NotNull(type);
        return type!;
    }
}
