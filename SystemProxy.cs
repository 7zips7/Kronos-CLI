using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KronosCLI;

internal sealed record ProxySettings(int Flags, string? Server, string? Bypass, string? AutoConfigUrl);

internal static class SystemProxy
{
    static readonly string BackupPath = Path.Combine(AppContext.BaseDirectory, "proxy-backup.json");

    public static void RestoreLeftover()
    {
        try
        {
            if (!File.Exists(BackupPath)) return;
            if (JsonSerializer.Deserialize<ProxySettings>(File.ReadAllText(BackupPath)) is { } settings) Apply(settings);
            File.Delete(BackupPath);
            Log.Info("Restored the Windows proxy settings an earlier build left behind");
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Couldn't restore the Windows proxy settings from {BackupPath}: {e.Message}");
        }
    }

    static void Apply(ProxySettings settings)
    {
        IntPtr server = Marshal.StringToHGlobalUni(settings.Server);
        IntPtr bypass = Marshal.StringToHGlobalUni(settings.Bypass);
        IntPtr autoConfigUrl = Marshal.StringToHGlobalUni(settings.AutoConfigUrl);
        try
        {
            Call(
            [
                new() { dwOption = Native.INTERNET_PER_CONN_FLAGS, dwValue = settings.Flags },
                new() { dwOption = Native.INTERNET_PER_CONN_PROXY_SERVER, pszValue = server },
                new() { dwOption = Native.INTERNET_PER_CONN_PROXY_BYPASS, pszValue = bypass },
                new() { dwOption = Native.INTERNET_PER_CONN_AUTOCONFIG_URL, pszValue = autoConfigUrl },
            ]);
            Native.InternetSetOption(IntPtr.Zero, Native.INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            Native.InternetSetOption(IntPtr.Zero, Native.INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(server);
            Marshal.FreeHGlobal(bypass);
            Marshal.FreeHGlobal(autoConfigUrl);
        }
    }

    static void Call(Native.INTERNET_PER_CONN_OPTION[] options)
    {
        var pinned = GCHandle.Alloc(options, GCHandleType.Pinned);
        try
        {
            var list = new Native.INTERNET_PER_CONN_OPTION_LIST
            {
                dwSize = Marshal.SizeOf<Native.INTERNET_PER_CONN_OPTION_LIST>(),
                dwOptionCount = options.Length,
                pOptions = pinned.AddrOfPinnedObject(),
            };
            if (!Native.InternetSetOption(IntPtr.Zero, Native.INTERNET_OPTION_PER_CONNECTION_OPTION, ref list, list.dwSize))
                throw new Win32Exception();
        }
        finally
        {
            pinned.Free();
        }
    }
}
