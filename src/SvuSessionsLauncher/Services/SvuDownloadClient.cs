using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace SvuSessionsLauncher.Services;

/// <summary>One option from a WebForms dropdown (the submitted value + its label).</summary>
public sealed record SvuOption(string Value, string Label)
{
    // The custom ComboBox template does not push DisplayMemberPath through to the
    // closed selection box, so render the label here as well.
    public override string ToString() => Label;
}

/// <summary>One row of the sessions grid (GridView_results).</summary>
public sealed class SvuSession
{
    public int Index { get; init; }
    public string Program { get; init; } = "";
    public string Course { get; init; } = "";
    public string Class { get; init; } = "";
    public string Term { get; init; } = "";
    public string Order { get; init; } = "";
    public string Tutor { get; init; } = "";
    public string Date { get; init; } = "";

    /// <summary>
    /// The dropdown value of the class this row belongs to. Selecting the row for
    /// download needs the class to be re-applied first, so a combined "all
    /// classes" list can pull rows that came from different classes.
    /// </summary>
    public string ClassValue { get; set; } = "";
}

/// <summary>The parsed state of a single page response.</summary>
public sealed class SvuPage
{
    public Dictionary<string, List<SvuOption>> Dropdowns { get; } = new(StringComparer.Ordinal);
    public List<SvuSession> Sessions { get; } = new();
    public bool HasSessionsGrid { get; set; }
    public List<string> FileLinks { get; } = new();
}

public enum DownloadOutcome
{
    Completed,
    Cancelled,
    Failed
}

public readonly record struct DownloadProgress(long Received, long Total, int Attempt);

/// <summary>
/// Drives the university's official ASP.NET WebForms "Download Sessions" page
/// (http://sessions.svuonline.org/new/) with plain GET/POST postbacks and
/// scrapes the returned HTML. There is no JSON/REST API and no authentication:
/// the flow is Term -> Program -> Course -> Tutor -> Class -> session row -> file.
/// </summary>
public sealed class SvuDownloadClient : IDisposable
{
    public const string BaseUrl = "http://sessions.svuonline.org/new/";

    public const string TermControl = "DropDownList_Term";
    public const string ProgramControl = "DropDownList_Program";
    public const string CourseControl = "DropDownList_Course";
    public const string TutorControl = "DropDownList_Tutor";
    public const string ClassControl = "DropDownList_Class";
    public const string GridControl = "GridView_results";

    private static readonly string[] DropdownIds =
    {
        TermControl, ProgramControl, CourseControl, TutorControl, ClassControl
    };

    private static readonly string[] TokenNames =
    {
        "__VIEWSTATE", "__VIEWSTATEGENERATOR", "__EVENTVALIDATION", "__VIEWSTATEENCRYPTED"
    };

    private static readonly Regex InputTagRegex =
        new("<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OptionRegex =
        new("<option\\b(?<attrs>[^>]*)>(?<text>.*?)</option>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex RowRegex =
        new("<tr\\b[^>]*>(?<body>.*?)</tr>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CellRegex =
        new("<t[dh]\\b[^>]*>(?<body>.*?)</t[dh]>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex LinkRegex =
        new("href\\s*=\\s*[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagStripRegex =
        new("<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex =
        new("\\s+", RegexOptions.Compiled);

    private const int MaxAttempts = 5;

    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public SvuDownloadClient()
    {
        var handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        _http.DefaultRequestHeaders.Referrer = new Uri(BaseUrl);

        foreach (var id in DropdownIds)
        {
            _values[id] = "";
        }
    }

    /// <summary>GETs the page once and reads the hidden state + the term list.</summary>
    public async Task<SvuPage> OpenAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));

        using var response = await _http.GetAsync(BaseUrl, cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return Parse(html);
    }

    /// <summary>
    /// POSTs the whole form back, changing exactly one dropdown
    /// (<paramref name="control"/>) to <paramref name="value"/>.
    /// </summary>
    public Task<SvuPage> ChooseAsync(string control, string value, CancellationToken cancellationToken)
    {
        _values[control] = value;
        return PostAsync(BuildForm(control, "", control, value), cancellationToken);
    }

    /// <summary>POSTs a row selection and returns the page (now carrying the file link).</summary>
    public Task<SvuPage> SelectRowAsync(int index, CancellationToken cancellationToken) =>
        PostAsync(BuildForm(GridControl, "Select$" + index, null, null), cancellationToken);

    /// <summary>The last known value echoed for a dropdown.</summary>
    public string ValueOf(string control) => _values.TryGetValue(control, out var value) ? value : "";

    private async Task<SvuPage> PostAsync(List<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(90));

        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(BaseUrl, content, cts.Token).ConfigureAwait(false);
        string html = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"The session archive returned HTTP {(int)response.StatusCode}.");
        }

        return Parse(html);
    }

    private List<KeyValuePair<string, string>> BuildForm(
        string eventTarget, string eventArgument, string? overrideControl, string? overrideValue)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("__EVENTTARGET", eventTarget),
            new("__EVENTARGUMENT", eventArgument),
            new("__LASTFOCUS", "")
        };

        foreach (var token in TokenNames)
        {
            form.Add(new KeyValuePair<string, string>(
                token, _tokens.TryGetValue(token, out var state) ? state : ""));
        }

        foreach (var id in DropdownIds)
        {
            string value = overrideControl == id && overrideValue != null
                ? overrideValue
                : (_values.TryGetValue(id, out var current) ? current : "");
            form.Add(new KeyValuePair<string, string>(id, value));
        }

        return form;
    }

    private SvuPage Parse(string html)
    {
        var page = new SvuPage();

        foreach (Match tag in InputTagRegex.Matches(html))
        {
            string markup = tag.Value;
            string? name = Attr(markup, "name");
            if (name == null)
            {
                continue;
            }

            if (Array.IndexOf(TokenNames, name) >= 0)
            {
                _tokens[name] = Attr(markup, "value") ?? "";
            }
        }

        foreach (var id in DropdownIds)
        {
            var (options, selected, found) = ParseOptions(html, id);
            page.Dropdowns[id] = options;
            if (found)
            {
                _values[id] = selected.Length > 0 ? selected : (options.Count > 0 ? options[0].Value : "");
            }
        }

        ParseGrid(html, page);
        ParseFileLinks(html, page);
        return page;
    }

    private static (List<SvuOption> Options, string Selected, bool Found) ParseOptions(string html, string id)
    {
        var options = new List<SvuOption>();
        var match = Regex.Match(
            html,
            "<select\\b[^>]*name\\s*=\\s*[\"']?" + Regex.Escape(id) + "[\"']?[^>]*>(?<body>.*?)</select>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success)
        {
            return (options, "", false);
        }

        string selected = "";
        foreach (Match option in OptionRegex.Matches(match.Groups["body"].Value))
        {
            string attrs = option.Groups["attrs"].Value;
            string label = Clean(option.Groups["text"].Value);
            string value = Attr(attrs, "value") ?? label;
            if (Regex.IsMatch(attrs, "\\bselected\\b", RegexOptions.IgnoreCase))
            {
                selected = value;
            }

            options.Add(new SvuOption(value, label));
        }

        return (options, selected, true);
    }

    private static void ParseGrid(string html, SvuPage page)
    {
        var table = Regex.Match(
            html,
            "<table\\b[^>]*id\\s*=\\s*[\"']?GridView_results[\"']?[^>]*>(?<body>.*?)</table>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!table.Success)
        {
            return;
        }

        page.HasSessionsGrid = true;
        int fallback = 0;

        foreach (Match row in RowRegex.Matches(table.Groups["body"].Value))
        {
            string rowHtml = row.Groups["body"].Value;
            if (Regex.IsMatch(rowHtml, "<th\\b", RegexOptions.IgnoreCase))
            {
                continue;
            }

            var cells = new List<string>();
            foreach (Match cell in CellRegex.Matches(rowHtml))
            {
                cells.Add(Clean(cell.Groups["body"].Value));
            }

            if (cells.Count < 8)
            {
                continue;
            }

            var select = Regex.Match(rowHtml, "Select\\$(\\d+)");
            int index = select.Success ? int.Parse(select.Groups[1].Value) : fallback;

            page.Sessions.Add(new SvuSession
            {
                Index = index,
                Program = cells[1],
                Course = cells[2],
                Class = cells[3],
                Term = cells[4],
                Order = cells[5],
                Tutor = cells[6],
                Date = cells[7]
            });

            fallback++;
        }
    }

    private static void ParseFileLinks(string html, SvuPage page)
    {
        foreach (Match link in LinkRegex.Matches(html))
        {
            string url = WebUtility.HtmlDecode(link.Groups["url"].Value);
            if (url.IndexOf(".lrec", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                url = new Uri(new Uri(BaseUrl), url).ToString();
            }

            if (!page.FileLinks.Contains(url))
            {
                page.FileLinks.Add(url);
            }
        }
    }

    /// <summary>
    /// Streams one recording to disk, resuming with an HTTP Range request and
    /// retrying a few times so an interrupted connection does not lose progress.
    /// The file is written to a ".part" sidecar and renamed only once complete.
    /// </summary>
    public async Task<DownloadOutcome> DownloadAsync(
        string url, string destinationPath, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(destinationPath) ?? ".";
        Directory.CreateDirectory(directory);
        string part = destinationPath + ".part";

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                long have = File.Exists(part) ? new FileInfo(part).Length : 0;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (have > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(have, null);
                }

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    // The sidecar already holds the whole file.
                    File.Move(part, destinationPath, true);
                    progress?.Report(new DownloadProgress(have, have, attempt));
                    return DownloadOutcome.Completed;
                }

                response.EnsureSuccessStatusCode();

                bool resuming = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (!resuming)
                {
                    have = 0;
                    if (File.Exists(part))
                    {
                        File.Delete(part);
                    }
                }

                long total = have + (response.Content.Headers.ContentLength ?? 0);
                if (total <= 0)
                {
                    total = have;
                }

                progress?.Report(new DownloadProgress(have, total, attempt));

                long done = have;
                {
                    await using var input = await response.Content
                        .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var output = new FileStream(
                        part,
                        have > 0 ? FileMode.Append : FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        1 << 16,
                        useAsync: true);

                    byte[] buffer = new byte[1 << 16];
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                               .ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        done += read;
                        progress?.Report(new DownloadProgress(done, total, attempt));
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(part, destinationPath, true);
                progress?.Report(new DownloadProgress(done, total, attempt));
                return DownloadOutcome.Completed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return DownloadOutcome.Cancelled;
            }
            catch (Exception) when (attempt < MaxAttempts)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return DownloadOutcome.Cancelled;
                }
            }
            catch (Exception)
            {
                return DownloadOutcome.Failed;
            }
        }

        return DownloadOutcome.Failed;
    }

    private static string? Attr(string tag, string name)
    {
        var match = Regex.Match(tag, "\\b" + name + "\\s*=\\s*[\"'](?<value>[^\"']*)[\"']", RegexOptions.IgnoreCase);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : null;
    }

    private static string Clean(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return "";
        }

        string text = TagStripRegex.Replace(html, " ");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace('\u00a0', ' ');
        return WhitespaceRegex.Replace(text, " ").Trim();
    }

    public void Dispose() => _http.Dispose();
}
