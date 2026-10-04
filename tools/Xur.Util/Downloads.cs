using System.Net;

namespace Xur.Util;

public class Downloads
{
    public const string GitHub = "https://github.com/DouglasCleghorn/Xur/releases";
    public const string Stable = GitHub + "/download/stable";
    public const string Nightly = GitHub + "/download/nightly";

    public static bool GitHubAsset(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Port != 443) return false;
        return uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" ||
            uri.Host == "github.com" && uri.AbsolutePath.StartsWith("/DouglasCleghorn/Xur/releases/download/", StringComparison.Ordinal);
    }

    public virtual async Task<string?> Fetch(string url, string path, long limit, CancellationToken cancellationToken = default)
    {
        var publicSource = url.StartsWith(GitHub + "/", StringComparison.Ordinal);
        string? releaseBase = null;
        using var handler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("XurUtil/1.0");
        for (var redirects = 0; redirects <= 10; redirects++)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ?? throw new UserError("Missing repository redirect location");
                var next = new Uri(new Uri(url), location).AbsoluteUri;
                if (!publicSource || !GitHubAsset(next)) throw new UserError("Untrusted repository redirect");
                if (new Uri(next).Host == "github.com") releaseBase = next[..next.LastIndexOf('/')];
                url = next;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > limit) throw new UserError("Download exceeds allowed size");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[65536];
            long count = 0;
            while (true)
            {
                // Bound stalled reads without limiting the total time of a large bundle download.
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var length = await input.ReadAsync(buffer, deadline.Token);
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                if (length == 0) break;
                count += length;
                if (count > limit) throw new UserError("Download exceeds allowed size");
                await output.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
            }
            output.Flush(flushToDisk: true);
            return releaseBase;
        }
        throw new UserError("Too many repository redirects");
    }
}
