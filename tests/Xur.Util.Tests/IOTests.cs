using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Xur.IO;
using Xur.Util;

namespace Xur.Util.Tests;

static class IOTests
{
    sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; return send(request, token); }
    }
    sealed class BrokenRead : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => throw new IOException("broken source");
    }
    sealed class StalledRead : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.Infinite, token); return 0; }
    }
    sealed class BrokenWrite : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => throw new IOException("broken destination");
    }
    // Real HTTP response bodies, including truncated Content-Length framing.
    sealed class Server : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(10));
        readonly Task serving;
        public List<string> Requests { get; } = [];
        public string Url { get; }
        public Server(params string[] responses)
        {
            listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/asset";
            serving = Task.Run(async () =>
            {
                foreach (var response in responses)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    await using var stream = client.GetStream();
                    var header = new StringBuilder(); var next = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(next, stop.Token) == 0) throw new IOException("Missing request headers");
                        header.Append((char)next[0]);
                    }
                    Requests.Add(header.ToString());
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response), stop.Token);
                }
            });
        }
        public Task Complete() => serving;
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            try { await serving; } catch (Exception e) when (e is OperationCanceledException or SocketException) { }
            stop.Dispose();
        }
    }

    public static async Task Run()
    {
        var data = Encoding.UTF8.GetBytes("The transferred bytes, including Unicode: café 🚀");
        using var source = new MemoryStream(data); using var destination = new MemoryStream();
        var receipt = await StreamTransfer.Copy(source, destination, data.Length);
        Verify.That(receipt.Bytes == data.Length && receipt.Sha256 == Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant() && destination.ToArray().SequenceEqual(data), "TeeForge hashes exactly the transferred bytes in one pass");
        Verify.That(source.CanRead && destination.CanWrite, "Copying leaves caller streams open");
        await Verify.Reject(() => StreamTransfer.Copy(new MemoryStream(data), new MemoryStream(), data.Length - 1), "Transfer rejects an overlong response before accepting a digest");
        using var empty = new MemoryStream();
        Verify.That((await StreamTransfer.Copy(empty, new MemoryStream(), 0)).Bytes == 0, "Empty transfers produce a complete receipt");
        await Verify.Reject(() => StreamTransfer.Copy(new BrokenRead(), new MemoryStream(), 100), "Source failure has no successful transfer receipt", "Source read failed");
        await Verify.Reject(() => StreamTransfer.Copy(new StalledRead(), new MemoryStream(), 100, TimeSpan.FromMilliseconds(20)), "Stalled reads have a bounded idle timeout", "timed out");
        try { await StreamTransfer.Copy(new MemoryStream(data), new BrokenWrite(), 100); throw new Exception("Destination error was accepted"); }
        catch (IOException error) { Verify.That(error is not SourceReadException && error.GetBaseException().Message == "broken destination", "Destination errors cannot be mistaken for retryable source faults"); }
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
            await Verify.Reject(() => StreamTransfer.Copy(new StalledRead(), new MemoryStream(), 100, cancellationToken: cancel.Token), "Caller cancellation interrupts body reads");

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete })
        {
            var calls = 0;
            var inner = new Handler((request, _) =>
            {
                Verify.That(request.Headers.Authorization?.Parameter == "secret" && request.Headers.Contains("X-Identity"), "Retries preserve request headers without logging credentials");
                return Task.FromResult(new HttpResponseMessage(++calls <= 2 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
            });
            using var http = new HttpClient(new HttpRetryHandler(inner, baseDelay: TimeSpan.Zero));
            using var request = new HttpRequestMessage(method, "http://localhost/status");
            request.Headers.Authorization = new("Bearer", "secret"); request.Headers.Add("X-Identity", "fixture");
            using var response = await http.SendAsync(request);
            Verify.That(calls == (method == HttpMethod.Get || method == HttpMethod.Head ? 3 : 1), "Only read operations retry: " + method);
        }
        foreach (var code in new[] { 401, 403, 404, 408, 429, 500, 502, 503, 504 })
        {
            var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)code)));
            using var http = new HttpClient(new HttpRetryHandler(handler, baseDelay: TimeSpan.Zero));
            using var response = await http.GetAsync("http://localhost/status");
            Verify.That(handler.Calls == (code is 401 or 403 or 404 ? 1 : 3), "HTTP status has a bounded retry policy: " + code);
        }
        foreach (var kind in new[] { HttpRequestError.ConnectionError, HttpRequestError.SecureConnectionError })
        {
            var handler = new Handler((_, _) => throw new HttpRequestException(kind, "transport"));
            using var http = new HttpClient(new HttpRetryHandler(handler, baseDelay: TimeSpan.Zero));
            try { await http.GetAsync("http://localhost/status"); throw new Exception("Transport failure was accepted"); }
            catch (HttpRequestException) { }
            Verify.That(handler.Calls == (kind == HttpRequestError.ConnectionError ? 3 : 1), "Connection faults retry; certificate/TLS faults fail immediately");
        }
        {
            var handler = new Handler((_, _) =>
            { var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromHours(1)); return Task.FromResult(response); });
            using var http = new HttpClient(new HttpRetryHandler(handler)); using var response = await http.GetAsync("http://localhost/status");
            Verify.That(handler.Calls == 1, "A long Retry-After is returned without retrying earlier than requested");
        }
        {
            var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            using var http = new HttpClient(new HttpRetryHandler(handler)); using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
            await Verify.Reject(async () => { using var response = await http.GetAsync("http://localhost/status", cancel.Token); }, "Cancellation stops retry backoff");
            Verify.That(handler.Calls == 1, "A cancelled backoff sends no extra request");
        }
        {
            var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
            using var http = new HttpClient(new HttpRetryHandler(handler, baseDelay: TimeSpan.Zero));
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/status") { Content = new StringContent("body") };
            using var response = await http.SendAsync(request);
            Verify.That(handler.Calls == 1, "GET with a body is not replayed");
        }
        using var fixture = new Fixture(); var downloads = new Downloads();
        await using (var server = new Server("HTTP/1.1 503 Service Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", "HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\npar", "HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\ncomplete"))
        {
            fixture.Write("download", "previous staged bytes");
            var downloaded = await downloads.FetchReceipt(server.Url, fixture.PathOf("download"), 8); await server.Complete();
            Verify.That(server.Requests.Count == 3 && File.ReadAllText(fixture.PathOf("download")) == "complete" && downloaded.Transfer.Bytes == 8, "HTTP status and interrupted bodies retry; each body restarts a truncated staging file");
            Verify.That(downloaded.Transfer.Sha256 == DurableFiles.HashFile(fixture.PathOf("download")), "Restarted download receipt matches only the complete final body");
        }
        await using (var server = new Server("HTTP/1.1 302 Found\r\nLocation: http://example.invalid/payload\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"))
        {
            await Verify.Reject(() => downloads.Fetch(server.Url, fixture.PathOf("download"), 10), "Local repository redirects still fail closed", "Untrusted");
            await server.Complete(); Verify.That(server.Requests.Count == 1, "Resilience cannot bypass repository redirect trust");
        }
        await using (var server = new Server("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n9\r\ntoo large\r\n0\r\n\r\n"))
        {
            await Verify.Reject(() => downloads.Fetch(server.Url, fixture.PathOf("download"), 8), "Chunked responses enforce signed size bounds without retries", "exceeds");
            await server.Complete(); Verify.That(server.Requests.Count == 1, "Size violations are not transport retries");
        }
        var command = await CommandRunner.Run("/usr/bin/printf", new[] { "%s", "literal $(whoami); `id`" });
        Verify.That(Encoding.UTF8.GetString(command.Output) == "literal $(whoami); `id`" && command.ExitCode == 0, "Shared command execution treats arguments as literal data");
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(20)))
            await Verify.Reject(() => CommandRunner.Run("/usr/bin/sleep", ["30"], cancellationToken: cancel.Token), "Command cancellation terminates a running child promptly");
    }
}
