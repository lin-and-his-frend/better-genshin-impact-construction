using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using BetterGenshinImpact.Helpers.Http;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.Remote;

internal sealed class OfficialSiteKnowledgeService
{
    private const string RootSitemapUrl = "https://www.bettergi.com/sitemap.xml";
    private const int MaxSitemapDepth = 120;
    private const int MaxPrimaryPageCount = 180;
    private const int MaxIndexedPageCount = 260;
    private const int MinPreferredPageCount = 30;
    private const int MaxLinkExpansionSources = 72;
    private static readonly Uri RootSiteUri = new("https://www.bettergi.com/");
    private static readonly TimeSpan RefreshTtl = TimeSpan.FromMinutes(30);
    private static readonly HttpClient OfficialSiteHttpClient = HttpClientFactory.GetClient(
        "mcp-official-site-index",
        () => new HttpClient { Timeout = TimeSpan.FromSeconds(12) });

    private static readonly Regex ScriptTagRegex = new(@"<script\b[^>]*>[\s\S]*?</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StyleTagRegex = new(@"<style\b[^>]*>[\s\S]*?</style>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoScriptTagRegex = new(@"<noscript\b[^>]*>[\s\S]*?</noscript>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex TitleRegex = new(@"<title\b[^>]*>(?<title>[\s\S]*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MainTagRegex = new(@"<main\b[^>]*>(?<content>[\s\S]*?)</main>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ArticleTagRegex = new(@"<article\b[^>]*>(?<content>[\s\S]*?)</article>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MetaDescriptionRegex = new(
        @"<meta\b(?=[^>]*\b(?:name|property)\s*=\s*[""'](?:description|og:description|twitter:description)[""'])(?=[^>]*\bcontent\s*=\s*[""'](?<content>[^""']+)[""'])[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AnchorHrefRegex = new(@"<a\b[^>]*?\bhref\s*=\s*[""'](?<href>[^""'#>]+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TokenRegex = new(@"[\u4e00-\u9fff]+|[a-z0-9][a-z0-9._/-]*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly string[] IgnoreFileExtensions = [".xml", ".jpg", ".jpeg", ".png", ".svg", ".gif", ".webp", ".ico", ".css", ".js", ".json", ".map", ".txt", ".woff", ".woff2"];

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyList<DocPage> _pages = [];
    private DateTimeOffset _pagesUpdatedUtc = DateTimeOffset.MinValue;
    private int _warmupStarted;

    public OfficialSiteKnowledgeService(ILogger logger)
    {
        _logger = logger;
    }

    public void WarmupInBackground()
    {
        if (Interlocked.Exchange(ref _warmupStarted, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await EnsureIndexedAsync(cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "MCP 官网索引预热失败");
            }
        });
    }

    public async Task<(IReadOnlyList<OfficialDocHit> hits, int pageCount, string? warning)> SearchAsync(string query, int limit, CancellationToken ct)
    {
        var warning = await EnsureIndexedAsync(ct);
        limit = Math.Clamp(limit, 1, 20);
        var normalized = NormalizeForSearch(query);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return ([], _pages.Count, warning);
        }

        var tokens = ExtractTokens(normalized);
        var hits = RankPages(normalized, tokens, limit);
        return (hits, _pages.Count, warning);
    }

    public Task<(IReadOnlyList<OfficialDocHit> hits, int pageCount, string? warning)> GetFeatureDetailAsync(string feature, int limit, CancellationToken ct)
    {
        var query = $"{feature} 功能 使用 设置 教程";
        return SearchAsync(query, limit, ct);
    }

    public Task<(IReadOnlyList<OfficialDocHit> hits, int pageCount, string? warning)> GetFaqAsync(string? query, int limit, CancellationToken ct)
    {
        var merged = string.IsNullOrWhiteSpace(query) ? "FAQ 常见问题 报错" : $"FAQ 常见问题 {query}";
        return SearchAsync(merged, limit, ct);
    }

    public Task<(IReadOnlyList<OfficialDocHit> hits, int pageCount, string? warning)> GetQuickstartAsync(int limit, CancellationToken ct)
    {
        return SearchAsync("快速开始 快速上手 入门 教程 安装", limit, ct);
    }

    public async Task<(IReadOnlyList<OfficialDownloadLink> links, IReadOnlyList<OfficialDocHit> relatedPages, int pageCount, string? warning)> GetDownloadInfoAsync(int limit, CancellationToken ct)
    {
        var warning = await EnsureIndexedAsync(ct);
        limit = Math.Clamp(limit, 1, 30);
        var related = RankPages("下载 download release 安装包 版本", ExtractTokens("下载 download release 安装包 版本"), 8);

        var pagesByUrl = _pages.ToDictionary(p => p.Url, StringComparer.OrdinalIgnoreCase);
        var candidatePages = new List<DocPage>();
        foreach (var hit in related)
        {
            if (pagesByUrl.TryGetValue(hit.Url, out var page))
            {
                candidatePages.Add(page);
            }
        }

        foreach (var page in _pages)
        {
            if (candidatePages.Count >= 12)
            {
                break;
            }

            if (ContainsAnyKeyword(page.UrlLower, "download", "release") ||
                ContainsAnyKeyword(page.TitleLower, "下载", "download", "release", "版本"))
            {
                candidatePages.Add(page);
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<OfficialDownloadLink>(limit);
        foreach (var page in candidatePages.DistinctBy(p => p.Url))
        {
            foreach (var link in page.Links)
            {
                if (!IsDownloadLikeLink(link) || !seen.Add(link))
                {
                    continue;
                }

                links.Add(new OfficialDownloadLink(link, page.Title, page.Url));
                if (links.Count >= limit)
                {
                    break;
                }
            }

            if (links.Count >= limit)
            {
                break;
            }
        }

        return (links, related, _pages.Count, warning);
    }

    private async Task<string?> EnsureIndexedAsync(CancellationToken ct)
    {
        if (_pages.Count > 0 && DateTimeOffset.UtcNow - _pagesUpdatedUtc < RefreshTtl)
        {
            return null;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_pages.Count > 0 && DateTimeOffset.UtcNow - _pagesUpdatedUtc < RefreshTtl)
            {
                return null;
            }

            var pages = await BuildIndexAsync(ct);
            if (pages.Count > 0)
            {
                _pages = pages;
                _pagesUpdatedUtc = DateTimeOffset.UtcNow;
                return null;
            }

            if (_pages.Count > 0)
            {
                return "官网索引刷新失败，已使用缓存。";
            }

            return "官网索引为空，请稍后重试。";
        }
        catch (OperationCanceledException)
        {
            if (_pages.Count > 0)
            {
                return "官网索引刷新超时，已使用缓存。";
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "构建官网索引失败");
            if (_pages.Count > 0)
            {
                return $"官网索引刷新失败，已使用缓存：{ex.Message}";
            }

            return $"官网索引构建失败：{ex.Message}";
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<IReadOnlyList<DocPage>> BuildIndexAsync(CancellationToken ct)
    {
        var pageUrls = await LoadSitemapPageUrlsAsync(ct);
        if (pageUrls.Count == 0)
        {
            pageUrls = BuildFallbackSeedPageUrls().ToList();
        }

        var initialUrls = pageUrls
            .Take(MaxPrimaryPageCount)
            .ToList();

        var pages = await FetchPagesAsync(initialUrls, ct);
        var pageMap = pages.ToDictionary(page => page.Url, StringComparer.OrdinalIgnoreCase);

        var shouldExpandLinks = pageMap.Count < MinPreferredPageCount || pageUrls.Count < MinPreferredPageCount;
        if (shouldExpandLinks && pageMap.Count < MaxIndexedPageCount)
        {
            var additionalUrls = BuildLinkExpansionUrls(pageMap.Values, pageMap.Keys);
            var quota = Math.Max(0, MaxIndexedPageCount - pageMap.Count);
            if (quota > 0)
            {
                foreach (var seed in BuildFallbackSeedPageUrls())
                {
                    if (quota <= 0)
                    {
                        break;
                    }

                    if (pageMap.ContainsKey(seed) || additionalUrls.Contains(seed))
                    {
                        continue;
                    }

                    additionalUrls.Add(seed);
                    quota--;
                }
            }

            var urlsToFetch = additionalUrls
                .Take(Math.Max(0, MaxIndexedPageCount - pageMap.Count))
                .ToList();
            var expandedPages = await FetchPagesAsync(urlsToFetch, ct);
            foreach (var page in expandedPages)
            {
                pageMap[page.Url] = page;
            }
        }

        return pageMap.Values
            .OrderBy(p => p.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<DocPage>> FetchPagesAsync(IReadOnlyCollection<string> pageUrls, CancellationToken ct)
    {
        if (pageUrls.Count == 0)
        {
            return [];
        }

        var bag = new ConcurrentBag<DocPage>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = 20,
            CancellationToken = ct
        };

        try
        {
            await Parallel.ForEachAsync(pageUrls, options, async (url, token) =>
            {
                var page = await TryFetchPageAsync(url, token);
                if (page != null)
                {
                    bag.Add(page);
                }
            });
        }
        catch (OperationCanceledException) when (bag.Count > 0)
        {
        }

        return bag
            .OrderBy(p => p.Url, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<OfficialDocHit> EmptyHits => [];

    private IReadOnlyList<OfficialDocHit> RankPages(string normalizedQuery, IReadOnlyList<string> tokens, int limit)
    {
        if (_pages.Count == 0 || string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return EmptyHits;
        }

        var hits = new List<OfficialDocHit>(_pages.Count);
        foreach (var page in _pages)
        {
            var score = Score(page, normalizedQuery, tokens);
            if (score <= 0d)
            {
                continue;
            }

            var snippet = BuildSnippet(page, normalizedQuery, tokens);
            hits.Add(new OfficialDocHit(page.Title, page.Url, snippet, score));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static double Score(DocPage page, string normalizedQuery, IReadOnlyList<string> tokens)
    {
        var score = 0d;
        if (page.TitleLower.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 12d;
        }

        if (page.TextLower.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 5d;
        }

        if (page.UrlLower.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 3d;
        }

        foreach (var token in tokens)
        {
            if (token.Length <= 1)
            {
                continue;
            }

            if (page.TitleLower.Contains(token, StringComparison.Ordinal))
            {
                score += 4d;
                continue;
            }

            if (page.UrlLower.Contains(token, StringComparison.Ordinal))
            {
                score += 1.5d;
                continue;
            }

            if (page.TextLower.Contains(token, StringComparison.Ordinal))
            {
                score += 1d;
            }
        }

        return score;
    }

    private static string BuildSnippet(DocPage page, string normalizedQuery, IReadOnlyList<string> tokens)
    {
        if (string.IsNullOrWhiteSpace(page.Text))
        {
            return string.Empty;
        }

        var index = page.TextLower.IndexOf(normalizedQuery, StringComparison.Ordinal);
        if (index < 0)
        {
            foreach (var token in tokens)
            {
                if (token.Length <= 1)
                {
                    continue;
                }

                index = page.TextLower.IndexOf(token, StringComparison.Ordinal);
                if (index >= 0)
                {
                    break;
                }
            }
        }

        if (index < 0)
        {
            index = 0;
        }

        var start = Math.Max(0, index - 36);
        const int maxLen = 180;
        var len = Math.Min(maxLen, page.Text.Length - start);
        if (len <= 0)
        {
            return string.Empty;
        }

        var snippet = page.Text.Substring(start, len).Trim();
        if (start > 0)
        {
            snippet = "..." + snippet;
        }

        if (start + len < page.Text.Length)
        {
            snippet += "...";
        }

        return snippet;
    }

    private static string NormalizeForSearch(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = WebUtility.HtmlDecode(text.Trim()).ToLowerInvariant();
        normalized = MultiSpaceRegex.Replace(normalized, " ");
        return normalized;
    }

    private static IReadOnlyList<string> ExtractTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var list = new List<string>(8);
        foreach (Match match in TokenRegex.Matches(text))
        {
            var token = match.Value.Trim().ToLowerInvariant();
            if (token.Length == 0 || list.Contains(token))
            {
                continue;
            }

            list.Add(token);
            if (list.Count >= 24)
            {
                break;
            }
        }

        return list;
    }

    private async Task<IReadOnlyList<string>> LoadSitemapPageUrlsAsync(CancellationToken ct)
    {
        var pending = new Queue<string>();
        var visitedSitemaps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        pending.Enqueue(RootSitemapUrl);
        while (pending.Count > 0 && visitedSitemaps.Count < MaxSitemapDepth)
        {
            var sitemap = pending.Dequeue();
            if (!visitedSitemaps.Add(sitemap))
            {
                continue;
            }

            var xml = await GetStringAsync(sitemap, "application/xml,text/xml;q=0.9,*/*;q=0.8", ct);
            if (string.IsNullOrWhiteSpace(xml))
            {
                continue;
            }

            XDocument document;
            try
            {
                document = XDocument.Parse(xml);
            }
            catch
            {
                continue;
            }

            foreach (var loc in document.Descendants().Where(x => x.Name.LocalName == "loc"))
            {
                var raw = (loc.Value ?? string.Empty).Trim();
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                if (!IsOfficialHost(uri))
                {
                    continue;
                }

                if (raw.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    pending.Enqueue(uri.ToString());
                    continue;
                }

                if (!IsIndexablePageUri(uri))
                {
                    continue;
                }

                pageUrls.Add(NormalizePageUrl(uri));
            }
        }

        foreach (var seed in BuildFallbackSeedPageUrls())
        {
            pageUrls.Add(seed);
        }

        return pageUrls.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<DocPage?> TryFetchPageAsync(string url, CancellationToken ct)
    {
        var html = await GetStringAsync(url, "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", ct);
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var title = ExtractTitle(html);
        var text = ExtractText(html);
        if (string.IsNullOrWhiteSpace(text) || text.Length < 20)
        {
            return null;
        }

        var links = ExtractLinks(url, html);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return new DocPage(NormalizePageUrl(uri), title, text, links);
    }

    private static string ExtractTitle(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var match = TitleRegex.Match(html);
        if (!match.Success)
        {
            return string.Empty;
        }

        var title = WebUtility.HtmlDecode(match.Groups["title"].Value);
        title = MultiSpaceRegex.Replace(title, " ").Trim();
        return title;
    }

    private static string ExtractText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var primary = TryExtractPrimaryContentHtml(html);
        var text = StripHtmlToText(primary);
        if (string.IsNullOrWhiteSpace(text) && !string.Equals(primary, html, StringComparison.Ordinal))
        {
            text = StripHtmlToText(html);
        }

        var metaDescription = ExtractMetaDescription(html);
        if (!string.IsNullOrWhiteSpace(metaDescription) &&
            !text.Contains(metaDescription, StringComparison.OrdinalIgnoreCase))
        {
            text = string.IsNullOrWhiteSpace(text) ? metaDescription : $"{metaDescription} {text}";
        }

        const int maxLen = 18000;
        if (text.Length > maxLen)
        {
            text = text[..maxLen];
        }

        return text;
    }

    private static IReadOnlyList<string> ExtractLinks(string pageUrl, string html)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri) || string.IsNullOrWhiteSpace(html))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var links = new List<string>(32);
        foreach (Match match in AnchorHrefRegex.Matches(html))
        {
            var href = match.Groups["href"].Value.Trim();
            if (string.IsNullOrWhiteSpace(href) ||
                href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!Uri.TryCreate(baseUri, href, out var absoluteUri))
            {
                continue;
            }

            var absolute = absoluteUri.GetLeftPart(UriPartial.Path);
            if (!seen.Add(absolute))
            {
                continue;
            }

            links.Add(absolute);
            if (links.Count >= 80)
            {
                break;
            }
        }

        return links;
    }

    private static async Task<string> GetStringAsync(string url, string accept, CancellationToken ct)
    {
        const int maxAttempts = 2;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("accept", accept);
                request.Headers.TryAddWithoutValidation("accept-language", "zh-CN,zh;q=0.9,en;q=0.8");
                request.Headers.UserAgent.ParseAdd("BetterGI-MCP/1.1");
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko)");
                request.Headers.Referrer = RootSiteUri;
                using var response = await OfficialSiteHttpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    if (attempt + 1 < maxAttempts && IsRetryableStatus(response.StatusCode))
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(120), ct).ConfigureAwait(false);
                        continue;
                    }

                    return string.Empty;
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body) && attempt + 1 < maxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(80), ct).ConfigureAwait(false);
                    continue;
                }

                return body;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt + 1 < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt + 1 < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(120), ct).ConfigureAwait(false);
            }
        }

        return string.Empty;
    }

    private static bool IsRetryableStatus(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.RequestTimeout ||
               statusCode == HttpStatusCode.TooManyRequests ||
               statusCode == HttpStatusCode.InternalServerError ||
               statusCode == HttpStatusCode.BadGateway ||
               statusCode == HttpStatusCode.ServiceUnavailable ||
               statusCode == HttpStatusCode.GatewayTimeout;
    }

    private static string StripHtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = ScriptTagRegex.Replace(html, " ");
        text = StyleTagRegex.Replace(text, " ");
        text = NoScriptTagRegex.Replace(text, " ");
        text = HtmlTagRegex.Replace(text, " ");
        text = WebUtility.HtmlDecode(text);
        text = MultiSpaceRegex.Replace(text, " ").Trim();
        return text;
    }

    private static string TryExtractPrimaryContentHtml(string html)
    {
        var best = string.Empty;
        foreach (Match match in MainTagRegex.Matches(html))
        {
            var content = match.Groups["content"].Value;
            if (content.Length > best.Length)
            {
                best = content;
            }
        }

        foreach (Match match in ArticleTagRegex.Matches(html))
        {
            var content = match.Groups["content"].Value;
            if (content.Length > best.Length)
            {
                best = content;
            }
        }

        return string.IsNullOrWhiteSpace(best) ? html : best;
    }

    private static string ExtractMetaDescription(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        foreach (Match match in MetaDescriptionRegex.Matches(html))
        {
            var content = match.Groups["content"].Value;
            if (string.IsNullOrWhiteSpace(content))
            {
                continue;
            }

            var normalized = MultiSpaceRegex.Replace(WebUtility.HtmlDecode(content).Trim(), " ");
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        return string.Empty;
    }

    private static string NormalizePageUrl(Uri uri)
    {
        if (uri.AbsolutePath == "/")
        {
            return $"{uri.GetLeftPart(UriPartial.Authority)}/";
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static HashSet<string> BuildLinkExpansionUrls(IEnumerable<DocPage> pages, IEnumerable<string> existingUrls)
    {
        var existing = new HashSet<string>(existingUrls, StringComparer.OrdinalIgnoreCase);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var page in pages.Take(MaxLinkExpansionSources))
        {
            foreach (var link in page.Links)
            {
                if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
                {
                    continue;
                }

                if (!IsIndexablePageUri(uri))
                {
                    continue;
                }

                var normalized = NormalizePageUrl(uri);
                if (existing.Contains(normalized))
                {
                    continue;
                }

                candidates.Add(normalized);
                if (candidates.Count >= MaxIndexedPageCount)
                {
                    return candidates;
                }
            }
        }

        return candidates;
    }

    private static IReadOnlyList<string> BuildFallbackSeedPageUrls()
    {
        return
        [
            "https://www.bettergi.com/",
            "https://www.bettergi.com/docs",
            "https://www.bettergi.com/docs/zh-cn/",
            "https://www.bettergi.com/docs/en-us/",
            "https://www.bettergi.com/feats/",
            "https://www.bettergi.com/download/",
            "https://www.bettergi.com/changelog/"
        ];
    }

    private static bool IsIndexablePageUri(Uri uri)
    {
        if (!IsOfficialHost(uri))
        {
            return false;
        }

        var path = uri.AbsolutePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return true;
        }

        foreach (var ext in IgnoreFileExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOfficialHost(Uri uri)
    {
        if (uri == null)
        {
            return false;
        }

        if (!uri.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(uri.Host, RootSiteUri.Host, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".bettergi.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDownloadLikeLink(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var lower = url.ToLowerInvariant();
        return lower.Contains("download", StringComparison.Ordinal) ||
               lower.Contains("release", StringComparison.Ordinal) ||
               lower.EndsWith(".exe", StringComparison.Ordinal) ||
               lower.EndsWith(".msi", StringComparison.Ordinal) ||
               lower.EndsWith(".zip", StringComparison.Ordinal) ||
               lower.EndsWith(".7z", StringComparison.Ordinal) ||
               lower.Contains("github.com", StringComparison.Ordinal);
    }

    private static bool ContainsAnyKeyword(string text, params string[] keywords)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var keyword in keywords)
        {
            if (!string.IsNullOrWhiteSpace(keyword) && text.Contains(keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed class DocPage
    {
        public DocPage(string url, string title, string text, IReadOnlyList<string> links)
        {
            Url = url;
            Title = string.IsNullOrWhiteSpace(title) ? url : title;
            Text = text;
            Links = links;
            UrlLower = Url.ToLowerInvariant();
            TitleLower = Title.ToLowerInvariant();
            TextLower = Text.ToLowerInvariant();
        }

        public string Url { get; }
        public string Title { get; }
        public string Text { get; }
        public IReadOnlyList<string> Links { get; }
        public string UrlLower { get; }
        public string TitleLower { get; }
        public string TextLower { get; }
    }
}

internal sealed record OfficialDocHit(string Title, string Url, string Snippet, double Score);

internal sealed record OfficialDownloadLink(string Url, string PageTitle, string PageUrl);
