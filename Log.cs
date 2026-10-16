namespace KronosCLI;

internal static class Log
{
    public static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "launcher.log");

    static readonly object Sync = new();
    static bool atPrompt;

    public static void Reset()
    {
        try { File.WriteAllText(FilePath, ""); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static void Info(string message) => Emit(Ui.Level.Info, "[*]", ConsoleColor.Cyan, message);
    public static void Ok(string message) => Emit(Ui.Level.Ok, "[+]", ConsoleColor.Green, message);
    public static void Warn(string message) => Emit(Ui.Level.Warn, "[!]", ConsoleColor.Yellow, message);
    public static void Error(string message) => Emit(Ui.Level.Error, "[-]", ConsoleColor.Red, message);
    public static void Detail(string message) => Emit(Ui.Level.Detail, "[.]", ConsoleColor.DarkGray, message);

    public static void Raw(string text)
    {
        if (Ui.Active)
        {
            foreach (var line in text.Split('\n'))
                if (line.Trim().Length > 0) Ui.Add(Ui.Level.Plain, line.TrimEnd('\r'));
            return;
        }
        lock (Sync) System.Console.WriteLine(text);
    }

    public static string? ReadCommand()
    {
        if (Ui.Active)
        {
            string command = Ui.ReadLine("› ", false);
            Append("> " + command);
            return command;
        }

        lock (Sync) { System.Console.Write("> "); atPrompt = true; }
        string? typed = System.Console.ReadLine()?.Trim();
        lock (Sync) { atPrompt = false; if (typed != null) Append("> " + typed); }
        return typed;
    }

    public static string? ReadValue(string label, bool secret = false, string example = "", IReadOnlyList<Ui.Button>? buttons = null)
    {
        if (Ui.Active)
        {
            string value = Ui.ReadLine(label, secret, example, buttons);
            if (value.Length > 0) Append(label + (secret ? "***" : value));
            return value.Length == 0 ? null : value;
        }

        lock (Sync) System.Console.Write(label);
        string? typed = System.Console.ReadLine()?.Trim();
        if (!string.IsNullOrEmpty(typed)) lock (Sync) Append(label + (secret ? "***" : typed));
        return string.IsNullOrEmpty(typed) ? null : typed;
    }

    static readonly Ui.Button[] YesNo = [new("Yes", "y"), new("No", "n")];

    public static bool Confirm(string question, bool defaultYes)
    {
        string? answer = ReadValue($"{question} [{(defaultYes ? "Y/n" : "y/N")}] ", buttons: YesNo);
        return answer == null ? defaultYes : answer.ToLowerInvariant() is "y" or "yes";
    }

    static void Emit(Ui.Level level, string tag, ConsoleColor color, string message)
    {
        Append($"{tag} {message}");
        if (Ui.Active)
        {
            Ui.Add(level, message);
            return;
        }

        lock (Sync)
        {
            if (atPrompt) System.Console.Write("\r  \r");
            System.Console.ForegroundColor = ConsoleColor.DarkGray;
            System.Console.Write($"{DateTime.Now:HH:mm:ss} ");
            System.Console.ForegroundColor = color;
            System.Console.Write(tag);
            System.Console.ResetColor();
            System.Console.WriteLine($" {message}");
            if (atPrompt) System.Console.Write("> ");
        }
    }

    static void Append(string line)
    {
        try { File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
