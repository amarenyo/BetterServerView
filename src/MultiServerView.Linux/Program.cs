using System.Net;
using System.Text.Json;
using MultiServerView.Core;
using MultiServerView.Host;
using Photino.NET;

namespace MultiServerView.Linux;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--selftest")
        {
            var testDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "msv-selftest");
            Environment.Exit(SelfTest.RunAsync(testDir).GetAwaiter().GetResult());
        }

        // MSV_DATA_DIR: own data folder (for tests); otherwise ~/.config/MultiServerView
        var dataDir = Environment.GetEnvironmentVariable("MSV_DATA_DIR");
        if (string.IsNullOrWhiteSpace(dataDir))
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiServerView");
        Directory.CreateDirectory(dataDir);

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (!File.Exists(Path.Combine(wwwroot, "index.html")))
        {
            Console.Error.WriteLine("The user interface was not found next to the program (wwwroot). Please reinstall.");
            Environment.Exit(1);
        }

        // The UI is served from a local web server that only listens on 127.0.0.1 (module scripts do not load from file://).
        using var files = new StaticFileServer(wwwroot);
        var devUrl = Environment.GetEnvironmentVariable("MSV_DEV_URL");
        var url = string.IsNullOrWhiteSpace(devUrl) ? files.Url : devUrl;

        PhotinoWindow? window = null;
        var maximized = false;
        void OnUi(Action action)
        {
            try { window?.Invoke(action); }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { /* window is closing */ }
        }

        Task<string?> Pick(Func<PhotinoWindow, string?> dialog)
        {
            var tcs = new TaskCompletionSource<string?>();
            OnUi(() =>
            {
                try { tcs.SetResult(dialog(window!)); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        var platform = new LinuxPlatform(
            dataDir,
            suggested => Pick(w => w.ShowSaveFile("Save vault backup", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), suggested))),
            title => Pick(w => w.ShowOpenFolder(title, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))?.FirstOrDefault()),
            command =>
            {
                switch (command)
                {
                    case "minimize": OnUi(() => window!.SetMinimized(true)); break;
                    case "maximize": maximized = !maximized; OnUi(() => window!.SetMaximized(maximized)); break;
                    case "close": OnUi(() => window!.Close()); break;
                }
                return maximized;
            });

        Bridge? bridge = null;
        void Emit(object payload)
        {
            var json = JsonSerializer.Serialize(payload, Json.Options);
            OnUi(() => window!.SendWebMessage(json));
        }

        bridge = HostBootstrap.Create(dataDir, platform, Emit, _ => new NoEmbeddedRdp());

        window = new PhotinoWindow()
            .SetTitle("MultiServerView")
            .SetUseOsDefaultSize(false)
            .SetSize(1360, 860)
            .SetMinSize(960, 600)
            .Center()
            .SetContextMenuEnabled(false)
            .SetGrantBrowserPermissions(true)
            .SetDevToolsEnabled(!string.IsNullOrWhiteSpace(devUrl))
            .RegisterWebMessageReceivedHandler((sender, message) =>
            {
                // everything except receiving the message runs in the background (PBKDF2, SSH connect, HTTP, ...)
                _ = Task.Run(async () =>
                {
                    var reply = await bridge!.HandleAsync(message);
                    OnUi(() => window!.SendWebMessage(reply));
                });
            })
            .Load(new Uri(url));

        window.WaitForClose();
        bridge.Dispose();
        Environment.Exit(0); // do not wait for background connections
    }
}
