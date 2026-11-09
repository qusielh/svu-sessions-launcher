using System.IO;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;
using SvuSessionsLauncher.Models;

namespace SvuSessionsLauncher.Services;

/// <summary>
/// Live client for the SVU WebDemo SOAP service (RPC/encoded SOAP 1.1).
/// The published WSDL is at
/// <c>https://svuis.svuonline.org/SVUIS/WebDemo_wservice/NewSessions2.php?wsdl</c>.
///
/// The service does not use WS-Security; credentials are passed as ordinary
/// method arguments. They are held in memory for the authenticated session
/// only (used by <c>getSessionsByProgCourse</c>, <c>getTodaySessions</c> and
/// <c>isUserTrainer</c>) and are never persisted by this class.
/// </summary>
public sealed class SoapWebDemoService : IWebDemoService
{
    private const string Ns = "https://svuis.svuonline.org/SVUIS/WebDemo_wservice/";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    private readonly HttpClient _http;
    private readonly string _endpoint;

    private string? _user;
    private string? _pass;

    public SoapWebDemoService(string endpoint)
    {
        _endpoint = endpoint;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ----------------------------------------------------------------- calls

    public async Task<AuthResult> CheckUserPasswordAsync(string username, string password, CancellationToken ct = default)
    {
        var body = await CallAsync("checkUserPass",
            Args(String("userName", username), String("passWord", password)), ct);

        var raw = Element(body, "checkUserPassResult")?.Value ?? string.Empty;
        var result = Interpret(raw);
        if (result.Success)
        {
            _user = username;
            _pass = password;
        }

        return result;
    }

    public async Task<int> IsUserTrainerAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var body = await CallAsync("isUserTrainer",
            Args(String("userName", _user!), String("passWord", _pass!)), ct);
        return ParseInt(Element(body, "return")?.Value);
    }

    public async Task<IReadOnlyList<ProgramInfo>> GetProgramsAsync(CancellationToken ct = default)
    {
        var body = await CallAsync("getPrograms", [], ct);
        var array = Element(body, "getProgramsResult");
        return ParseArray(array, item =>
            new ProgramInfo(ParseInt(Local(item, "programID")), Local(item, "programCode") ?? string.Empty));
    }

    public async Task<IReadOnlyList<CourseInfo>> GetCoursesAsync(int programId, CancellationToken ct = default)
    {
        var body = await CallAsync("getCourses", Args(String("programID", programId.ToString())), $"p{programId}", ct);
        var array = Element(body, "getCoursesResult");
        return ParseArray(array, item =>
            new CourseInfo(ParseInt(Local(item, "courseID")), Local(item, "courseCode") ?? string.Empty));
    }

    public async Task<IReadOnlyList<SessionInfo>> GetSessionsAsync(int programId, int courseId, int option = 0, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var body = await CallAsync("getSessionsByProgCourse",
            Args(
                String("userName", _user!),
                String("passWord", _pass!),
                Int("option", option),
                Int("programID", programId),
                Int("courseID", courseId)),
            $"opt{option}_p{programId}_c{courseId}", ct);

        return ParseSessions(Element(body, "return"));
    }

    public async Task<IReadOnlyList<SessionInfo>> GetTodaySessionsAsync(int option = 0, CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var body = await CallAsync("getTodaySessions",
            Args(String("userName", _user!), String("passWord", _pass!), Int("option", option)),
            $"opt{option}", ct);
        return ParseSessions(Element(body, "return"));
    }

    /// <summary>
    /// Not exposed by the current WSDL (the original launcher called a second
    /// endpoint for this). Returns 0 until SVU publishes the method.
    /// </summary>
    public Task<int> GetMeetingLaunchCountAsync(string launchedDate, int serverNumber, CancellationToken ct = default)
        => Task.FromResult(0);

    /// <summary>
    /// Sweeps the read methods across the <c>option</c> values and records each
    /// raw reply. These are the experiments that reveal the real contract.
    /// </summary>
    public async Task DiagnosticsAsync(CancellationToken ct = default)
    {
        EnsureAuthenticated();
        var user = String("userName", _user!);
        var pass = String("passWord", _pass!);

        foreach (var option in new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })
        {
            await SafeCallAsync("getTodaySessions",
                Args(user, pass, Int("option", option)), $"opt{option}", ct);

            await SafeCallAsync("getSessionsByProgCourse",
                Args(user, pass, Int("option", option), Int("programID", 0), Int("courseID", 0)),
                $"opt{option}_p0_c0", ct);
        }

        await SafeCallAsync("getTodayTraining", Args(user, pass), "all", ct);
    }

    // ---------------------------------------------------------------- parsing

    private static IReadOnlyList<SessionInfo> ParseSessions(XElement? array)
    {
        var items = array?.Elements().Where(e => e.Name.LocalName is "item") ?? [];
        var list = new List<SessionInfo>();
        foreach (var item in items)
        {
            var name = Local(item, "sessionName") ?? string.Empty;

            // The service emits pseudo-rows as its error channel: "Not Found
            // <user>" for unknown accounts and "Invalid Option" for bad filter
            // values. Treat both as "no data".
            if (name.StartsWith("Not Found", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Invalid Option", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            list.Add(new SessionInfo
            {
                SessionId = ParseInt(Local(item, "sessionID")),
                StudentUrl = Local(item, "studentURL") ?? string.Empty,
                SessionName = name,
                Status = Local(item, "status") ?? string.Empty,
                StartDate = ParseDate(Local(item, "startDate")),
                LaunchedDate = Local(item, "launchedDate") ?? string.Empty,
                ServerNumber = ParseInt(Local(item, "serverNumber")),
                TutorId = ParseInt(Local(item, "tutorID")),
                TutorName = Local(item, "tutorName") ?? string.Empty,
                ClassId = ParseInt(Local(item, "classID"))
            });
        }

        return list;
    }

    private static List<T> ParseArray<T>(XElement? array, Func<XElement, T> map)
    {
        var result = new List<T>();
        foreach (var item in array?.Elements().Where(e => e.Name.LocalName == "item") ?? [])
        {
            result.Add(map(item));
        }

        return result;
    }

    private static AuthResult Interpret(string? raw)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return AuthResult.Fail("login.error.invalid");
        }

        var low = text.ToLowerInvariant();
        if (low is "false" or "0" or "no" or "invalid" or "error" or "null" or "fail" or "failed")
        {
            return AuthResult.Fail("login.error.invalid");
        }

        // Any other non-empty value (a name, "true", "1", ...) is treated as a
        // successful sign-in; the raw value is surfaced so unexpected responses
        // are easy to spot during testing.
        return AuthResult.Ok(text);
    }

    private static string? Local(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static XElement? Element(XElement parent, string localName) =>
        parent.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    private static int ParseInt(string? value) =>
        int.TryParse(value, out var n) ? n : 0;

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParse(value, out var d) ? d : null;

    // -------------------------------------------------------------- transport

    private static XElement String(string name, string value) =>
        new(name, new XAttribute(Xsi + "type", "xsd:string"), value ?? string.Empty);

    private static XElement Int(string name, int value) =>
        new(name, new XAttribute(Xsi + "type", "xsd:int"), value);

    private static IEnumerable<XElement> Args(params XElement[] args) => args;

    private async Task<XElement> CallAsync(string method, IEnumerable<XElement> args, CancellationToken ct)
        => await CallAsync(method, args, string.Empty, ct);

    private async Task<XElement> CallAsync(string method, IEnumerable<XElement> args, string tag, CancellationToken ct)
    {
        var envelope = new XDocument(
            new XElement(Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", Soap),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                new XAttribute(XNamespace.Xmlns + "xsd", Xsd),
                new XElement(Soap + "Body",
                    new XElement(XName.Get(method, Ns), args))));

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{Ns}{method}\"");

        string text;
        try
        {
            using var response = await _http.SendAsync(request, ct);
            text = await response.Content.ReadAsStringAsync(ct);
            DumpResponse(method, tag, text);
            if (!response.IsSuccessStatusCode)
            {
                throw new WebDemoServiceException($"HTTP {(int)response.StatusCode}");
            }
        }
        catch (WebDemoServiceException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WebDemoServiceException("login.error.network", ex);
        }

        XDocument doc;
        try
        {
            doc = XDocument.Parse(text);
        }
        catch (Exception ex)
        {
            throw new WebDemoServiceException("login.error.network", ex);
        }

        var body = doc.Root?.Element(Soap + "Body");
        if (body is null)
        {
            throw new WebDemoServiceException("login.error.network");
        }

        var fault = body.Element(Soap + "Fault");
        if (fault is not null)
        {
            throw new WebDemoServiceException(fault.Value.Trim());
        }

        return body;
    }

    /// <summary>Calls a method for diagnostics, ignoring faults (they are dumped).</summary>
    private async Task SafeCallAsync(string method, IEnumerable<XElement> args, string tag, CancellationToken ct)
    {
        try
        {
            await CallAsync(method, args, tag, ct);
        }
        catch
        {
            // The raw reply (including faults) has already been recorded.
        }
    }

    private static readonly Lazy<string> ResponseDir = new(() =>
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SVU Sessions Launcher", "responses");
        Directory.CreateDirectory(dir);
        return dir;
    });

    /// <summary>
    /// Saves the raw SOAP response for analysis. Only the <b>response</b> is
    /// written — never the request, which carries the password. This is how we
    /// inspect the exact field names and values the server actually returns.
    /// </summary>
    private static void DumpResponse(string method, string tag, string text)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var suffix = string.IsNullOrWhiteSpace(tag) ? string.Empty : $"_{tag}";
            File.WriteAllText(Path.Combine(ResponseDir.Value, $"{method}{suffix}_{stamp}.xml"), text);
        }
        catch
        {
            // Diagnostics must never break the data path.
        }
    }

    private void EnsureAuthenticated()
    {
        if (string.IsNullOrEmpty(_user) || _pass is null)
        {
            throw new WebDemoServiceException("login.error.invalid");
        }
    }
}
