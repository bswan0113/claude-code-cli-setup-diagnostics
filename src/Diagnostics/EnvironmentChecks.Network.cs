using System.Net;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeLauncher;

internal static partial class EnvironmentChecks
{
    private const int NetworkTimeoutSeconds = 15;
    private static readonly HttpClient NetworkClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static Task<CheckItem[]> ScanNetworkAsync(HttpClient client, CancellationToken token)
    {
        var probes = new List<Task<CheckItem>>
        {
            CheckHostAsync(client, new("https://downloads.claude.ai/claude-code-releases/latest"), true, token),
            CheckHostAsync(client, new("https://claude.ai"), false, token)
        };
        // Probe the default API only when the child shell inherits a key for that endpoint.
        // The GET is anonymous. Never attach the key or call a configured third-party endpoint.
        const string api = "https://api.anthropic.com";
        var baseUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        if (!LauncherProfiles.IsAdditional && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) &&
            (string.IsNullOrWhiteSpace(baseUrl) || string.Equals(baseUrl.Trim().TrimEnd('/'), api, StringComparison.OrdinalIgnoreCase)))
            probes.Add(CheckHostAsync(client, new(api), false, token));
        return Task.WhenAll(probes);
    }

    // Accepting a client also lets acceptance tests supply fixed responses without accessing the network.
    internal static async Task<CheckItem> CheckHostAsync(HttpClient client, Uri uri, bool download, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(NetworkTimeoutSeconds));
        CheckItem Result(string key, CheckState state, string? observation = null) =>
            new(7, key, state, observation is null ? uri.Host : $"{uri.Host}\n{observation}", Target: uri.Host);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var observed = $"HTTP {status}";

            if (!download)
            {
                // A response from the root proves HTTP reachability only. It says nothing about credentials.
                if (status == 403 && uri.Host == "claude.ai")
                    return Result("NetWebProbeForbidden", CheckState.Info, observed);
                return status is 403 or 407 or 429 or >= 500
                    ? Result("NetHttpWarning", CheckState.Warning, observed)
                    : Result("NetReachable", CheckState.Pass, observed);
            }

            if (status == 403)
                return Result("NetDownloadForbidden", CheckState.Failed, observed);

            if (status == 200)
            {
                var sample = new byte[8192];
                using var body = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                var length = await body.ReadAtLeastAsync(sample.AsMemory(), sample.Length,
                    throwOnEndOfStream: false, cancellationToken: deadline.Token).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(sample, 0, length);
                if (text.Contains("unavailable in region", StringComparison.OrdinalIgnoreCase))
                    return Result("NetRegion", CheckState.Failed, observed);
                if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html",
                        StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("<!doctype", StringComparison.OrdinalIgnoreCase))
                    return Result("NetDownloadHtml", CheckState.Failed, observed);

                if (Regex.IsMatch(text.Trim().TrimStart('\uFEFF'), @"^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$",
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                    return Result("NetDownloadReady", CheckState.Pass, observed);

                return Result("NetDownloadUnexpected", CheckState.Warning, observed);
            }
            return Result("NetDownloadUnexpected", CheckState.Warning, observed);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return Result("NetTimeout", CheckState.Failed, $"{NetworkTimeoutSeconds} s");
        }
        catch (HttpRequestException ex) when (!token.IsCancellationRequested)
        {
            var code = ex.HttpRequestError;
            var key = code switch
            {
                HttpRequestError.SecureConnectionError => "NetTls",
                HttpRequestError.NameResolutionError => "NetDns",
                HttpRequestError.ProxyTunnelError => "NetProxy",
                _ when ex.InnerException is AuthenticationException => "NetTls",
                _ => "NetTransport"
            };
            return Result(key, CheckState.Failed, code.ToString());
        }
        catch (IOException) when (!token.IsCancellationRequested)
        {
            return Result("NetTransport", CheckState.Failed);
        }
    }
}
