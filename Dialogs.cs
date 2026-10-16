using System.Runtime.InteropServices;

namespace KronosCLI;

internal static class Dialogs
{
    const uint FOS_PICKFOLDERS = 0x20;
    const uint FOS_FORCEFILESYSTEM = 0x40;
    const uint FOS_PATHMUSTEXIST = 0x800;
    const uint FOS_FILEMUSTEXIST = 0x1000;
    const uint SIGDN_FILESYSPATH = 0x80058000;

    public static string? PickFolder(string title, string? start) => Show(title, start, true, null, null);

    public static string? PickFile(string title, string filterName, string filterSpec, string? start) =>
        Show(title, start, false, filterName, filterSpec);

    static string? Show(string title, string? start, bool folders, string? filterName, string? filterSpec)
    {
        string? result = null;
        var thread = new Thread(() =>
        {
            try { result = ShowDialog(title, start, folders, filterName, filterSpec); }
            catch (Exception e) { Log.Detail($"Browse dialog failed: {e.Message}"); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    static string? ShowDialog(string title, string? start, bool folders, string? filterName, string? filterSpec)
    {
        var dialog = (IFileDialog)new FileOpenDialog();
        try
        {
            dialog.GetOptions(out uint options);
            dialog.SetOptions(options | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST | (folders ? FOS_PICKFOLDERS : FOS_FILEMUSTEXIST));
            dialog.SetTitle(title);
            if (!folders && filterName != null && filterSpec != null)
                dialog.SetFileTypes(1, [new FilterSpec { Name = filterName, Spec = filterSpec }]);
            if (StartFolder(start) is { } folder && SHCreateItemFromParsingName(folder, IntPtr.Zero, typeof(IShellItem).GUID, out var item) == 0)
                dialog.SetFolder(item);

            if (dialog.Show(GetConsoleWindow()) != 0) return null;
            dialog.GetResult(out var chosen);
            chosen.GetDisplayName(SIGDN_FILESYSPATH, out string path);
            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    static string? StartFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string full = Path.GetFullPath(path);
            if (Directory.Exists(full)) return full;
            string? parent = Path.GetDirectoryName(full);
            return parent != null && Directory.Exists(parent) ? parent : null;
        }
        catch { return null; }
    }

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    class FileOpenDialog { }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct FilterSpec
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        [MarshalAs(UnmanagedType.LPWStr)] public string Spec;
    }

    [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFileDialog
    {
        [PreserveSig] int Show(IntPtr parent);
        void SetFileTypes(uint count, [MarshalAs(UnmanagedType.LPArray)] FilterSpec[] filters);
        void SetFileTypeIndex(uint index);
        void GetFileTypeIndex(out uint index);
        void Advise(IntPtr events, out uint cookie);
        void Unadvise(uint cookie);
        void SetOptions(uint options);
        void GetOptions(out uint options);
        void SetDefaultFolder(IShellItem item);
        void SetFolder(IShellItem item);
        void GetFolder(out IShellItem item);
        void GetCurrentSelection(out IShellItem item);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
        void GetResult(out IShellItem item);
        void AddPlace(IShellItem item, int placement);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
        void Close(int result);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr filter);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr context, ref Guid handler, ref Guid riid, out IntPtr result);
        void GetParent(out IShellItem parent);
        void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        void GetAttributes(uint mask, out uint attributes);
        void Compare(IShellItem other, uint hint, out int order);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr context,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItem item);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();
}
