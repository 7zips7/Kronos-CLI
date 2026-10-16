using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace KronosCLI;

internal sealed class RedirectProxy(TcpListener listener, CertificateAuthority authority, Options options) : IDisposable
{
    static readonly string[] EpicDomains =
        ["epicgames.com", "epicgames.net", "epicgames.dev", "akamaized.net", "superawesome.com", "cosmo.fdeb.live.use1a.on.epicgames.com"];

    static readonly string[] PassThroughDomains = ["eac-prod.on.epicgames.com"];

    static readonly string[] ContentPaths =
    [
        "/launcher/api/public/distributionpoints",
        "/launcher/api/public/assets/",
        "/Builds/Fortnite/Content/CloudDir/",
        "/ias/fortnite/",
        "/iad/fortnite/",
        "/valkyrie/cooked-content/",
    ];

    static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization", "Proxy-Authenticate",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Expect", "Content-Length",
    };

    static readonly UriCreationOptions RawPath = new() { DangerousDisablePathAndQueryCanonicalization = true };

    const string Established = "HTTP/1.1 200 Connection Established\r\n\r\n";

    readonly CancellationTokenSource stop = new();
    readonly ConcurrentDictionary<string, bool> failedHandshakes = new(StringComparer.OrdinalIgnoreCase);
    readonly HttpClient http = new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ActivityHeadersPropagator = null,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        RequestHeaderEncodingSelector = (_, _) => Encoding.Latin1,
        ResponseHeaderEncodingSelector = (_, _) => Encoding.Latin1,
    })
    {
        Timeout = TimeSpan.FromMinutes(2),
    };

    int backendDown;

    string Backend => options.Backend.TrimEnd('/');

    public static RedirectProxy? Start(Options options)
    {
        if (!IPEndPoint.TryParse(options.Proxy, out var endpoint) || endpoint.Port == 0)
        {
            Log.Error($"Proxy:   {options.Proxy} is not an ip:port to listen on");
            return null;
        }
        if (!Uri.TryCreate(options.Backend, UriKind.Absolute, out _))
        {
            Log.Error($"Proxy:   {options.Backend} is not a backend URL");
            return null;
        }

        CertificateAuthority? authority;
        try
        {
            authority = CertificateAuthority.FromFiddler();
        }
        catch (Exception e)
        {
            Log.Error($"Proxy:   couldn't load the Fiddler root certificate: {e.Message}");
            return null;
        }
        if (authority == null)
        {
            Log.Error(@"Proxy:   no trusted Fiddler root certificate with a private key in CurrentUser\My, so HTTPS can't be redirected");
            return null;
        }

        var listener = new TcpListener(endpoint);
        try
        {
            listener.Start();
        }
        catch (SocketException e)
        {
            authority.Dispose();
            if (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
                Log.Warn($"Proxy:   {options.Proxy} is already taken (Fiddler?) - the game will go through that instead");
            else
                Log.Error($"Proxy:   can't listen at {options.Proxy}: {e.Message}");
            return null;
        }

        var proxy = new RedirectProxy(listener, authority, options);
        _ = proxy.AcceptLoop();
        Log.Ok($"Proxy:   listening at {options.Proxy}");
        return proxy;
    }

    async Task AcceptLoop()
    {
        while (true)
        {
            try
            {
                _ = Serve(await listener.AcceptTcpClientAsync(stop.Token));
            }
            catch (Exception) when (stop.IsCancellationRequested) { return; }
            catch (SocketException) { }
        }
    }

    async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await ServeRequests(client.GetStream(), null);
            }
            catch (Exception e) when (e is IOException or SocketException or InvalidDataException or AuthenticationException
                                          or ObjectDisposedException or OperationCanceledException) { }
            catch (Exception e)
            {
                Log.Detail($"Proxy: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    async Task ServeRequests(Stream stream, string? origin)
    {
        var reader = new HttpReader(stream);
        while (await reader.ReadHead() is { } head)
        {
            var request = HttpRequest.Parse(head);

            if (request.Method == "CONNECT" && origin == null)
            {
                await Connect(reader, request.Target);
                return;
            }

            string url = !request.Target.StartsWith('/') ? request.Target
                : origin != null ? origin + request.Target
                : $"http://{request.Header("Host")}{request.Target}";
            if (!Uri.TryCreate(url, in RawPath, out var target))
            {
                await WriteError(stream, 400, "Bad Request", $"Can't route {request.Target}");
                return;
            }

            if (!await Forward(reader, request, target)) return;
        }
    }

    async Task Connect(HttpReader client, string authorityText)
    {
        if (!Uri.TryCreate($"https://{authorityText}", UriKind.Absolute, out var target))
        {
            await WriteError(client.Stream, 400, "Bad Request", $"Can't connect to {authorityText}");
            return;
        }

        string host = target.DnsSafeHost;
        if (IsEpicHost(host))
        {
            await WriteRaw(client.Stream, Established);
            await using var tls = new SslStream(client.Stream, leaveInnerStreamOpen: true);
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = authority.For(host),
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                });
            }
            catch (Exception e) when (e is AuthenticationException or IOException)
            {
                if (failedHandshakes.TryAdd(host, true))
                    Log.Warn($"Proxy: TLS handshake for {host} failed - does the game trust the Fiddler root? ({e.Message})");
                return;
            }

            await ServeRequests(tls, $"https://{target.Authority}");
            return;
        }

        using var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(host, target.Port, stop.Token);
        }
        catch (SocketException e)
        {
            await WriteError(client.Stream, 502, "Bad Gateway", e.Message);
            return;
        }

        Log.Detail($"Proxy: CONNECT {host}:{target.Port} -> passthrough");
        await WriteRaw(client.Stream, Established);
        await Pipe(client, upstream.GetStream());
    }

    async Task<bool> Forward(HttpReader client, HttpRequest request, Uri target)
    {
        bool redirected = IsEpicHost(target.DnsSafeHost) &&
                          !ContentPaths.Any(path => target.PathAndQuery.Contains(path, StringComparison.Ordinal));
        var destination = redirected ? new Uri(Backend + target.PathAndQuery, in RawPath) : target;
        string route = $"{request.Method} {target.Host}{target.AbsolutePath} -> {(redirected ? "backend" : "upstream")}";

        if (request.Header("Upgrade") != null)
        {
            Log.Detail($"Proxy: {route} (upgrade)");
            await Upgrade(client, request, destination);
            return false;
        }

        if (string.Equals(request.Header("Expect"), "100-continue", StringComparison.OrdinalIgnoreCase))
            await WriteRaw(client.Stream, "HTTP/1.1 100 Continue\r\n\r\n");
        byte[]? body = await client.ReadBody(request);
        bool keepAlive = request.KeepAlive;

        using var message = new HttpRequestMessage(new HttpMethod(request.Method), destination);
        if (body != null) message.Content = new ByteArrayContent(body);
        foreach (var (name, value) in request.Headers)
            if (!SkippedHeaders.Contains(name) && !message.Headers.TryAddWithoutValidation(name, value))
                message.Content?.Headers.TryAddWithoutValidation(name, value);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, stop.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Detail($"Proxy: {route} failed: {e.Message}");
            if (redirected && Interlocked.Exchange(ref backendDown, 1) == 0)
                Log.Warn($"Proxy: backend at {Backend} isn't answering ({e.Message})");
            await WriteError(client.Stream, 502, "Bad Gateway", e.Message);
            return keepAlive;
        }

        using (response)
        {
            if (redirected && Interlocked.Exchange(ref backendDown, 0) == 1)
                Log.Ok($"Proxy: backend at {Backend} is answering again");
            Log.Detail($"Proxy: {route} {(int)response.StatusCode}");
            await WriteResponse(client.Stream, request, response, keepAlive);
        }
        return keepAlive;
    }

    async Task Upgrade(HttpReader client, HttpRequest request, Uri destination)
    {
        using var upstream = new TcpClient();
        try
        {
            await upstream.ConnectAsync(destination.DnsSafeHost, destination.Port, stop.Token);
        }
        catch (SocketException e)
        {
            await WriteError(client.Stream, 502, "Bad Gateway", e.Message);
            return;
        }

        Stream server = upstream.GetStream();
        if (destination.Scheme == Uri.UriSchemeHttps)
        {
            var tls = new SslStream(server);
            await tls.AuthenticateAsClientAsync(destination.DnsSafeHost);
            server = tls;
        }

        await using (server)
        {
            var head = new StringBuilder($"{request.Method} {destination.PathAndQuery} HTTP/1.1\r\nHost: {destination.Authority}\r\n");
            foreach (var (name, value) in request.Headers)
                if (!name.Equals("Host", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                    head.Append($"{name}: {value}\r\n");
            await WriteRaw(server, head.Append("\r\n").ToString());
            await Pipe(client, server);
        }
    }

    static async Task WriteResponse(Stream client, HttpRequest request, HttpResponseMessage response, bool keepAlive)
    {
        int status = (int)response.StatusCode;
        bool hasBody = request.Method != "HEAD" && status is >= 200 and not 204 and not 304;
        long? length = response.Content.Headers.ContentLength;

        var head = new StringBuilder($"HTTP/1.1 {status} {response.ReasonPhrase}\r\n");
        foreach (var header in response.Headers.NonValidated.Concat(response.Content.Headers.NonValidated))
            if (!SkippedHeaders.Contains(header.Key))
                foreach (var value in header.Value)
                    head.Append($"{header.Key}: {value}\r\n");
        if (length != null) head.Append($"Content-Length: {length}\r\n");
        else if (hasBody) head.Append("Transfer-Encoding: chunked\r\n");
        if (!keepAlive) head.Append("Connection: close\r\n");
        await WriteRaw(client, head.Append("\r\n").ToString());

        if (!hasBody) return;
        await using var content = await response.Content.ReadAsStreamAsync();
        if (length != null) await content.CopyToAsync(client);
        else await CopyChunked(content, client);
    }

    static async Task CopyChunked(Stream source, Stream destination)
    {
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await WriteRaw(destination, $"{read:X}\r\n");
            await destination.WriteAsync(buffer.AsMemory(0, read));
            await WriteRaw(destination, "\r\n");
        }
        await WriteRaw(destination, "0\r\n\r\n");
    }

    static async Task Pipe(HttpReader client, Stream server)
    {
        byte[] buffered = client.TakeBuffered();
        if (buffered.Length > 0) await server.WriteAsync(buffered);
        await Task.WhenAny(client.Stream.CopyToAsync(server), server.CopyToAsync(client.Stream));
    }

    static Task WriteError(Stream stream, int status, string reason, string message)
    {
        byte[] body = Encoding.UTF8.GetBytes(message);
        return WriteRaw(stream,
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\n\r\n", body);
    }

    static async Task WriteRaw(Stream stream, string text, byte[]? body = null)
    {
        await stream.WriteAsync(Encoding.Latin1.GetBytes(text));
        if (body != null) await stream.WriteAsync(body);
    }

    static bool IsEpicHost(string host) => InDomains(host, EpicDomains) && !InDomains(host, PassThroughDomains);

    static bool InDomains(string host, string[] domains) =>
        domains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
                              host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        http.Dispose();
        authority.Dispose();
    }
}

internal sealed record HttpRequest(string Method, string Target, string Version, List<KeyValuePair<string, string>> Headers)
{
    public string? Header(string name) =>
        Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    public bool KeepAlive
    {
        get
        {
            string connection = $"{Header("Connection")},{Header("Proxy-Connection")}";
            return Version == "HTTP/1.0"
                ? connection.Contains("keep-alive", StringComparison.OrdinalIgnoreCase)
                : !connection.Contains("close", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static HttpRequest Parse(string head)
    {
        string[] lines = head.Split("\r\n");
        string[] parts = lines[0].Split(' ');
        if (parts.Length != 3) throw new InvalidDataException($"Bad request line: {lines[0]}");

        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0) headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        return new HttpRequest(parts[0], parts[1], parts[2], headers);
    }
}

internal sealed class HttpReader(Stream stream)
{
    static readonly byte[] LineEnd = "\r\n"u8.ToArray();
    static readonly byte[] HeadEnd = "\r\n\r\n"u8.ToArray();

    readonly byte[] buffer = new byte[64 * 1024];
    int start, end;

    public Stream Stream => stream;

    public Task<string?> ReadHead() => ReadUntil(HeadEnd);

    public async Task<byte[]?> ReadBody(HttpRequest request)
    {
        if (request.Header("Transfer-Encoding")?.Contains("chunked", StringComparison.OrdinalIgnoreCase) == true)
            return await ReadChunked();
        return int.TryParse(request.Header("Content-Length"), out int length) ? await ReadBytes(length) : null;
    }

    public byte[] TakeBuffered()
    {
        byte[] taken = buffer[start..end];
        start = end = 0;
        return taken;
    }

    async Task<byte[]> ReadChunked()
    {
        using var body = new MemoryStream();
        while (true)
        {
            string line = await ReadUntil(LineEnd) ?? throw new IOException("Connection closed mid-body");
            int size = Convert.ToInt32(line.Split(';')[0].Trim(), 16);
            if (size == 0) break;
            body.Write(await ReadBytes(size));
            await ReadUntil(LineEnd);
        }
        while (await ReadUntil(LineEnd) is { Length: > 0 }) { }
        return body.ToArray();
    }

    async Task<byte[]> ReadBytes(int count)
    {
        byte[] result = new byte[count];
        int buffered = Math.Min(count, end - start);
        Array.Copy(buffer, start, result, 0, buffered);
        start += buffered;
        if (buffered < count) await stream.ReadExactlyAsync(result.AsMemory(buffered));
        return result;
    }

    async Task<string?> ReadUntil(byte[] delimiter)
    {
        while (true)
        {
            int index = buffer.AsSpan(start, end - start).IndexOf(delimiter);
            if (index >= 0)
            {
                string text = Encoding.Latin1.GetString(buffer, start, index);
                start += index + delimiter.Length;
                return text;
            }

            if (start > 0)
            {
                Array.Copy(buffer, start, buffer, 0, end - start);
                end -= start;
                start = 0;
            }
            if (end == buffer.Length) throw new InvalidDataException("Request head is too large");

            int read = await stream.ReadAsync(buffer.AsMemory(end));
            if (read == 0) return end == 0 ? null : throw new IOException("Connection closed mid-request");
            end += read;
        }
    }
}
