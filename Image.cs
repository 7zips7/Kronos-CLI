using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http;
using SdImage = System.Drawing.Image;

namespace KronosCLI;

internal sealed class Image
{
    readonly string buildRoot;
    readonly List<SwapRecord> records = [];
    readonly object sync = new();

    sealed class SwapRecord
    {
        public required string Target { get; init; }
        public string? Backup { get; init; }
        public required string Temp { get; init; }
    }

    public Image(string buildRoot) => this.buildRoot = buildRoot ?? string.Empty;

    public void SwapDirectory(string dir, string imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            Log.Warn("Image: no image URL given");
            return;
        }

        if (string.IsNullOrWhiteSpace(dir)) dir = buildRoot;
        else if (!Path.IsPathRooted(dir)) dir = Path.Combine(buildRoot, dir);

        if (!Directory.Exists(dir))
        {
            Log.Detail($"Image: directory not found, skipping: {dir}");
            return;
        }

        var targets = Directory.EnumerateFiles(dir)
            .Where(IsSwappable)
            .Where(f => !AlreadySwapped(f))
            .ToList();
        if (targets.Count == 0)
        {
            Log.Detail($"Image: no .bmp/.png files to replace in {dir}");
            return;
        }

        byte[] data;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            data = client.GetByteArrayAsync(imageUrl).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Log.Warn($"Image: download failed ({imageUrl}): {e.Message}");
            return;
        }

        foreach (var target in targets)
            SwapFile(target, data);
    }

    void SwapFile(string target, byte[] data)
    {
        string ext = Path.GetExtension(target).ToLowerInvariant();
        string temp = Path.Combine(Path.GetTempPath(), "fl_" + Path.GetRandomFileName() + ext);

        try
        {
            var (width, height) = OriginalSize(target);
            using var stream = new MemoryStream(data);
            using var source = new Bitmap(stream);
            Convert(source, width > 0 ? width : source.Width, height > 0 ? height : source.Height, ext, temp);
        }
        catch (Exception e)
        {
            Log.Warn($"Image: couldn't decode the downloaded image for {Path.GetFileName(target)}: {e.Message}");
            TryDelete(temp);
            return;
        }

        try
        {
            string? backup = null;
            if (File.Exists(target))
            {
                backup = target + ".flbak";
                if (File.Exists(backup)) backup = target + ".flbak." + DateTime.Now.Ticks;
                File.Move(target, backup);
            }

            File.Copy(temp, target, overwrite: true);
            lock (sync) records.Add(new SwapRecord { Target = target, Backup = backup, Temp = temp });
            Log.Detail($"Image: replaced {target}" + (backup != null ? $" (backup {Path.GetFileName(backup)})" : ""));
        }
        catch (UnauthorizedAccessException)
        {
            Log.Warn($"Image: can't write {target} - run the launcher as administrator to replace files under {Path.GetDirectoryName(target)}");
            TryDelete(temp);
        }
        catch (Exception e)
        {
            Log.Warn($"Image: couldn't replace {Path.GetFileName(target)}: {e.Message}");
            TryDelete(temp);
        }
    }

    static void Convert(Bitmap source, int width, int height, string ext, string temp)
    {
        bool png = ext == ".png";
        using var canvas = new Bitmap(width, height, png ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        }
        canvas.Save(temp, png ? ImageFormat.Png : ImageFormat.Bmp);
    }

    static (int Width, int Height) OriginalSize(string path)
    {
        try
        {
            if (!File.Exists(path)) return (0, 0);
            using var stream = File.OpenRead(path);
            using var image = SdImage.FromStream(stream, false, false);
            return (image.Width, image.Height);
        }
        catch { return (0, 0); }
    }

    public void RestoreAll()
    {
        lock (sync)
        {
            for (int i = records.Count - 1; i >= 0; i--)
            {
                var r = records[i];
                try
                {
                    if (File.Exists(r.Target)) File.Delete(r.Target);
                    if (r.Backup != null && File.Exists(r.Backup)) File.Move(r.Backup, r.Target);
                }
                catch (Exception e)
                {
                    Log.Warn($"Image: couldn't restore {Path.GetFileName(r.Target)}: {e.Message}");
                }
                TryDelete(r.Temp);
            }
            records.Clear();
        }
    }

    bool AlreadySwapped(string file)
    {
        lock (sync) return records.Any(r => string.Equals(r.Target, file, StringComparison.OrdinalIgnoreCase));
    }

    static bool IsSwappable(string file) => Path.GetExtension(file).ToLowerInvariant() is ".bmp" or ".png";

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
