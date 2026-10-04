using System.Net;

namespace Xur.Util;

public sealed record DownloadReceipt(string? ReleaseBase, Xur.IO.TransferReceipt Transfer);

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

    public virtual async Task<string?> Fetch(string url, string path, long limit, CancellationToken cancellationToken = default) =>
        (await FetchReceipt(url, path, limit, cancellationToken)).ReleaseBase;

    public virtual async Task<DownloadReceipt> FetchReceipt(string url, string path, long limit, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await Copy(url, path, limit, cancellationToken); }
            catch (Xur.IO.SourceReadException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * (1 << attempt)), cancellationToken);
                // A new request and truncated staging file restart the complete
                // signed resource. Partial bytes never enter an activation.
            }
        }
    }

    async Task<DownloadReceipt> Copy(string url, string path, long limit, CancellationToken cancellationToken)
    {
        var publicSource = url.StartsWith(GitHub + "/", StringComparison.Ordinal);
        string? releaseBase = null;
        using var client = Xur.IO.HttpClients.Create(TimeSpan.FromSeconds(30));
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
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            Xur.IO.TransferReceipt receipt;
            try { receipt = await Xur.IO.StreamTransfer.Copy(input, output, limit, TimeSpan.FromSeconds(30), cancellationToken); }
            catch (InvalidDataException error) { throw new UserError(error.Message); }
            output.Flush(flushToDisk: true);
            return new(releaseBase, receipt);
        }
        throw new UserError("Too many repository redirects");
    }
}
