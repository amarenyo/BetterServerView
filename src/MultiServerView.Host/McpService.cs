using System.Net;
using System.Security.Cryptography;
using System.Text;
using MultiServerView.Core;
using MultiServerView.Core.Mcp;
using MultiServerView.Core.Sftp;
using MultiServerView.Core.Ssh;

namespace MultiServerView.Host;

/// <summary>
/// Local MCP server for AI tools (Claude Code, Claude Desktop, ...): listens on 127.0.0.1 only and needs the bearer token
/// shown in the settings. The tools work on the hosts of the unlocked vault.
/// </summary>
public sealed class McpService : IMcpBackend, IDisposable
{
    private readonly VaultStore _vault;
    private readonly Func<string, int, string, HostKeyTrust> _check;
    private readonly JumpResolver _jumps;
    private readonly Action? _onActivity;
    private readonly McpHandler _handler;
    private readonly McpConnectionPool<SshExecSession> _sshPool = new(TimeSpan.FromSeconds(90));
    private readonly McpConnectionPool<PooledSftp> _sftpPool = new(TimeSpan.FromSeconds(90));
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _token = "";
    private int _port;
    private long _lastActivityTick = long.MinValue;

    public McpService(VaultStore vault, Func<string, int, string, HostKeyTrust> check, JumpResolver jumps, Action? onActivity = null)
    {
        _vault = vault;
        _check = check;
        _jumps = jumps;
        _onActivity = onActivity;
        _handler = new McpHandler(this);
    }

    /// <summary>Last start error (e.g. port already in use), shown in the settings.</summary>
    public string? Error { get; private set; }

    public bool Running { get { lock (_gate) return _listener != null; } }

    /// <summary>Closes all pooled SSH/SFTP connections (for example after vault lock or credential changes).</summary>
    public void InvalidateConnections()
    {
        _sshPool.Clear();
        _sftpPool.Clear();
    }

    public void Apply(AppSettings settings)
    {
        var clear = false;
        lock (_gate)
        {
            if (settings.McpEnabled && _listener != null && _port == settings.McpPort && _token == settings.McpToken) return;
            clear = _listener != null;
            StopLocked();
            Error = null;
            if (!settings.McpEnabled || settings.McpToken.Length == 0)
            {
                // Still clear below if the listener had been running
            }
            else
            {
                try
                {
                    var listener = new HttpListener();
                    listener.Prefixes.Add($"http://127.0.0.1:{settings.McpPort}/");
                    try { listener.Prefixes.Add($"http://localhost:{settings.McpPort}/"); }
                    catch (HttpListenerException) { /* 127.0.0.1 is already listening */ }
                    listener.Start();
                    var port = settings.McpPort;
                    var token = settings.McpToken;
                    var cts = new CancellationTokenSource();
                    _listener = listener;
                    _port = port;
                    _token = token;
                    _cts = cts;
                    _ = Task.Run(() => LoopAsync(listener, port, token, cts.Token));
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    Error = $"The MCP server could not start on port {settings.McpPort}: {ex.Message}";
                }
            }
        }
        if (clear) InvalidateConnections();
    }

    public void Stop()
    {
        lock (_gate) StopLocked();
        InvalidateConnections();
    }

    private void StopLocked()
    {
        _cts?.Cancel();
        try { _listener?.Close(); } catch (Exception) { /* already closed */ }
        _listener = null;
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
        _sshPool.Dispose();
        _sftpPool.Dispose();
    }

    private async Task LoopAsync(HttpListener listener, int port, string token, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => HandleAsync(context, port, token, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, int port, string token, CancellationToken ct)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            // Not for browsers: no Origin header (blocks web pages talking to localhost), only our own host name.
            var hostOk = request.Headers["Host"] is { } h && (
                h.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) ||
                h.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase) ||
                h.Equals($"[::1]:{port}", StringComparison.OrdinalIgnoreCase));
            if (!hostOk || request.Headers["Origin"] != null) { response.StatusCode = 403; return; }
            if (request.Url?.AbsolutePath.TrimEnd('/') != "/mcp") { response.StatusCode = 404; return; }
            if (!TokenOk(request.Headers["Authorization"], token)) { response.StatusCode = 401; response.AddHeader("WWW-Authenticate", "Bearer"); return; }
            if (request.HttpMethod != "POST") { response.AddHeader("Allow", "POST"); response.StatusCode = 405; return; }

            using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            if (body.Length > 5_000_000) { response.StatusCode = 413; return; }

            NotifyActivity();
            var answer = await _handler.HandleAsync(body, ct);
            if (answer == null) { response.StatusCode = 202; response.ContentLength64 = 0; return; }
            var bytes = Encoding.UTF8.GetBytes(answer);
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { response.StatusCode = 500; } catch (Exception) { /* connection already gone */ }
        }
        finally
        {
            try { response.Close(); } catch (Exception) { /* connection already gone */ }
        }
    }

    private void NotifyActivity()
    {
        var cb = _onActivity;
        if (cb == null) return;
        var now = Environment.TickCount64;
        var prev = Interlocked.Read(ref _lastActivityTick);
        if (prev != long.MinValue && now - prev < 10_000) return;
        if (Interlocked.CompareExchange(ref _lastActivityTick, now, prev) != prev) return;
        try { cb(); } catch { /* UI notification errors must never break MCP calls */ }
    }

    private static bool TokenOk(string? header, string token)
    {
        const string prefix = "Bearer ";
        if (header == null || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var given = Encoding.UTF8.GetBytes(header[prefix.Length..].Trim());
        return CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(token));
    }

    // ------------------------------------------------------------------------------------------------ Tools

    public Task<IReadOnlyList<McpHost>> ListHostsAsync(string query)
    {
        var hosts = Unlocked().List()
            .Where(s => query.Length == 0 || $"{s.Name} {s.Host} {s.Workspace} {s.Group} {string.Join(" ", s.Tags)}".Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(s => new McpHost(s.Id, s.Name, s.Host, s.Port, s.Protocol.ToString().ToLowerInvariant(), s.Workspace, s.Group, string.Join(",", s.Tags)))
            .ToList();
        return Task.FromResult<IReadOnlyList<McpHost>>(hosts);
    }

    public async Task<McpExecResult> RunCommandAsync(string host, string command, int timeoutSeconds, CancellationToken ct)
    {
        var (server, key) = Resolve(host);
        var r = await _sshPool.ExecuteAsync(
            server.Id,
            token => SshExecSession.ConnectAsync(server, key, _check, _jumps, token),
            s => s.IsConnected,
            async s =>
            {
                var res = await s.RunAsync(command, TimeSpan.FromSeconds(timeoutSeconds), ct);
                if (res.TimedOut) s.Disconnect(); // do not reuse a connection whose command timed out
                return res;
            },
            SshExec.IsConnectionError,
            ct);
        return new McpExecResult(r.StdOut, r.StdErr, r.ExitCode, r.TimedOut);
    }

    public Task<string> ReadFileAsync(string host, string path, CancellationToken ct) =>
        RunSftpAsync(host, async p => (await p.Sftp.ReadTextAsync(SftpSession.Normalize(p.Home, path))).Content, ct);

    public Task WriteFileAsync(string host, string path, string content, CancellationToken ct) =>
        RunSftpAsync(host, async p =>
        {
            await p.Sftp.WriteTextAsync(SftpSession.Normalize(p.Home, path), content, "\n", false, 0, 0, force: true);
            return true;
        }, ct);

    public Task<IReadOnlyList<McpFileEntry>> ListDirectoryAsync(string host, string path, CancellationToken ct) =>
        RunSftpAsync<IReadOnlyList<McpFileEntry>>(host, async p =>
        {
            var listing = await p.Sftp.ListAsync(path);
            return listing.Entries.Select(e => new McpFileEntry(e.Name, e.IsDir, e.Size)).ToList();
        }, ct);

    private Task<T> RunSftpAsync<T>(string host, Func<PooledSftp, Task<T>> action, CancellationToken ct)
    {
        var (server, key) = Resolve(host);
        return _sftpPool.ExecuteAsync(
            server.Id,
            token => OpenSftpAsync(server, key, token),
            p => p.IsConnected,
            action,
            SshExec.IsConnectionError,
            ct);
    }

    private async Task<PooledSftp> OpenSftpAsync(ServerEntry server, KeyEntry? key, CancellationToken ct)
    {
        var sftp = new SftpSession(server, key, _check, _jumps);
        try
        {
            var home = await sftp.ConnectAsync(ct);
            return new PooledSftp(sftp, home);
        }
        catch
        {
            sftp.Dispose();
            throw;
        }
    }

    private sealed record PooledSftp(SftpSession Sftp, string Home) : IDisposable
    {
        public bool IsConnected => Sftp.IsConnected;
        public void Dispose() => Sftp.Dispose();
    }

    private VaultStore Unlocked() => _vault.IsUnlocked ? _vault : throw new InvalidOperationException("The vault is locked. Unlock MultiServerView first.");

    private (ServerEntry Server, KeyEntry? Key) Resolve(string reference)
    {
        var all = Unlocked().List();
        var matches = all.Where(s => s.Id == reference).ToList();
        if (matches.Count == 0) matches = all.Where(s => s.Name.Equals(reference, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) throw new KeyNotFoundException($"No host named \"{reference}\". Use list_hosts to see the available hosts.");
        if (matches.Count > 1) throw new InvalidOperationException($"\"{reference}\" matches {matches.Count} hosts. Use the id from list_hosts.");
        var server = _vault.FindResolved(matches[0].Id) ?? throw new KeyNotFoundException("Host not found.");
        if (server.Protocol != Protocol.Ssh) throw new InvalidOperationException($"\"{server.Name}\" is a {server.Protocol} host. Only SSH hosts are supported.");
        return (server, server.Auth == AuthMethod.Key ? _vault.FindKey(server.KeyId) : null);
    }
}
