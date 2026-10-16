using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace KronosCLI;

internal sealed class Launcher(Options options) : IDisposable
{
    static readonly string[] ProcessName =
    [
        "FortniteClient-Win64-Shipping_EAC",
        "FortniteClient-Win64-Shipping_EAC_EOS",
        "FortniteClient-Win64-Shipping_BE",
        "FortniteClient-Win64-Shipping",
		"FortniteClient-Win32-Shipping",
		"FortniteLauncher",
    ];

    
    const string ContentSplashUrl = "https://raw.githubusercontent.com/Helix-Dev-Q/random-stuff-for-my-repos/refs/heads/main/kronoy.bmp";
    const string EacSplashUrl = "https://raw.githubusercontent.com/Helix-Dev-Q/random-stuff-for-my-repos/refs/heads/main/Kronos%20(1).png";

    readonly List<GameSession> sessions = [];
    readonly Dictionary<string, IntPtr> ProcessesFN = new(StringComparer.OrdinalIgnoreCase);
    readonly Image images = new(options.BuildPath);

    RedirectProxy? proxy = BuildVersion.Read(options) is { UsesProxy: false } ? null : RedirectProxy.Start(options);
    GameSession? eacSession;
    bool proxyUnavailableWarned;
    bool eacInstalled;
    bool disposed;

    public GameSession? StartClient(bool useProxy) =>
        Start("Client", options.Username, options.ClientLobbyDll, [], useProxy);

    public GameSession? StartServer() =>
        Start("Server", "Server", options.ServerLobbyDll, ["-nullrhi", "-nosound", "-log"], false);

    GameSession? Start(string role, string login, string lobbyDll, string[] roleArgs, bool useProxy)
    {
        if (useProxy && eacSession is { HasEnded: false })
        {
            Log.Warn($"[{role}] EAC only allows one game at a time and a proxy client is already running (4 kills it)");
            return null;
        }

        string gameExe = options.GameExeFor(useProxy);
        if (!File.Exists(gameExe))
        {
            Log.Error($"[{role}] Game not found: {gameExe}");
            return null;
        }

        string? redirectDll = useProxy ? null : options.RedirectDll == "" ? null : options.RedirectDll;
        if (useProxy)
        {
            proxy ??= RedirectProxy.Start(options);
            WarnIfProxyMissing();
            EnsureEacInstalled();
            EnsureEacSettings();
        }
        else if (redirectDll == null)
            Log.Warn($"[{role}] No redirect DLL is set (Options > DLLs) - traffic won't be redirected");

        string[] args =
        [
            "-epicapp=Fortnite",
            "-epicenv=Prod",
            "-epiclocale=en-us",
            "-epicportal",
            "-skippatchcheck",
            "-nobe",
            useProxy ? "-fromfl=eaceos" : "-fromfl=eac",
            "-fltoken=3db3ba5dcbd2e16703f3978d",
            "-AUTH_TYPE=epic",
            $"-httpproxy={options.Proxy}",
            $"-AUTH_LOGIN={login}",
            $"-AUTH_PASSWORD={options.Password}",
            .. roleArgs,
            .. options.GameArgs,
        ];
        string argLine = string.Join(' ', args.Select(Quote));
        var startedAt = DateTime.Now;

        try
        {
            images.SwapDirectory(Path.Combine(options.BuildPath, "FortniteGame", "Content", "Splash"), ContentSplashUrl);
            images.SwapDirectory(Path.Combine(options.BuildPath, "FortniteGame", "Binaries", "Win64", "EasyAntiCheat"), EacSplashUrl);
            foreach (var rd in options.ReplaceDirs)
                images.SwapDirectory(Path.IsPathRooted(rd) ? rd : Path.Combine(options.BuildPath, rd), ContentSplashUrl);
        }
        catch (Exception e)
        {
            Log.Warn($"Preparing image swaps failed: {e.Message}");
        }

        Process? process = redirectDll != null
            ? StartInjected(role, gameExe, argLine, redirectDll)
            : StartNormally(role, gameExe, argLine, useProxy ? options.Proxy : null);
        if (process == null) return null;

        string mode = useProxy ? "EAC + proxy" : redirectDll != null ? "no EAC + redirect DLL" : "no EAC";
        Log.Ok($"[{role}] Started as {login} ({mode}, PID {process.Id})");
        Log.Detail($"[{role}] \"{gameExe}\" " + string.Join(' ',
            args.Select(a => a.StartsWith("-AUTH_PASSWORD=") ? "-AUTH_PASSWORD=***" : Quote(a))));

        var session = new GameSession(role, process, login, startedAt, useProxy ? "" : lobbyDll, useProxy || redirectDll != null);
        sessions.RemoveAll(s => s.HasEnded);
        sessions.Add(session);
        if (useProxy) eacSession = session;

        EnsureProcessesFN(useProxy);
        return session;
    }

    Process? StartNormally(string role, string gameExe, string argLine, string? proxyEndpoint)
    {
        var process = new Process
        {
            StartInfo =
            {
                FileName = gameExe,
                Arguments = argLine,
                WorkingDirectory = options.Win64Dir,
                UseShellExecute = false,
            },
        };

        var env = process.StartInfo.Environment;
        foreach (var key in new[] { "HTTP_PROXY", "http_proxy", "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy" })
            if (proxyEndpoint != null) env[key] = $"http://{proxyEndpoint}";
            else env.Remove(key);

        try
        {
            process.Start();
            return process;
        }
        catch (Exception e)
        {
            Log.Error($"[{role}] Could not start {gameExe}: {e.Message}");
            return null;
        }
    }

    Process? StartInjected(string role, string gameExe, string argLine, string dll)
    {
        string name = Path.GetFileName(dll);
        if (!File.Exists(dll))
        {
            Log.Error($"[{role}] Redirect DLL not found: {dll}");
            return null;
        }

        var process = StartNormally(role, gameExe, argLine, null);
        if (process == null) return null;

        try
        {
            Injector.Inject(process.Handle, dll);
            Log.Ok($"[{role}] Injected redirect DLL {name}");
        }
        catch (Exception e)
        {
            Log.Error($"[{role}] Injecting redirect DLL {name} failed: {e.Message}");
        }
        return process;
    }

    void WarnIfProxyMissing()
    {
        if (proxyUnavailableWarned || proxy != null || ProxyListening()) return;
        Log.Warn($"Proxy chosen but nothing is listening at {options.Proxy} - the game won't be redirected");
        proxyUnavailableWarned = true;
    }

    bool ProxyListening()
    {
        int colon = options.Proxy.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(options.Proxy[(colon + 1)..], out int port)) return false;
        try
        {
            using var tcp = new TcpClient();
            return tcp.ConnectAsync(options.Proxy[..colon], port).Wait(1000) && tcp.Connected;
        }
        catch { return false; }
    }

    void EnsureEacInstalled()
    {
        if (eacInstalled) return;

        string dir = Path.Combine(options.Win64Dir, "EasyAntiCheat");
        string setup = Path.Combine(dir, "EasyAntiCheat_EOS_Setup.exe");
        if (!File.Exists(setup))
        {
            Log.Warn($"EAC installer not found: {setup}");
            eacInstalled = true;
            return;
        }

        try
        {
            var process = new Process
            {
                StartInfo =
                {
                    FileName = setup,
                    Arguments = "install fn",
                    WorkingDirectory = dir,
                    UseShellExecute = true,
                    Verb = "runas",
                },
            };
            process.Start();
            process.WaitForExit();
            eacInstalled = true;

            if (process.ExitCode == 0) Log.Ok("EAC installed (EasyAntiCheat_EOS_Setup install fn)");
            else Log.Warn($"EAC installer exited with 0x{process.ExitCode:X} - EAC may not be installed");
        }
        catch (Exception e)
        {
            Log.Error($"Running the EAC installer failed: {e.Message}");
        }
    }

    void EnsureEacSettings()
    {
        string dir = Path.Combine(options.Win64Dir, "EasyAntiCheat");
        string settings = Path.Combine(dir, "Settings.json");
        if (File.Exists(settings)) return;

        try
        {
            File.Copy(Path.Combine(dir, "DefaultSettings.json"), settings);
            Log.Ok($"Created {settings} from DefaultSettings.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"EAC needs {settings} and it couldn't be created: {e.Message}");
        }
    }

    void EnsureProcessesFN(bool eac)
    {
        if (!eac)
        {
            string? antiCheat = new[] { "_BE", "_EAC_EOS", "_EAC" }
                .Select(suffix => Path.Combine(options.Win64Dir, $"FortniteClient-Win64-Shipping{suffix}.exe"))
                .FirstOrDefault(File.Exists);
            if (antiCheat != null) StartSuspended(antiCheat);
            else Log.Warn($"Skipping the anti-cheat: no _BE/_EAC_EOS/_EAC exe in {options.Win64Dir}");
        }
        StartSuspended(Path.Combine(options.Win64Dir, "FortniteLauncher.exe"));
    }

    void StartSuspended(string exe)
    {
        string name = Path.GetFileName(exe);
        if (ProcessesFN.ContainsKey(exe)) return;
        if (!File.Exists(exe))
        {
            Log.Warn($"Skipping {name}: not found in {Path.GetDirectoryName(exe)}");
            return;
        }

        var startup = new Native.STARTUPINFOW { cb = Marshal.SizeOf<Native.STARTUPINFOW>() };
        if (!Native.CreateProcessW(null, new StringBuilder($"\"{exe}\""), IntPtr.Zero, IntPtr.Zero, false,
                Native.CREATE_SUSPENDED, IntPtr.Zero, Path.GetDirectoryName(exe), ref startup, out var info))
        {
            Log.Error($"{name} failed to start: {new Win32Exception().Message}");
            return;
        }

        Native.CloseHandle(info.hThread);
        ProcessesFN[exe] = info.hProcess;
        Log.Detail($"{name} suspended (PID {info.dwProcessId})");
    }

    public void KillProcessesFN()
    {
        foreach (var handle in ProcessesFN.Values)
        {
            Native.TerminateProcess(handle, 0);
            Native.CloseHandle(handle);
        }
        ProcessesFN.Clear();
    }

    public void KillEverything()
    {
        foreach (var session in sessions) session.MarkStopping();

        bool killedAny = ProcessesFN.Count > 0;
        KillProcessesFN();
        foreach (var name in ProcessName)
            killedAny |= KillByName(name);

        foreach (var session in sessions) session.WaitForExit(TimeSpan.FromSeconds(3));
        sessions.Clear();
        eacSession = null;

        images.RestoreAll();

        if (killedAny) Log.Ok("Killed everything");
        else Log.Info("Nothing was running");
    }

    static bool KillByName(string name)
    {
        bool killedAny = false;
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                process.Kill(true);
                killedAny = true;
            }
            catch { }
            finally { process.Dispose(); }
        }
        return killedAny;
    }

    static string Quote(string arg) => arg.Contains(' ') && !arg.StartsWith('"') ? $"\"{arg}\"" : arg;

    public void RestartProxy()
    {
        proxy?.Dispose();
        proxy = RedirectProxy.Start(options);
        proxyUnavailableWarned = false;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        images.RestoreAll();
        KillProcessesFN();
        proxy?.Dispose();
    }
}
