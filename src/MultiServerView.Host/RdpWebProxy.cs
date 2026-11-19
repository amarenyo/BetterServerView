using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;

namespace MultiServerView.Host;

/// <summary>
/// Local "RDCleanPath" proxy for the in-app RDP client (IronRDP in the UI). The browser cannot open TCP or TLS to
/// the RDP server itself, so it talks WebSocket to this proxy (127.0.0.1 only, one-time token per session):
/// the proxy opens the TCP connection, forwards the X.224 request, terminates TLS towards the server, hands the server
/// certificate to the client (needed for CredSSP/NLA) and then relays the decrypted bytes both ways.
/// </summary>
public sealed class RdpWebProxy : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, (string Host, int Port)> _sessions = new();
    private readonly int _port;

    public RdpWebProxy()
    {
        for (var attempt = 0; ; attempt++)
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            _port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            try
            {
                _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                _listener.Prefixes.Clear();
            }
        }
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Registers a target and returns the WebSocket address the UI has to connect to.</summary>
    public string Register(string host, int port)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _sessions[token] = (host, port);
        return $"ws://127.0.0.1:{_port}/rdp/{token}";
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Close(); } catch (Exception) { /* already closed */ }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception) { return; }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "";
        var token = path.StartsWith("/rdp/", StringComparison.Ordinal) ? path[5..] : "";
        if (!ctx.Request.IsWebSocketRequest || !_sessions.TryRemove(token, out var target))
        {
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
            return;
        }

        WebSocket ws;
        try { ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket; }
        catch (Exception) { ctx.Response.Close(); return; }

        try
        {
            await RunAsync(ws, target.Host, target.Port);
        }
        catch (Exception) { /* connection ended */ }
        finally
        {
            try { ws.Dispose(); } catch (Exception) { /* ignore */ }
        }
    }

    private async Task RunAsync(WebSocket ws, string host, int port)
    {
        var ct = _cts.Token;
        var first = await ReceiveMessageAsync(ws, ct);
        if (first == null) return;
        Console.Error.WriteLine("[rdp-proxy] request " + first.Length + " bytes: " + Convert.ToHexString(first.AsSpan(0, Math.Min(first.Length, 48))));

        RdpCleanPath.Request req;
        try { req = RdpCleanPath.ParseRequest(first); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[rdp-proxy] bad request: " + ex.Message);
            await SendAsync(ws, RdpCleanPath.EncodeError(400, "Invalid request: " + ex.Message), ct);
            return;
        }

        using var tcp = new TcpClient { NoDelay = true };
        Stream network;
        byte[] x224Response;
        SslStream tls;
        byte[] cert;
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(15));
            await tcp.ConnectAsync(host, port, connectCts.Token);
            network = tcp.GetStream();

            // X.224 connection request / confirm (TPKT framed, still in clear text)
            await network.WriteAsync(req.X224, connectCts.Token);
            x224Response = await ReadTpktAsync(network, connectCts.Token);
            Console.Error.WriteLine("[rdp-proxy] x224 response: " + Convert.ToHexString(x224Response));

            tls = new SslStream(network, false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                EnabledSslProtocols = SslProtocols.None,
                RemoteCertificateValidationCallback = (_, _, _, _) => true, // RDP servers use self-signed certificates; the client checks the key via CredSSP
            }, connectCts.Token);
            cert = tls.RemoteCertificate?.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert) ?? throw new InvalidOperationException("The server sent no certificate.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[rdp-proxy] connect failed: " + ex.Message);
            await SendAsync(ws, RdpCleanPath.EncodeError(502, ex is OperationCanceledException ? "Connection timed out." : ex.Message), ct);
            return;
        }

        using (tls)
        {
            Console.Error.WriteLine("[rdp-proxy] tls ok, cert " + cert.Length + " bytes");
            await SendAsync(ws, RdpCleanPath.EncodeResponse(x224Response, cert, $"{host}:{port}"), ct);

            // From here on only relay bytes.
            using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var up = Task.Run(async () =>
            {
                while (!relayCts.IsCancellationRequested)
                {
                    var msg = await ReceiveMessageAsync(ws, relayCts.Token);
                    if (msg == null) break;
                    await tls.WriteAsync(msg, relayCts.Token);
                    await tls.FlushAsync(relayCts.Token);
                }
            });
            var down = Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];
                while (!relayCts.IsCancellationRequested)
                {
                    var n = await tls.ReadAsync(buffer, relayCts.Token);
                    if (n == 0) break;
                    await SendAsync(ws, buffer.AsMemory(0, n).ToArray(), relayCts.Token);
                }
            });
            await Task.WhenAny(up, down);
            relayCts.Cancel();
            try { await Task.WhenAll(up, down); } catch (Exception) { /* ended */ }
        }
    }

    private static async Task<byte[]> ReadTpktAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[4];
        await s.ReadExactlyAsync(header, ct);
        var length = (header[2] << 8) | header[3];
        if (header[0] != 3 || length < 4) throw new InvalidDataException("The server did not answer like an RDP server.");
        var all = new byte[length];
        Array.Copy(header, all, 4);
        await s.ReadExactlyAsync(all.AsMemory(4), ct);
        return all;
    }

    private static async Task<byte[]?> ReceiveMessageAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return ms.ToArray();
        }
    }

    private static Task SendAsync(WebSocket ws, byte[] data, CancellationToken ct) =>
        ws.SendAsync(data, WebSocketMessageType.Binary, true, ct);
}

/// <summary>The RDCleanPath PDU (ASN.1 DER) that the IronRDP web client speaks.</summary>
internal static class RdpCleanPath
{
    private const int Version = 3390; // 3389 + 1

    internal sealed record Request(string? Destination, byte[] X224);

    public static Request ParseRequest(byte[] data)
    {
        var (tag, body, _) = ReadTlv(data, 0);
        if (tag != 0x30) throw new InvalidDataException("not a SEQUENCE");
        string? destination = null;
        byte[]? x224 = null;
        var pos = 0;
        while (pos < body.Length)
        {
            var (t, content, next) = ReadTlv(body, pos);
            pos = next;
            var (_, inner, _) = ReadTlv(content, 0);
            switch (t)
            {
                case 0xA2: destination = System.Text.Encoding.UTF8.GetString(inner); break;
                case 0xA6: x224 = inner; break;
            }
        }
        return new Request(destination, x224 ?? throw new InvalidDataException("no X.224 PDU"));
    }

    public static byte[] EncodeResponse(byte[] x224, byte[] serverCert, string serverAddr) =>
        Tlv(0x30,
            Explicit(0, Tlv(0x02, Int(Version))),
            Explicit(6, Tlv(0x04, x224)),
            Explicit(7, Tlv(0x30, Tlv(0x04, serverCert))),
            Explicit(9, Tlv(0x0C, System.Text.Encoding.UTF8.GetBytes(serverAddr))));

    public static byte[] EncodeError(int httpStatus, string _) =>
        Tlv(0x30,
            Explicit(0, Tlv(0x02, Int(Version))),
            Explicit(1, Tlv(0x30,
                Explicit(0, Tlv(0x02, Int(1))),            // general error
                Explicit(1, Tlv(0x02, Int(httpStatus))))));

    private static byte[] Explicit(int tag, byte[] inner) => Tlv((byte)(0xA0 | tag), inner);

    private static byte[] Int(int value)
    {
        var bytes = new List<byte>();
        var v = value;
        do { bytes.Insert(0, (byte)(v & 0xFF)); v >>= 8; } while (v != 0);
        if ((bytes[0] & 0x80) != 0) bytes.Insert(0, 0);
        return bytes.ToArray();
    }

    private static byte[] Tlv(byte tag, params byte[][] parts)
    {
        var length = parts.Sum(p => p.Length);
        var header = new List<byte> { tag };
        if (length < 0x80) header.Add((byte)length);
        else
        {
            var lb = new List<byte>();
            for (var l = length; l > 0; l >>= 8) lb.Insert(0, (byte)(l & 0xFF));
            header.Add((byte)(0x80 | lb.Count));
            header.AddRange(lb);
        }
        var result = new byte[header.Count + length];
        header.CopyTo(result);
        var o = header.Count;
        foreach (var p in parts) { Array.Copy(p, 0, result, o, p.Length); o += p.Length; }
        return result;
    }

    private static (byte Tag, byte[] Content, int Next) ReadTlv(byte[] d, int pos)
    {
        var tag = d[pos++];
        int len = d[pos++];
        if ((len & 0x80) != 0)
        {
            var n = len & 0x7F;
            len = 0;
            for (var i = 0; i < n; i++) len = (len << 8) | d[pos++];
        }
        if (pos + len > d.Length) throw new InvalidDataException("truncated");
        return (tag, d.AsSpan(pos, len).ToArray(), pos + len);
    }
}
