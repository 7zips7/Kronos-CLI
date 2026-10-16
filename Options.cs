using System.Text.Json;

namespace KronosCLI;

internal sealed class Options
{
    static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KronosCLI", "config.json");

    sealed class Stored
    {
        public string? BuildPath { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public string? Backend { get; set; }
        public string? Proxy { get; set; }
        public string? RedirectDll { get; set; }
        public string? ClientLobbyDll { get; set; }
        public string? ServerLobbyDll { get; set; }
    }

    public string BuildPath { get; set; } = @"C:\Program Files\Epic Games\Fortnite";
    public string Username { get; set; } = "Player";
    public string Password { get; set; } = "Player123";
    public string Backend { get; set; } = "http://127.0.0.1:3551";
    public string Proxy { get; set; } = "127.0.0.1:8888";  // idk why you would change this , but it's here if you want to
    public string RedirectDll { get; set; } = "";
    public string ClientLobbyDll { get; set; } = "";
    public string ServerLobbyDll { get; set; } = "";

    public string? Mode { get; private set; }
    public bool NoProxy { get; private set; }
    public bool ShowHelp { get; private set; }
    public List<string> GameArgs { get; } = [];
    public List<string> ReplaceDirs { get; } = [];

    public const string Usage = """
        Kronos CLI [options] [game arguments]

          --mode client|server|both   Launch, wait for the game to exit, then quit (no menu)
          --path <dir|exe>            Build root, its Win64 folder, or the game exe
          --user <name>               Client login
          --pass <password>           Password for the client and the server
          --backend <url>             Private backend that Epic traffic is redirected to
          --proxy <ip:port>           Where the built-in redirect proxy listens
          --no-proxy                  Launch the client without the proxy on 30.10+ builds
                                      (older builds, the server, and both never use the proxy)
          --redirect-dll <path>       DLL injected at startup when the proxy is declined
          --client-lobby-dll <path>   DLL injected into the client in the lobby (redirect DLL launches only)
          --server-lobby-dll <path>   DLL injected into the server in the lobby (redirect DLL launches only)
          --                          Pass everything after it to the game untouched

        Unrecognised arguments are passed to the game.
        """;

    public string Win64Dir => string.IsNullOrWhiteSpace(BuildPath)
        ? string.Empty
        : File.Exists(BuildPath)
            ? Path.GetDirectoryName(Path.GetFullPath(BuildPath))!
            : File.Exists(Path.Combine(BuildPath, "FortniteClient-Win64-Shipping.exe"))
                ? BuildPath
                : Path.Combine(BuildPath, "FortniteGame", "Binaries", "Win64");

    public string GameExeFor(bool eac) => string.IsNullOrWhiteSpace(BuildPath)
        ? string.Empty
        : Path.Combine(Win64Dir,
            eac ? "FortniteClient-Win64-Shipping_EAC_EOS.exe" : "FortniteClient-Win64-Shipping.exe");

    public string BuildRoot
    {
        get
        {
            if (string.IsNullOrWhiteSpace(BuildPath)) return string.Empty;
            try { return Path.GetFullPath(Path.Combine(Win64Dir, "..", "..", "..")); }
            catch { return BuildPath; }
        }
    }

    public static string SavedAt => ConfigPath;

    void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(ConfigPath));
            if (stored == null) return;

            if (!string.IsNullOrWhiteSpace(stored.BuildPath)) BuildPath = stored.BuildPath;
            if (!string.IsNullOrWhiteSpace(stored.Username)) Username = stored.Username;
            if (!string.IsNullOrWhiteSpace(stored.Password)) Password = stored.Password;
            if (!string.IsNullOrWhiteSpace(stored.Backend)) Backend = stored.Backend;
            if (!string.IsNullOrWhiteSpace(stored.Proxy)) Proxy = stored.Proxy;
            if (stored.RedirectDll != null) RedirectDll = stored.RedirectDll;
            if (stored.ClientLobbyDll != null) ClientLobbyDll = stored.ClientLobbyDll;
            if (stored.ServerLobbyDll != null) ServerLobbyDll = stored.ServerLobbyDll;
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            var stored = new Stored
            {
                BuildPath = BuildPath,
                Username = Username,
                Password = Password,
                Backend = Backend,
                Proxy = Proxy,
                RedirectDll = RedirectDll,
                ClientLobbyDll = ClientLobbyDll,
                ServerLobbyDll = ServerLobbyDll,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Log.Detail($"Config: couldn't save {ConfigPath}: {e.Message}");
        }
    }

    public static Options Parse(string[] argv)
    {
        var options = new Options();
        options.LoadConfig();

        for (int i = 0; i < argv.Length; i++)
        {
            string Value() => i + 1 < argv.Length
                ? argv[++i]
                : throw new ArgumentException($"{argv[i]} needs a value.");

            switch (argv[i])
            {
                case "-h" or "--help" or "/?": options.ShowHelp = true; break;
                case "--mode": options.Mode = Value().ToLowerInvariant(); break;
                case "--path": options.BuildPath = Value(); break;
                case "--user": options.Username = Value(); break;
                case "--pass": options.Password = Value(); break;
                case "--backend": options.Backend = Value(); break;
                case "--proxy": options.Proxy = Value(); break;
                case "--no-proxy": options.NoProxy = true; break;
                case "--redirect-dll": options.RedirectDll = Path.GetFullPath(Value()); break;
                case "--replace-dir": options.ReplaceDirs.Add(Value()); break;
                case "--client-lobby-dll": options.ClientLobbyDll = Path.GetFullPath(Value()); break;
                case "--server-lobby-dll": options.ServerLobbyDll = Path.GetFullPath(Value()); break;
                case "--": options.GameArgs.AddRange(argv[(i + 1)..]); i = argv.Length; break;
                default: options.GameArgs.Add(argv[i]); break;
            }
        }

        return options;
    }
}
