using System.Net;
using System.Security.Authentication;
using NjuPrepaidStatus.Models;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace NjuPrepaidStatus.Services;

public sealed class NjuWebClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly FileLogger _logger;

    public NjuWebClient(FileLogger logger)
    {
        _logger = logger;
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Gecko/20100101 Firefox/52.0");
    }

    public async Task<string> GetBalanceHtmlAsync(Credentials credentials, CancellationToken cancellationToken)
    {
        await LoginAsync(credentials, cancellationToken);
        try
        {
            return await FetchBalanceHtmlAsync(cancellationToken);
        }
        finally
        {
            await TryLogoutAsync(cancellationToken);
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private async Task LoginAsync(Credentials credentials, CancellationToken cancellationToken)
    {
        const string loginUrl = "https://www.njumobile.pl/logowanie";
        _logger.LogHttpRequest(HttpMethod.Get, loginUrl, BuildDefaultHeadersForLog());
        using var loginResponse = await _httpClient.GetAsync(loginUrl, cancellationToken);
        var loginSite = await loginResponse.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogHttpResponse(loginUrl, (int)loginResponse.StatusCode, BuildResponseHeadersForLog(loginResponse), loginSite);
        var sessionNo = ExtractDynSessConf(loginSite);

        var payload = new Dictionary<string, string>
        {
            ["_dyncharset"] = "UTF-8",
            ["_dynSessConf"] = sessionNo,
            ["/ptk/sun/login/formhandler/LoginFormHandler.backUrl"] = "",
            ["_D:/ptk/sun/login/formhandler/LoginFormHandler.backUrl"] = "",
            ["/ptk/sun/login/formhandler/LoginFormHandler.hashMsisdn"] = "",
            ["_D:/ptk/sun/login/formhandler/LoginFormHandler.hashMsisdn"] = "",
            ["phone-input"] = credentials.Username,
            ["_D:phone-input"] = "",
            ["password-form"] = credentials.Password,
            ["_D:password-form"] = "",
            ["login-submit"] = "zaloguj sie",
            ["_D:login-submit"] = "",
            ["_DARGS"] = "/profile-processes/login/login.jsp.portal-login-form"
        };

        using var content = new FormUrlEncodedContent(payload);
        var requestBody = await content.ReadAsStringAsync(cancellationToken);
        _logger.LogHttpRequest(HttpMethod.Post, loginUrl, BuildDefaultHeadersForLog(), requestBody);
        using var response = await _httpClient.PostAsync(
            loginUrl,
            content,
            cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogHttpResponse(loginUrl, (int)response.StatusCode, BuildResponseHeadersForLog(response), html);
        EnsureLoginSuccessful(html);
    }

    private async Task<string> FetchBalanceHtmlAsync(CancellationToken cancellationToken)
    {
        const string url = "https://www.njumobile.pl/ecare-infoservices/ajax?group=home-alerts-state-funds&toGet=home-alerts-state-funds&toUpdate=state-funds-infoservices&pageId=7800013&actionUrl=%2Fmojekonto%2Fstan-konta&parentUrl=%2Fmojekonto&isMobile=false";

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.Referrer = new Uri("https://www.njumobile.pl/mojekonto/stan-konta");
            _logger.LogHttpRequest(HttpMethod.Post, url, BuildRequestHeadersForLog(request));

            var response = await _httpClient.SendAsync(request, cancellationToken);
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogHttpResponse(url, (int)response.StatusCode, BuildResponseHeadersForLog(response), html);
            if (LooksLikeBalanceView(html))
            {
                return html;
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }

        throw new InvalidOperationException("Could not fetch valid balance view.");
    }

    private async Task TryLogoutAsync(CancellationToken cancellationToken)
    {
        const string logoutUrl = "https://www.njumobile.pl/?_DARGS=/core/v3.0/navigation/account_navigation.jsp.portal-logout-form";
        var payload = new Dictionary<string, string>
        {
            ["_dyncharset"] = "UTF-8",
            ["logout-submit"] = "wyloguj sie",
            ["_D:logout-submit"] = "",
            ["_DARGS"] = "/core/v3.0/navigation/account_navigation.jsp.portal-logout-form"
        };

        try
        {
            using var content = new FormUrlEncodedContent(payload);
            var requestBody = await content.ReadAsStringAsync(cancellationToken);
            _logger.LogHttpRequest(HttpMethod.Post, logoutUrl, BuildDefaultHeadersForLog(), requestBody);
            using var response = await _httpClient.PostAsync(
                logoutUrl,
                content,
                cancellationToken);
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogHttpResponse(logoutUrl, (int)response.StatusCode, BuildResponseHeadersForLog(response), html);
        }
        catch
        {
            // Best effort logout only.
        }
    }

    private static string ExtractDynSessConf(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var node = doc.DocumentNode.SelectSingleNode("//input[@name='_dynSessConf']");
        var value = node?.GetAttributeValue("value", null);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("Login page does not contain _dynSessConf.");
        }

        return value;
    }

    private static void EnsureLoginSuccessful(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var loggedNoNode = doc.DocumentNode.SelectSingleNode("//li[contains(@class,'title-dashboard-summary')]");
        if (loggedNoNode is null)
        {
            throw new AuthenticationException("Login failed. Verify phone number and password.");
        }
    }

    private static bool LooksLikeBalanceView(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        return html.Contains("okresu rozliczeniowego", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("dane okresu rozliczeniowego", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("promocyjny pakiet danych", StringComparison.OrdinalIgnoreCase) ||
               html.Contains("small-comment", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildDefaultHeadersForLog()
    {
        return
        [
            new KeyValuePair<string, string>("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Gecko/20100101 Firefox/52.0")
        ];
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildRequestHeadersForLog(HttpRequestMessage request)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var header in request.Headers)
        {
            result.Add(new KeyValuePair<string, string>(header.Key, string.Join("; ", header.Value)));
        }
        return result;
    }

    private static IEnumerable<KeyValuePair<string, string>> BuildResponseHeadersForLog(HttpResponseMessage response)
    {
        var headers = response.Headers
            .SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v)))
            .ToList();
        headers.AddRange(response.Content.Headers.SelectMany(h => h.Value.Select(v => new KeyValuePair<string, string>(h.Key, v))));
        return headers;
    }
}
