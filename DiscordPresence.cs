using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using DiscordRPC;

namespace KronosCLI;

// One connection lives for as long as the launcher is open.
internal sealed class DiscordPresence : IDisposable
{
    // Copy your existing Discord Application ID here before building.
    private const string ApplicationId = "1564627952862363739";

    // Paste your GitHub profile or repository URL between the quotes.
    private const string GitHubUrl = "https://github.com/7zips7/Kronos-CLI";

    // Change this to the name you want displayed on the Discord button.
    private const string GitHubButtonLabel = "GitHub";

    // Paste the original repository URL here and choose its button name.
    private const string OriginalRepoUrl = "https://github.com/Helix-Dev-Q/Kronos-CLI";
    private const string OriginalRepoButtonLabel = "Original Repository";

    private sealed class TrackedGame(string version)
    {
        public string Version { get; } = version;
        public Timestamps StartedAt { get; } = Timestamps.Now;
    }

    private readonly object gate = new();
    private readonly List<TrackedGame> games = new();
    private DiscordRpcClient? client;
    private bool disposed;

    public DiscordPresence()
    {
        try
        {
            client = new DiscordRpcClient(ApplicationId);
            client.Initialize();
            UpdatePresence();
        }
        catch (Exception ex)
        {
            global::System.Console.Error.WriteLine($"Discord Rich Presence error: {ex.Message}");
            try { client?.Dispose(); }
            catch (Exception disposeError)
            {
                global::System.Console.Error.WriteLine($"Discord Rich Presence cleanup error: {disposeError.Message}");
            }
            client = null;
        }
    }

    public async Task TrackGameAsync(Process gameProcess, string version)
    {
        TrackedGame game;
        lock (gate)
        {
            if (disposed || client == null) return;
            game = new TrackedGame(version);
            games.Add(game);
            UpdatePresence();
        }

        try
        {
            await gameProcess.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            global::System.Console.Error.WriteLine($"Discord game tracking error: {ex.Message}");
        }
        finally
        {
            lock (gate)
            {
                games.Remove(game);
                if (!disposed) UpdatePresence();
            }
        }
    }

    // Called under gate after construction. Most recently started client wins.
    private void UpdatePresence()
    {
        if (client == null) return;

        var presence = games.Count == 0
            ? new RichPresence
            {
                Details = "Waiting to launch a build",
                State = ""
            }
            : new RichPresence
            {
                Details = $"Fortnite version: {games[^1].Version}",
                State = "In game",
                Timestamps = games[^1].StartedAt
            };

        var buttons = new List<DiscordRPC.Button>();
        if (!string.IsNullOrWhiteSpace(GitHubUrl))
        {
            buttons.Add(new DiscordRPC.Button { Label = GitHubButtonLabel, Url = GitHubUrl });
        }
        if (!string.IsNullOrWhiteSpace(OriginalRepoUrl))
        {
            buttons.Add(new DiscordRPC.Button { Label = OriginalRepoButtonLabel, Url = OriginalRepoUrl });
        }
        if (buttons.Count > 0)
        {
            presence.Buttons = buttons.ToArray();
        }

        try { client.SetPresence(presence); }
        catch (Exception ex)
        {
            global::System.Console.Error.WriteLine($"Discord Rich Presence error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            games.Clear();
            try { client?.ClearPresence(); }
            catch (Exception ex)
            {
                global::System.Console.Error.WriteLine($"Discord Rich Presence clear error: {ex.Message}");
            }
            try { client?.Dispose(); }
            catch (Exception ex)
            {
                global::System.Console.Error.WriteLine($"Discord Rich Presence cleanup error: {ex.Message}");
            }
            client = null;
        }
    }
}
