using System.Net;
using System.Net.Sockets;

namespace KronosCLI;

internal static class Program
{
    const string MainHint = "[1] Client   [2] Server   [3] Both   [4] Kill   [5] Options   [6] Exit";
    const string OptionsHint = "[1] Build   [2] DLLs   [3] Login   [4] Backend   [5] Proxy   [6] Back";
    const string OptionsHintNoProxy = "[1] Build   [2] DLLs   [3] Login   [4] Backend   [6] Back";

    static readonly Ui.Button Keep = new("Keep", "");

    static int Main(string[] argv)
    {
        Options options;
        try
        {
            options = Options.Parse(argv);
        }
        catch (ArgumentException e)
        {
            System.Console.Error.WriteLine(e.Message);
            System.Console.Error.WriteLine();
            System.Console.Error.WriteLine(Options.Usage);
            return 1;
        }

        if (options.ShowHelp)
        {
            System.Console.WriteLine(Options.Usage);
            return 0;
        }

        Log.Reset();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Ui.Stop();
            Log.Error($"Unexpected error: {e.ExceptionObject}");
        };
        SystemProxy.RestoreLeftover();
        if (options.Mode != null && !HasGame(options))
        {
            Log.Error($"No Fortnite exe under {options.Win64Dir} - set the build with --path");
            return 1;
        }

        bool dashboard = options.Mode == null
            && !System.Console.IsInputRedirected && !System.Console.IsOutputRedirected
            && Console.TryEnableAnsi();
        if (dashboard) Ui.Start();

        try
        {
            CheckSetup(options);
            using var launcher = new Launcher(options);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => { launcher.Dispose(); Ui.Stop(); };
            System.Console.CancelKeyPress += (_, _) => { launcher.Dispose(); Ui.Stop(); };

            return options.Mode != null ? RunOnce(launcher, options) : RunMenu(launcher, options);
        }
        catch (Exception e)
        {
            Ui.Stop();
            Log.Error($"Unexpected error: {e}");
            if (!System.Console.IsInputRedirected)
            {
                System.Console.WriteLine("Press Enter to close.");
                System.Console.ReadLine();
            }
            return 1;
        }
        finally
        {
            if (dashboard) Ui.Stop();
        }
    }

    static int RunMenu(Launcher launcher, Options options)
    {
        ShowMainMenu();

        while (true)
        {
            switch (Log.ReadCommand())
            {
                case "1":
                    StartClient(launcher, options);
                    break;
                case "2":
                    if (ProxyAllowed(options) && Log.Confirm($"Launch with proxy? (Note: the server can't launch with the proxy - it doesn't allow Dll Injections)", false))
                        Log.Warn("The server can't launch with the proxy because EAC doesn't allow Dll Injections - pick 2 again and answer n");
                    else
                        launcher.StartServer();
                    break;
                case "3":
                    if (ProxyAllowed(options) && Log.Confirm($"Launch with proxy? (Note: you won't be able to launch the server and client with the proxy - with a patched exe, a gameserver and a client it works without having proxy on)", false))
                        Log.Warn("The client and server can't both launch with the proxy because EAC doesn't allow DLl Injections and also it can't have 2 EAC opened at the same time - pick 3 again and answer n, or use 1 for a proxy client (Note: Client Dll won't work either if launched with proxy)");
                    else
                    {
                        launcher.StartClient(false);
                        launcher.StartServer();
                    }
                    break;
                case "4":
                    Ui.Clear();
                    launcher.KillEverything();
                    break;
                case "5":
                    OptionsMenu(launcher, options);
                    ShowMainMenu();
                    break;
                case "6" or null:
                    return 0;
                case "":
                    break;
                case "?":
                    ShowMainMenu();
                    break;
                default:
                    Log.Warn("Pick 1-6, or ? to show the commands again");
                    break;
            }
        }
    }

    static void ShowMainMenu()
    {
        if (Ui.Active) Ui.SetHint(MainHint);
        else Log.Raw($"""

              1  Run Client
              2  Run Server
              3  Run Both
              4  Kill Everything
              5  Options
              6  Exit

            """);
    }

    static void StartClient(Launcher launcher, Options options)
    {
        if (BuildVersion.Read(options) is { UsesProxy: false } version)
        {
            Log.Info($"Version {version}: older than 30.10, launching without the proxy");
            launcher.StartClient(false);
            return;
        }

        launcher.StartClient(Log.Confirm("Launch with proxy? (Note: Client Dll won't work because EAC doesn't allow Dll Injections", true));
    }

    static void OptionsMenu(Launcher launcher, Options options)
    {
        while (true)
        {
            ShowOptions(options);

            switch (Log.ReadCommand())
            {
                case "1":
                    ChangeBuild(options);
                    break;
                case "2":
                    ChangeDlls(options);
                    break;
                case "3":
                    ChangeLogin(options);
                    break;
                case "4":
                    ChangeBackend(options);
                    break;
                case "5" when ProxyAllowed(options):
                    ChangeProxy(launcher, options);
                    break;
                case "6" or "" or null:
                    return;
                default:
                    Log.Warn(ProxyAllowed(options)
                        ? "Pick 1-6, or Enter to go back"
                        : "Pick 1-4 or 6, or Enter to go back - builds older than 30.10 don't use the proxy");
                    break;
            }
        }
    }

    static void ShowOptions(Options options)
    {
        bool proxy = ProxyAllowed(options);
        if (Ui.Active)
        {
            Ui.SetHint(proxy ? OptionsHint : OptionsHintNoProxy);
            return;
        }

        Log.Raw($"""

              1  Build     {options.BuildPath}
              2  DLLs      redirect {DllName(options.RedirectDll)}, client {DllName(options.ClientLobbyDll)}, server {DllName(options.ServerLobbyDll)}
              3  Login     {options.Username}
              4  Backend   {options.Backend}
              {(proxy ? $"5  Proxy     {options.Proxy}" : "   Proxy     not used below 30.10")}
              6  Back

            """);
    }

    static string DllName(string dll) => dll == "" ? "none" : Path.GetFileName(dll);

    static void ChangeBuild(Options options)
    {
        string? value = Log.ReadValue($"  Build [{options.BuildPath}]: ", example: @"e.g. C:\FortniteBuilds\42.30-CL-58813929",
            buttons: [new("Browse…", Action: () => Dialogs.PickFolder("Select your Fortnite build folder", options.BuildPath)), Keep]);
        if (value == null) return;

        options.BuildPath = value;
        if (HasGame(options))
            Log.Ok($"Build set to {options.BuildPath}");
        else
            Log.Warn($"Build set to {options.BuildPath}, but no Fortnite exe is under {options.Win64Dir}");
        ReportVersion(options);
        RefreshStatus(options);
        options.Save();
    }

    static void ChangeDlls(Options options)
    {
        options.RedirectDll = EditDll("Redirect DLL", options.RedirectDll);
        options.ClientLobbyDll = EditDll("Client lobby DLL", options.ClientLobbyDll);
        options.ServerLobbyDll = EditDll("Server lobby DLL", options.ServerLobbyDll);
        options.Save();
    }

    static string EditDll(string label, string current)
    {
        string? value = Log.ReadValue($"  {label} [{(current == "" ? "none" : current)}] (- to clear): ",
            example: @"e.g. C:\Hera-Redirect\x64\Release\Hera-Redirect.dll",
            buttons: [new("Browse…", Action: () => Dialogs.PickFile($"Select the {label}", "DLL files", "*.dll", current)), new("Clear", "-"), Keep]);
        if (value == null) return current;
        if (value is "-" or "none")
        {
            Log.Ok($"{label} cleared");
            return "";
        }

        string full = Path.GetFullPath(value);
        if (File.Exists(full)) Log.Ok($"{label} set to {full}");
        else Log.Warn($"{label} set to {full}, but it doesn't exist yet");
        return full;
    }

    static void ChangeLogin(Options options)
    {
        options.Username = Log.ReadValue($"  User [{options.Username}]: ", example: "e.g. Player (any name your backend accepts)", buttons: [Keep]) ?? options.Username;
        options.Password = Log.ReadValue($"  Pass [{new string('*', options.Password.Length)}]: ", secret: true, buttons: [Keep]) ?? options.Password;
        RefreshStatus(options);
        options.Save();
    }

    static void ChangeBackend(Options options)
    {
        string? value = Log.ReadValue($"  Backend [{options.Backend}]: ", example: "e.g. http://127.0.0.1:3551", buttons: [Keep]);
        if (value == null) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            Log.Warn($"{value} isn't an http:// URL - backend unchanged");
            return;
        }

        options.Backend = value;
        if (Reachable(value)) Log.Ok($"Backend set to {value}");
        else Log.Warn($"Backend set to {value}, but nothing is listening there yet");
        RefreshStatus(options);
        options.Save();
    }

    static void ChangeProxy(Launcher launcher, Options options)
    {
        string? value = Log.ReadValue($"  Proxy [{options.Proxy}]: ", example: "e.g. 127.0.0.1:8888", buttons: [Keep]);
        if (value == null || value == options.Proxy) return;
        if (!IPEndPoint.TryParse(value, out var endpoint) || endpoint.Port == 0)
        {
            Log.Warn($"{value} isn't an ip:port - proxy unchanged");
            return;
        }

        options.Proxy = value;
        launcher.RestartProxy();
        RefreshStatus(options);
        options.Save();
    }

    static int RunOnce(Launcher launcher, Options options)
    {
        var sessions = new List<GameSession?>();
        switch (options.Mode)
        {
            case "client":
                sessions.Add(launcher.StartClient(ProxyAllowed(options) && !options.NoProxy));
                break;
            case "server":
                sessions.Add(launcher.StartServer());
                break;
            case "both":
                sessions.Add(launcher.StartClient(false));
                sessions.Add(launcher.StartServer());
                break;
            default:
                Log.Error($"Unknown mode: {options.Mode} (expected client, server or both)");
                return 1;
        }

        Log.Info("Waiting for the game to exit");
        foreach (var session in sessions.OfType<GameSession>())
            session.WaitForExit();

        return sessions.Contains(null) ? 1 : 0;
    }

    static bool HasGame(Options options) =>
        File.Exists(options.GameExeFor(true)) || File.Exists(options.GameExeFor(false));

    static bool ProxyAllowed(Options options) => BuildVersion.Read(options) is not { UsesProxy: false };

    static void ReportVersion(Options options)
    {
        if (BuildVersion.Read(options) is not { } version)
            Log.Warn($"Version: unknown - couldn't read {BuildVersion.CloudContentPath(options)}, you'll be asked whether to use the proxy");
        else if (version.UsesProxy)
            Log.Ok($"Version: {version} - 30.10 or newer, clients launch through EAC with the proxy (Unless you have a patched executable, in which case the proxy wouldn't be needed)");
        else
            Log.Ok($"Version: {version} - older than 30.10, clients launch without the proxy");
    }

    static void CheckSetup(Options options)
    {
        Log.Info($"Build:   {options.Win64Dir}");
        if (!HasGame(options))
            Log.Warn("Build:   no Fortnite exe here - set your build in Options > Build");
        ReportVersion(options);
        Log.Info($"Log:     {Log.FilePath}");

        bool backendUp = Reachable(options.Backend);
        if (backendUp) Log.Ok($"Backend: listening at {options.Backend}");
        else Log.Warn($"Backend: nothing is listening at {options.Backend} - start it first");

        foreach (var dll in new[] { options.RedirectDll, options.ClientLobbyDll, options.ServerLobbyDll })
            if (dll != "" && !File.Exists(dll)) Log.Warn($"DLL not found: {dll}");

        RefreshStatus(options, backendUp);
    }

    static void RefreshStatus(Options options, bool? backendUp = null)
    {
        if (!Ui.Active) return;

        bool up = backendUp ?? Reachable(options.Backend);
        var version = BuildVersion.Read(options);
        bool proxy = version is not { UsesProxy: false };
        Ui.SetStatus("build", ShortBuild(options), Ui.Level.Info);
        Ui.SetStatus("version", version?.Text ?? "unknown", version == null ? Ui.Level.Warn : Ui.Level.Info);
        Ui.SetStatus("user", options.Username, Ui.Level.Plain);
        Ui.SetStatus("backend", options.Backend, up ? Ui.Level.Ok : Ui.Level.Warn);
        Ui.SetStatus("proxy", proxy ? options.Proxy : "off below 30.10", proxy ? Ui.Level.Info : Ui.Level.Detail);
    }

    static string ShortBuild(Options options)
    {
        string path = options.BuildPath.TrimEnd('\\', '/');
        string leaf = Path.GetFileName(path);
        return string.IsNullOrEmpty(leaf) ? path : leaf;
    }

    static bool Reachable(string url)
    {
        try
        {
            var uri = new Uri(url);
            using var tcp = new TcpClient();
            return tcp.ConnectAsync(uri.Host, uri.Port).Wait(1500) && tcp.Connected;
        }
        catch { return false; }
    }
}
