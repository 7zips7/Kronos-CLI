using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DiscordRPC;

namespace KronosCLI;

internal static class DiscordPresence
{
    // Replace this with your Discord application's ID.
    private const string ApplicationId = "1564627952862363739";

    public static async Task TrackGameAsync(Process gameProcess, string version)
    {
        try
        {
            using var client = new DiscordRpcClient(ApplicationId);

            client.Initialize();
            client.SetPresence(new RichPresence
            {
                Details = $"Fortnite version: {version}",
                State = "In game",
                Timestamps = Timestamps.Now
            });

            // Keep the presence active until this process closes.
            await gameProcess.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            global::System.Console.WriteLine($"Discord Rich Presence error: {ex.Message}");

        }
    }
}
