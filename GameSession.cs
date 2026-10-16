using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KronosCLI;

internal sealed class GameSession
{
    static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Logs");

    static readonly HashSet<string> ClaimedLogs = new(StringComparer.OrdinalIgnoreCase);

    static readonly TimeSpan LogTimeout = TimeSpan.FromSeconds(90);

    static readonly Regex LinePrefix = new(@"^\[[\d.:-]+\]\[\s*\d+\]", RegexOptions.Compiled);

    const string LoadMap = "LogLoad: LoadMap: ";
    const string FrontendUp = "Bringing World /Game/Maps/Frontend";
    const string LoginStats = "[AutoLoginStats] [Result=";

    readonly string role, login, lobbyDll;
    readonly Process process;
    readonly DateTime startedAt;
    readonly bool redirectExpected;
    readonly Stopwatch uptime = Stopwatch.StartNew();
    readonly Thread watcher;

    string? logFile;
    string? loginError;
    bool inLobby;
    bool loginShown;
    volatile bool stopping;

    public GameSession(string role, Process process, string login, DateTime startedAt, string lobbyDll, bool redirectExpected)
    {
        this.role = role;
        this.process = process;
        this.login = login;
        this.startedAt = startedAt;
        this.lobbyDll = lobbyDll;
        this.redirectExpected = redirectExpected;

        watcher = new Thread(Watch) { IsBackground = true, Name = $"{role} log" };
        watcher.Start();
    }

    public bool HasEnded => !watcher.IsAlive;

    public void MarkStopping() => stopping = true;

    public bool WaitForExit(TimeSpan? timeout = null) => watcher.Join(timeout ?? Timeout.InfiniteTimeSpan);

    void Watch()
    {
        try
        {
            logFile = WaitForLog();
            if (logFile != null)
            {
                Log.Detail($"[{role}] Following {Path.GetFileName(logFile)}");
                Follow(logFile);
            }

            process.WaitForExit();
            ReportExit();
        }
        catch (Exception e)
        {
            Error($"Log watcher failed: {e.Message}");
        }
        finally
        {
            if (logFile != null) lock (ClaimedLogs) ClaimedLogs.Remove(logFile);
        }
    }

    string? WaitForLog()
    {
        while (!process.HasExited)
        {
            if (FindLog() is { } file) return file;
            if (uptime.Elapsed > LogTimeout)
            {
                Warn($"No log with -AUTH_LOGIN={login} showed up in {LogDir}, so only the exit will be reported");
                return null;
            }
            Thread.Sleep(500);
        }
        return null;
    }

    string? FindLog()
    {
        if (!Directory.Exists(LogDir)) return null;
        string token = $"-AUTH_LOGIN={login}";

        foreach (var file in Directory.GetFiles(LogDir, "FortniteGame*.log"))
        {
            if (file.Contains("-backup-")) continue;
            lock (ClaimedLogs) if (ClaimedLogs.Contains(file)) continue;

            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                if (!IsFromThisRun(file, reader.ReadLine())) continue;

                for (int i = 0; i < 5000 && reader.ReadLine() is { } line; i++)
                {
                    if (!line.Contains("Command Line:")) continue;
                    if (!line.Split(' ').Contains(token)) break;
                    lock (ClaimedLogs)
                        if (!ClaimedLogs.Add(file)) break;
                    return file;
                }
            }
            catch (IOException) { }
        }
        return null;
    }

    bool IsFromThisRun(string file, string? firstLine)
    {
        const string stamp = "Log file open, ";
        var earliest = startedAt.AddSeconds(-2);

        if (firstLine != null && firstLine.StartsWith(stamp) &&
            DateTime.TryParseExact(firstLine[stamp.Length..].Trim(), "MM/dd/yy HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var opened))
            return opened >= earliest;

        return File.GetLastWriteTime(file) >= earliest;
    }

    void Follow(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var pending = new StringBuilder();
        var buffer = new char[64 * 1024];

        while (true)
        {
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                if (process.HasExited) return;
                Thread.Sleep(250);
                continue;
            }

            pending.Append(buffer, 0, read);
            string text = pending.ToString();
            int lastNewline = text.LastIndexOf('\n');
            if (lastNewline < 0) continue;
            pending.Clear().Append(text, lastNewline + 1, text.Length - lastNewline - 1);

            foreach (var line in text[..lastNewline].Split('\n'))
                HandleLine(line.TrimEnd('\r'));
        }
    }

    void HandleLine(string line)
    {
        if (line.Contains("HttpProxyAddress = '"))
            Info($"Game is using HTTP proxy {Between(line, "HttpProxyAddress = '", "'")}");
        else if (line.Contains("bUseHttpProxy = false") && !redirectExpected)
            Warn("Game is not using an HTTP proxy, so its traffic won't be redirected");
        else if (line.Contains("[process_user_login] Successfully logged in"))
            ReportLogin(Field(line, "DisplayName"), Field(line, "UserId"));
        else if (line.Contains("HandleLoginChanged") && line.Contains("LoginStatus=[LoggedIn]"))
            ReportLogin(login, Field(line, "LocalUserId"));
        else if (line.Contains(FrontendUp) && line.Contains("up for play"))
            OnFrontend();
        else if (line.Contains(LoadMap))
            Info($"Loading map {line[(line.IndexOf(LoadMap, StringComparison.Ordinal) + LoadMap.Length)..].Split('?')[0]}");
        else if (line.Contains("[login_failed]"))
            loginError = Field(line, "DetailedError");
        else if (line.Contains(LoginStats))
            OnLoginStats(Field(line, "Result") ?? "unknown");
        else if (line.Contains("Fatal error"))
            Error(Shorten(LinePrefix.Replace(line, "")));
    }

    void OnFrontend()
    {
        if (inLobby) return;

        inLobby = true;
        Ok($"In lobby after {Format(uptime.Elapsed)}");
        if (lobbyDll != "") InjectLobbyDll();
    }

    void OnLoginStats(string result)
    {
        if (result == "Success") return;
        Error(loginError != null ? $"Login failed ({result}): {loginError}" : $"Login failed ({result})");
    }

    void ReportLogin(string? name, string? id)
    {
        if (loginShown) return;
        loginShown = true;

        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id) && name != id)
            Ok($"Logged in as {name} ({id})");
        else
            Ok($"Logged in as {name ?? id ?? login}");
    }

    void InjectLobbyDll()
    {
        string name = Path.GetFileName(lobbyDll);
        try
        {
            Injector.Inject(process.Handle, lobbyDll);
            Ok($"Injected lobby DLL {name}");
        }
        catch (Exception e)
        {
            Error($"Injecting lobby DLL {name} failed: {e.Message}");
        }
    }

    void ReportExit()
    {
        string after = $"after {Format(uptime.Elapsed)}";
        string code = $"code 0x{process.ExitCode:X}";

        if (stopping) Info($"Stopped {after}");
        else if (inLobby) Info($"Exited with {code} {after}");
        else if (logFile == null) Error($"Exited with {code} {after}, before writing a log");
        else Error($"Exited with {code} {after}, before reaching the lobby");
    }

    void Info(string message) => Log.Info($"[{role}] {message}");
    void Ok(string message) => Log.Ok($"[{role}] {message}");
    void Warn(string message) => Log.Warn($"[{role}] {message}");
    void Error(string message) => Log.Error($"[{role}] {message}");

    static string? Between(string line, string open, string close)
    {
        int start = line.IndexOf(open, StringComparison.Ordinal);
        if (start < 0) return null;
        start += open.Length;
        int end = line.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : line[start..end];
    }

    static string? Field(string line, string name)
    {
        int start = line.IndexOf($"{name}=", StringComparison.Ordinal);
        if (start < 0) return null;
        start += name.Length + 1;
        if (start < line.Length && line[start] == '[') start++;

        int depth = 1;
        for (int i = start; i < line.Length; i++)
        {
            if (line[i] == '[') depth++;
            else if (line[i] == ']' && --depth == 0) return line[start..i];
        }
        return null;
    }

    static string Shorten(string text) => text.Length > 200 ? text[..200] + "..." : text;

    static string Format(TimeSpan time) =>
        time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes}m {time.Seconds}s" : $"{time.Seconds}s";
}
