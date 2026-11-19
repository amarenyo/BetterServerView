using System.Net;
using System.Text;
using System.Text.Json;
using MultiServerView.Core;
using MultiServerView.Host;

namespace MultiServerView.Linux;

/// <summary>
/// Test helper (never used by the normal app): `multiserverview --testbridge &lt;port&gt; &lt;dataDir&gt;` exposes the bridge over
/// plain HTTP on 127.0.0.1 so the real UI can be driven from a browser script without a window.
/// POST /call = one bridge message, GET /events = queued events (JSON array).
/// </summary>
internal static class TestBridge
{
    public static int Run(int port, string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        var events = new List<string>();
        var platform = new LinuxPlatform(dataDir, _ => Task.FromResult<string?>(null), _ => Task.FromResult<string?>(null), _ => false);
        using var bridge = HostBootstrap.Create(dataDir, platform, e => { lock (events) events.Add(JsonSerializer.Serialize(e, Json.Options)); }, _ => new NoEmbeddedRdp());

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        Console.WriteLine("test bridge on " + port);
        while (true)
        {
            var ctx = listener.GetContext();
            _ = Task.Run(async () =>
            {
                try
                {
                    ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                    ctx.Response.AddHeader("Access-Control-Allow-Headers", "*");
                    byte[] body;
                    if (ctx.Request.HttpMethod == "OPTIONS") body = [];
                    else if (ctx.Request.Url!.AbsolutePath == "/call")
                    {
                        using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                        body = Encoding.UTF8.GetBytes(await bridge.HandleAsync(await reader.ReadToEndAsync()));
                    }
                    else
                    {
                        string json;
                        lock (events) { json = "[" + string.Join(",", events) + "]"; events.Clear(); }
                        body = Encoding.UTF8.GetBytes(json);
                    }
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                }
                catch (Exception) { /* ignore */ }
                finally { try { ctx.Response.Close(); } catch (Exception) { /* ignore */ } }
            });
        }
    }
}
