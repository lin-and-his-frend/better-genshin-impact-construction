using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Helpers.Http;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.Remote;

internal sealed class GenshinCharacterKnowledgeService
{
    private const string HoneyHunterBaseUrl = "https://gensh.honeyhunterworld.com";
    private const string HoneyHunterHomeUrl = "https://gensh.honeyhunterworld.com/?lang=CHS";
    private static readonly TimeSpan CharacterUrlMapCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CharacterEntryCacheTtl = TimeSpan.FromMinutes(20);
    private static readonly Regex QueryTokenRegex = new(@"[\u4e00-\u9fffA-Za-z0-9]{2,}", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex TableRowRegex = new("<tr[^>]*>(?<row>.*?)</tr>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TableCellRegex = new("<td(?<attrs>[^>]*)>(?<cell>.*?)</td>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex AnchorRegex = new("<a[^>]*>(?<anchor>.*?)</a>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ImgAltRegex = new("<img[^>]*\\balt\\s*=\\s*[\"'](?<alt>[^\"']+)[\"'][^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex SpanRegex = new("<span[^>]*>(?<qty>.*?)</span>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex RowspanRegex = new("rowspan\\s*=\\s*[\"']?(?<n>\\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LevelRegex = new("^(?<level>\\d+)(?<plus>\\+?)$", RegexOptions.Compiled);
    private static readonly Regex QuantityRegex = new("^(?<num>[0-9]+(?:\\.[0-9]+)?)(?<unit>[KM]?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HomepageCharacterRowRegex = new("<tr[^>]*\\bid\\s*=\\s*[\"']char_[^\"']+[\"'][^>]*>(?<row>.*?)</tr>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex HomepageCharacterAnchorRegex = new("<a[^>]*\\bhref\\s*=\\s*[\"'](?<href>[^\"']+)[\"'][^>]*>(?<text>.*?)</a>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly string[] CharacterIntentKeywords =
    [
        "原神", "角色", "材料", "突破", "天赋", "培养", "命座", "圣遗物", "武器", "介绍", "生日", "cv", "配队", "需要", "数量"
    ];
    private static readonly HttpClient CharacterCrawlerHttpClient = HttpClientFactory.GetClient(
        "mcp-genshin-character-crawler",
        () => new HttpClient { Timeout = TimeSpan.FromSeconds(18) });

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _characterUrlMapSemaphore = new(1, 1);
    private DateTimeOffset _characterUrlMapUpdatedUtc = DateTimeOffset.MinValue;
    private Dictionary<string, List<string>> _characterUrlMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CachedCharacterEntry> _characterEntryCache = new(StringComparer.OrdinalIgnoreCase);

    public GenshinCharacterKnowledgeService(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<(List<object> matches, string? note)> SearchAsync(string query, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || !LooksLikeCharacterIntent(query))
        {
            return ([], null);
        }

        var characterUrlMap = await GetCharacterUrlMapAsync(ct).ConfigureAwait(false);
        if (characterUrlMap.Count == 0)
        {
            return ([], null);
        }

        var matched = MatchCharacterCandidates(query, characterUrlMap, Math.Clamp(maxResults, 1, 5));
        if (matched.Count == 0)
        {
            return ([], null);
        }

        var fetchTasks = matched
            .Select(candidate => GetCharacterEntryAsync(candidate.url, candidate.name, ct))
            .ToList();
        var entries = await Task.WhenAll(fetchTasks).ConfigureAwait(false);

        var payload = entries
            .Where(entry => entry != null)
            .Select(entry => BuildResultPayload(entry!))
            .Cast<object>()
            .ToList();

        if (payload.Count == 0)
        {
            return ([], null);
        }

        return (payload, "已通过 Honey Hunter 在线抓取角色数据。");
    }

    private async Task<IReadOnlyDictionary<string, List<string>>> GetCharacterUrlMapAsync(CancellationToken ct)
    {
        if (_characterUrlMap.Count > 0 &&
            DateTimeOffset.UtcNow - _characterUrlMapUpdatedUtc <= CharacterUrlMapCacheTtl)
        {
            return _characterUrlMap;
        }

        await _characterUrlMapSemaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_characterUrlMap.Count > 0 &&
                DateTimeOffset.UtcNow - _characterUrlMapUpdatedUtc <= CharacterUrlMapCacheTtl)
            {
                return _characterUrlMap;
            }

            var html = await FetchHtmlAsync(HoneyHunterHomeUrl, ct).ConfigureAwait(false);
            var parsed = ParseCharacterUrlMap(html);
            if (parsed.Count == 0)
            {
                return _characterUrlMap;
            }

            _characterUrlMap = parsed;
            _characterUrlMapUpdatedUtc = DateTimeOffset.UtcNow;
            return _characterUrlMap;
        }
        finally
        {
            _characterUrlMapSemaphore.Release();
        }
    }

    private async Task<CharacterKnowledgeEntry?> GetCharacterEntryAsync(string url, string fallbackName, CancellationToken ct)
    {
        if (_characterEntryCache.TryGetValue(url, out var cached) &&
            DateTimeOffset.UtcNow - cached.UpdatedUtc <= CharacterEntryCacheTtl)
        {
            return cached.Entry;
        }

        try
        {
            var html = await FetchHtmlAsync(url, ct).ConfigureAwait(false);
            var parsed = ParseCharacterEntryFromHtml(html, fallbackName, url);
            if (parsed == null)
            {
                return null;
            }

            _characterEntryCache[url] = new CachedCharacterEntry(parsed, DateTimeOffset.UtcNow);
            return parsed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "在线角色解析失败: {Url}", url);
            return null;
        }
    }

    private static List<(string name, string url)> MatchCharacterCandidates(
        string query,
        IReadOnlyDictionary<string, List<string>> characterUrlMap,
        int maxResults)
    {
        var normalizedQuery = NormalizeText(query);
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return [];
        }

        var tokens = QueryTokenRegex.Matches(query)
            .Select(match => NormalizeText(match.Value))
            .Where(token => token.Length >= 2)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var ranked = new List<(string name, string url, int score)>();
        foreach (var pair in characterUrlMap)
        {
            if (pair.Value.Count == 0)
            {
                continue;
            }

            var normalizedName = NormalizeText(pair.Key);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                continue;
            }

            var score = 0;
            if (normalizedQuery.Contains(normalizedName, StringComparison.Ordinal))
            {
                score = 220 + normalizedName.Length * 3;
            }
            else
            {
                foreach (var token in tokens)
                {
                    if (token == normalizedName)
                    {
                        score = Math.Max(score, 180 + normalizedName.Length * 2);
                        continue;
                    }

                    if (token.Contains(normalizedName, StringComparison.Ordinal) ||
                        normalizedName.Contains(token, StringComparison.Ordinal))
                    {
                        score = Math.Max(score, 130 + Math.Min(token.Length, normalizedName.Length) * 2);
                    }
                }
            }

            if (score <= 0)
            {
                continue;
            }

            ranked.Add((pair.Key, pair.Value[0], score));
        }

        if (ranked.Count == 0)
        {
            return [];
        }

        return ranked
            .OrderByDescending(item => item.score)
            .ThenBy(item => item.name.Length)
            .Take(maxResults)
            .Select(item => (item.name, item.url))
            .ToList();
    }

    private CharacterKnowledgeEntry? ParseCharacterEntryFromHtml(string html, string fallbackName, string sourceUrl)
    {
        var (name, introduction) = ParseBasicCharacterInfo(html);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fallbackName;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var characterAscension = ParseCharacterAscensionMaxTotal(html);
        var skillAscension = ParseSkillAscensionMaxTotalAllSkills(html);
        var allRequired = AggregateMaterials(characterAscension, skillAscension);

        return new CharacterKnowledgeEntry(
            name.Trim(),
            introduction?.Trim() ?? string.Empty,
            sourceUrl,
            allRequired,
            characterAscension,
            skillAscension);
    }

    private static (string? name, string? introduction) ParseBasicCharacterInfo(string html)
    {
        var mainTable = ExtractFirstTableByClass(html, "main_table");
        if (string.IsNullOrWhiteSpace(mainTable))
        {
            return (null, null);
        }

        string? name = null;
        string? introduction = null;
        foreach (var rowHtml in ExtractTableRows(mainTable))
        {
            var cells = ExtractTableCells(rowHtml);
            if (cells.Count < 2)
            {
                continue;
            }

            var key = CleanText(cells[^2].InnerHtml);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var value = CleanText(cells[^1].InnerHtml);
            if (string.Equals(key, "Name", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                name = value;
            }
            else if (string.Equals(key, "Description", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                introduction = value;
            }
        }

        return (name, introduction);
    }

    private static List<MaterialItem> ParseCharacterAscensionMaxTotal(string html)
    {
        var section = ExtractSectionById(html, "char_stats");
        if (string.IsNullOrWhiteSpace(section))
        {
            return [];
        }

        var statTable = ExtractFirstTableByClass(section, "stat_table");
        if (string.IsNullOrWhiteSpace(statTable))
        {
            return [];
        }

        var bestLevelScore = -1;
        var bestTotalMaterials = new List<MaterialItem>();
        var cachedTotals = new List<MaterialItem>();
        var cachedTotalRowspanLeft = 0;

        foreach (var rowHtml in ExtractTableRows(statTable))
        {
            var cells = ExtractTableCells(rowHtml);
            if (cells.Count < 7)
            {
                continue;
            }

            var level = CleanText(cells[0].InnerHtml);
            var levelScore = ParseLevelScore(level);
            if (levelScore < 0)
            {
                continue;
            }

            List<MaterialItem> rowTotalMaterials;
            if (cells.Count >= 9)
            {
                rowTotalMaterials = ParseMaterialsFromCell(cells[8].InnerHtml);
                var rowspan = ParseRowspan(cells[8].Attrs);
                if (rowspan > 1)
                {
                    cachedTotals = CloneMaterials(rowTotalMaterials);
                    cachedTotalRowspanLeft = rowspan - 1;
                }
                else
                {
                    cachedTotals.Clear();
                    cachedTotalRowspanLeft = 0;
                }
            }
            else if (cachedTotalRowspanLeft > 0)
            {
                rowTotalMaterials = CloneMaterials(cachedTotals);
                cachedTotalRowspanLeft--;
            }
            else
            {
                rowTotalMaterials = [];
            }

            if (rowTotalMaterials.Count == 0 || levelScore < bestLevelScore)
            {
                continue;
            }

            bestLevelScore = levelScore;
            bestTotalMaterials = rowTotalMaterials;
        }

        return bestTotalMaterials;
    }

    private static List<MaterialItem> ParseSkillAscensionMaxTotalAllSkills(string html)
    {
        var section = ExtractSectionById(html, "skill_ascension_material");
        if (string.IsNullOrWhiteSpace(section))
        {
            return [];
        }

        var skillTotals = new List<IReadOnlyList<MaterialItem>>();
        foreach (var tableHtml in ExtractAllTablesByClass(section, "asc_table"))
        {
            var bestLevelScore = -1;
            List<MaterialItem>? bestLevelMaterials = null;

            foreach (var rowHtml in ExtractTableRows(tableHtml))
            {
                var cells = ExtractTableCells(rowHtml);
                if (cells.Count < 3)
                {
                    continue;
                }

                var level = CleanText(cells[0].InnerHtml);
                var levelScore = ParseLevelScore(level);
                if (levelScore < 0)
                {
                    continue;
                }

                var totalMaterials = ParseMaterialsFromCell(cells[2].InnerHtml);
                if (totalMaterials.Count == 0 || levelScore < bestLevelScore)
                {
                    continue;
                }

                bestLevelScore = levelScore;
                bestLevelMaterials = totalMaterials;
            }

            if (bestLevelMaterials is { Count: > 0 })
            {
                skillTotals.Add(bestLevelMaterials);
            }
        }

        if (skillTotals.Count == 0)
        {
            return [];
        }

        return AggregateMaterials(skillTotals.ToArray());
    }

    private static List<MaterialItem> ParseMaterialsFromCell(string cellHtml)
    {
        if (string.IsNullOrWhiteSpace(cellHtml))
        {
            return [];
        }

        var list = new List<MaterialItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match anchorMatch in AnchorRegex.Matches(cellHtml))
        {
            var anchorHtml = anchorMatch.Groups["anchor"].Value;
            if (string.IsNullOrWhiteSpace(anchorHtml))
            {
                continue;
            }

            var altMatch = ImgAltRegex.Match(anchorHtml);
            if (!altMatch.Success)
            {
                continue;
            }

            var name = CleanText(WebUtility.HtmlDecode(altMatch.Groups["alt"].Value));
            if (string.IsNullOrWhiteSpace(name) ||
                string.Equals(name, "n/a", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var quantityText = string.Empty;
            var quantityMatch = SpanRegex.Match(anchorHtml);
            if (quantityMatch.Success)
            {
                quantityText = CleanText(quantityMatch.Groups["qty"].Value);
            }

            var quantity = ParseQuantity(quantityText);
            var normalizedQuantityText = string.IsNullOrWhiteSpace(quantityText)
                ? quantity.HasValue ? FormatQuantity(quantity.Value) : "?"
                : quantityText;
            var dedupeKey = $"{name}|{normalizedQuantityText}";
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            list.Add(new MaterialItem(name, normalizedQuantityText, quantity));
        }

        return list;
    }

    private static int ParseRowspan(string attrs)
    {
        if (string.IsNullOrWhiteSpace(attrs))
        {
            return 1;
        }

        var match = RowspanRegex.Match(attrs);
        if (!match.Success)
        {
            return 1;
        }

        return int.TryParse(match.Groups["n"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 1;
    }

    private static int ParseLevelScore(string level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return -1;
        }

        var match = LevelRegex.Match(level.Trim());
        if (!match.Success)
        {
            return -1;
        }

        if (!int.TryParse(match.Groups["level"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var levelNumber))
        {
            return -1;
        }

        var plus = string.Equals(match.Groups["plus"].Value, "+", StringComparison.Ordinal) ? 1 : 0;
        return levelNumber * 2 + plus;
    }

    private static double? ParseQuantity(string quantityText)
    {
        if (string.IsNullOrWhiteSpace(quantityText))
        {
            return null;
        }

        var text = quantityText.Trim().Replace(",", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        var match = QuantityRegex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        if (!double.TryParse(match.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        var unit = match.Groups["unit"].Value;
        if (string.Equals(unit, "K", StringComparison.OrdinalIgnoreCase))
        {
            value *= 1000;
        }
        else if (string.Equals(unit, "M", StringComparison.OrdinalIgnoreCase))
        {
            value *= 1000000;
        }

        return value;
    }

    private static string FormatQuantity(double value)
    {
        return Math.Abs(value - Math.Round(value)) < 0.000001
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static List<MaterialItem> CloneMaterials(IReadOnlyList<MaterialItem> source)
    {
        var cloned = new List<MaterialItem>(source.Count);
        foreach (var item in source)
        {
            cloned.Add(new MaterialItem(item.Name, item.QuantityText, item.Quantity));
        }

        return cloned;
    }

    private static List<MaterialItem> AggregateMaterials(params IReadOnlyList<MaterialItem>[] groups)
    {
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            foreach (var item in group)
            {
                if (!item.Quantity.HasValue)
                {
                    continue;
                }

                if (totals.TryGetValue(item.Name, out var current))
                {
                    totals[item.Name] = current + item.Quantity.Value;
                }
                else
                {
                    totals[item.Name] = item.Quantity.Value;
                }
            }
        }

        return totals
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new MaterialItem(pair.Key, FormatQuantity(pair.Value), pair.Value))
            .ToList();
    }

    private static Dictionary<string, List<string>> ParseCharacterUrlMap(string html)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match rowMatch in HomepageCharacterRowRegex.Matches(html))
        {
            var rowHtml = rowMatch.Groups["row"].Value;
            if (string.IsNullOrWhiteSpace(rowHtml))
            {
                continue;
            }

            Match? targetAnchor = null;
            foreach (Match anchorMatch in HomepageCharacterAnchorRegex.Matches(rowHtml))
            {
                var text = CleanText(anchorMatch.Groups["text"].Value);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                targetAnchor = anchorMatch;
            }

            if (targetAnchor == null)
            {
                continue;
            }

            var name = CleanText(targetAnchor.Groups["text"].Value);
            var href = targetAnchor.Groups["href"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            if (!Uri.TryCreate(new Uri(HoneyHunterBaseUrl), href, out var absoluteUri))
            {
                continue;
            }

            if (!map.TryGetValue(name, out var urls))
            {
                urls = [];
                map[name] = urls;
            }

            var normalizedUrl = absoluteUri.ToString();
            if (!urls.Contains(normalizedUrl, StringComparer.OrdinalIgnoreCase))
            {
                urls.Add(normalizedUrl);
            }
        }

        return map;
    }

    private static string? ExtractSectionById(string html, string sectionId)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(sectionId))
        {
            return null;
        }

        var pattern = $"<section[^>]*\\bid\\s*=\\s*[\"']{Regex.Escape(sectionId)}[\"'][^>]*>(?<content>.*?)</section>";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["content"].Value : null;
    }

    private static string? ExtractFirstTableByClass(string html, string className)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(className))
        {
            return null;
        }

        var pattern = $"<table[^>]*\\bclass\\s*=\\s*[\"'][^\"']*\\b{Regex.Escape(className)}\\b[^\"']*[\"'][^>]*>(?<content>.*?)</table>";
        var match = Regex.Match(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["content"].Value : null;
    }

    private static IReadOnlyList<string> ExtractAllTablesByClass(string html, string className)
    {
        if (string.IsNullOrWhiteSpace(html) || string.IsNullOrWhiteSpace(className))
        {
            return [];
        }

        var pattern = $"<table[^>]*\\bclass\\s*=\\s*[\"'][^\"']*\\b{Regex.Escape(className)}\\b[^\"']*[\"'][^>]*>(?<content>.*?)</table>";
        var matches = Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (matches.Count == 0)
        {
            return [];
        }

        var list = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            var content = match.Groups["content"].Value;
            if (!string.IsNullOrWhiteSpace(content))
            {
                list.Add(content);
            }
        }

        return list;
    }

    private static IReadOnlyList<string> ExtractTableRows(string tableHtml)
    {
        var rows = new List<string>();
        foreach (Match match in TableRowRegex.Matches(tableHtml))
        {
            var row = match.Groups["row"].Value;
            if (!string.IsNullOrWhiteSpace(row))
            {
                rows.Add(row);
            }
        }

        return rows;
    }

    private static IReadOnlyList<TableCell> ExtractTableCells(string rowHtml)
    {
        var cells = new List<TableCell>();
        foreach (Match match in TableCellRegex.Matches(rowHtml))
        {
            cells.Add(new TableCell(
                match.Groups["attrs"].Value,
                match.Groups["cell"].Value));
        }

        return cells;
    }

    private static bool LooksLikeCharacterIntent(string query)
    {
        foreach (var keyword in CharacterIntentKeywords)
        {
            if (query.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string CleanText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var noTags = HtmlTagRegex.Replace(value, string.Empty);
        return WebUtility.HtmlDecode(WhitespaceRegex.Replace(noTags, " ")).Trim();
    }

    private static string NormalizeText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch is >= '\u4E00' and <= '\u9FFF')
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static object BuildResultPayload(CharacterKnowledgeEntry entry)
    {
        var snippet = BuildSnippet(entry);
        return new
        {
            title = $"{entry.Name}（在线角色库）",
            url = entry.SourceUrl,
            snippet,
            source = "honeyhunter_crawler",
            character = new
            {
                name = entry.Name,
                introduction = Truncate(entry.Introduction, 180)
            },
            materials = new
            {
                allRequired = entry.AllRequiredMaterials.Take(40).Select(ToSerializableMaterial).ToList(),
                characterAscension = entry.CharacterAscensionMaterials.Take(30).Select(ToSerializableMaterial).ToList(),
                skillAscension = entry.SkillAscensionMaterials.Take(30).Select(ToSerializableMaterial).ToList()
            }
        };
    }

    private static string BuildSnippet(CharacterKnowledgeEntry entry)
    {
        if (entry.AllRequiredMaterials.Count == 0)
        {
            return string.IsNullOrWhiteSpace(entry.Introduction)
                ? $"在线命中角色：{entry.Name}"
                : Truncate(entry.Introduction, 200);
        }

        var preview = string.Join("、", entry.AllRequiredMaterials
            .Take(8)
            .Select(material => $"{material.Name}×{material.QuantityText}"));
        return $"{entry.Name} 全培养材料预览：{preview}";
    }

    private static object ToSerializableMaterial(MaterialItem material)
    {
        return new
        {
            name = material.Name,
            quantityText = material.QuantityText,
            quantity = material.Quantity
        };
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Trim();
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..Math.Max(0, maxLength - 1)] + "…";
    }

    private static HttpRequestMessage CreateHtmlRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        request.Headers.TryAddWithoutValidation("accept-language", "zh-CN,zh;q=0.9,en;q=0.8");
        request.Headers.UserAgent.ParseAdd("BetterGI-MCP/1.0");
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36");
        return request;
    }

    private static async Task<string> FetchHtmlAsync(string url, CancellationToken ct)
    {
        using var request = CreateHtmlRequest(url);
        using var response = await CharacterCrawlerHttpClient.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        return body;
    }

    private sealed record TableCell(string Attrs, string InnerHtml);

    private sealed record MaterialItem(string Name, string QuantityText, double? Quantity);

    private sealed record CharacterKnowledgeEntry(
        string Name,
        string Introduction,
        string SourceUrl,
        IReadOnlyList<MaterialItem> AllRequiredMaterials,
        IReadOnlyList<MaterialItem> CharacterAscensionMaterials,
        IReadOnlyList<MaterialItem> SkillAscensionMaterials);

    private sealed record CachedCharacterEntry(CharacterKnowledgeEntry Entry, DateTimeOffset UpdatedUtc);
}
