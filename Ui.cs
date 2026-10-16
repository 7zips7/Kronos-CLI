using System.Text;

namespace KronosCLI;

internal static class Ui
{
    public enum Level { Plain, Info, Ok, Warn, Error, Detail, Proxy }

    public sealed record Button(string Label, string? Value = null, Func<string?>? Action = null);

    readonly record struct Entry(DateTime Time, Level Level, string Text);
    readonly record struct Span(string Text, string Fg, string? Bg = null);

    enum HitKind { Command, Button, Tab, Follow, Body, Bar }
    readonly record struct Hit(int Row, int Left, int Right, HitKind Kind, int Pane = 0, string? Value = null, Button? Button = null);

    const string Esc = "\x1b";
    const string Reset = Esc + "[0m";
    const string MouseOn = Esc + "[?1000h" + Esc + "[?1002h" + Esc + "[?1006h";
    const string MouseOff = Esc + "[?1006l" + Esc + "[?1002l" + Esc + "[?1000l";

    static readonly string Backdrop = Bg(8, 8, 8);
    static readonly string Panel = Bg(20, 20, 20);
    static readonly string ChipBg = Bg(46, 46, 46);
    static readonly string GreyBg = Bg(150, 150, 150);
    static readonly string KeyBg = Bg(205, 205, 205);
    static readonly string LightBg = Bg(235, 235, 235);

    static readonly string Ink = Fg(10, 10, 10);
    static readonly string Border = Fg(60, 60, 60);
    static readonly string Faint = Fg(105, 105, 105);
    static readonly string Dim = Fg(150, 150, 150);
    static readonly string Text = Fg(208, 208, 208);
    static readonly string Bright = Fg(245, 245, 245);
    static readonly string Thumb = Fg(220, 220, 220);

    const string Title = "Kronos CLI";
    const int MaxLines = 600;
    const int MarginX = 2;
    const int MarginY = 1;
    const int Indent = 12;
    const int Col0 = MarginX + 2;

    static readonly object sync = new();
    static readonly object draw = new();
    static readonly List<Entry> activity = [];
    static readonly List<Entry> proxyLines = [];
    static readonly List<(string Key, string Value, Level Kind)> status = [];

    static string hint = "";
    static string inputLabel = "";
    static string inputExample = "";
    static string input = "";
    static IReadOnlyList<Button> inputButtons = [];
    static bool prompting;
    static bool secret;

    static int focus;
    static readonly int[] offset = new int[2];
    static readonly int[] maxOffset = new int[2];
    static readonly int[] paneTop = [0, 0];
    static readonly int[] paneHeight = [1, 1];
    static int lastInner = 80;
    static List<Hit> hits = [];

    static bool mouse;
    static bool leftDown;
    static int dragPane = -1;

    static Thread? renderThread;
    static volatile bool running;
    static volatile bool dirty = true;
    static int lastWidth, lastHeight, lastSecond = -1;

    public static bool Active => running;

    public static void Start()
    {
        if (running) return;
        running = true;
        try { System.Console.OutputEncoding = Encoding.UTF8; } catch { }
        Write(Esc + "[?1049h" + Esc + "[?25l" + Esc + "[?7l" + Backdrop + Esc + "[2J");
        mouse = Console.TryEnableMouse();
        if (mouse) Write(MouseOn);
        renderThread = new Thread(RenderLoop) { IsBackground = true, Name = "ui" };
        renderThread.Start();
    }

    public static void Stop()
    {
        if (!running) return;
        running = false;
        try { renderThread?.Join(400); } catch { }
        Write(Reset + (mouse ? MouseOff : "") + Esc + "[?7h" + Esc + "[?25h" + Esc + "[?1049l");
        Console.RestoreInput();
    }

    public static void Add(Level level, string message)
    {
        bool isProxy = message.StartsWith("Proxy:", StringComparison.Ordinal);
        if (!isProxy && level == Level.Detail) return;

        string text = isProxy ? message[6..].TrimStart() : message;
        Level shown = isProxy && level is Level.Plain or Level.Info ? Level.Proxy : level;

        lock (sync)
        {
            int pane = isProxy ? 1 : 0;
            var list = isProxy ? proxyLines : activity;
            var entry = new Entry(DateTime.Now, shown, text);
            list.Add(entry);
            if (list.Count > MaxLines) list.RemoveRange(0, list.Count - MaxLines);
            if (offset[pane] > 0) offset[pane] += Wrap(entry.Text, MessageWidth(lastInner)).Count;
        }
        dirty = true;
    }

    public static void SetStatus(string key, string value, Level kind)
    {
        lock (sync)
        {
            int i = status.FindIndex(s => s.Key == key);
            if (i >= 0) status[i] = (key, value, kind);
            else status.Add((key, value, kind));
        }
        dirty = true;
    }

    public static void SetHint(string text)
    {
        lock (sync) hint = text;
        dirty = true;
    }

    public static void Clear()
    {
        lock (sync)
        {
            activity.Clear();
            proxyLines.Clear();
            offset[0] = offset[1] = 0;
        }
        dirty = true;
    }

    public static string ReadLine(string label, bool secret = false, string example = "", IReadOnlyList<Button>? buttons = null)
    {
        lock (sync)
        {
            inputLabel = label;
            inputExample = example;
            input = "";
            inputButtons = buttons ?? [];
            Ui.secret = secret;
            prompting = true;
        }
        dirty = true;

        string? chosen = mouse ? ReadWithMouse() : ReadWithKeys();

        string result;
        lock (sync)
        {
            result = (chosen ?? input).Trim();
            prompting = false;
            inputLabel = "";
            inputExample = "";
            input = "";
            inputButtons = [];
        }
        dirty = true;
        return result;
    }

    static string? ReadWithKeys()
    {
        while (running)
        {
            ConsoleKeyInfo key;
            try { key = System.Console.ReadKey(true); }
            catch { Thread.Sleep(50); continue; }

            if (key.Key == ConsoleKey.Enter) return null;
            lock (sync) OnKey((int)key.Key, key.KeyChar, 1);
            dirty = true;
        }
        return null;
    }

    static string? ReadWithMouse()
    {
        var pending = PendingKeys();
        var buffer = new Console.InputRecord[32];

        while (running)
        {
            if (pending.Count == 0)
            {
                int read = Console.ReadInput(buffer);
                if (read <= 0) { Thread.Sleep(20); continue; }
                for (int i = 0; i < read; i++) pending.Enqueue(buffer[i]);
            }

            var record = pending.Dequeue();
            if (record.EventType == Console.KeyEvent)
            {
                var key = record.Key;
                if (key.KeyDown == 0) continue;
                if (key.VirtualKey == 0x0D) return null;
                lock (sync) OnKey(key.VirtualKey, (char)key.Char, Math.Max(1, (int)key.RepeatCount));
                dirty = true;
            }
            else if (record.EventType == Console.MouseEvent && OnMouse(record.Mouse) is { } hit)
            {
                if (hit.Kind == HitKind.Command) return hit.Value;
                if (hit.Button?.Value is { } value) return value;
                if (hit.Button?.Action is { } action)
                {
                    string? picked = action();
                    if (picked != null) return picked;
                    pending = PendingKeys();
                }
            }
        }
        return null;
    }

    static Queue<Console.InputRecord> PendingKeys()
    {
        leftDown = false;
        dragPane = -1;

        var keys = new Queue<Console.InputRecord>();
        var buffer = new Console.InputRecord[64];
        while (Console.PendingInput() > 0)
        {
            int read = Console.ReadInput(buffer);
            if (read <= 0) break;
            for (int i = 0; i < read; i++)
                if (buffer[i].EventType == Console.KeyEvent) keys.Enqueue(buffer[i]);
        }
        return keys;
    }

    static void OnKey(int virtualKey, char ch, int repeat)
    {
        switch (virtualKey)
        {
            case 0x08: for (int i = 0; i < repeat && input.Length > 0; i++) input = input[..^1]; break;
            case 0x1B: input = ""; break;
            case 0x09: focus = 1 - focus; break;
            case 0x21: ScrollPane(focus, repeat * Math.Max(1, paneHeight[focus] - 1)); break;
            case 0x22: ScrollPane(focus, -repeat * Math.Max(1, paneHeight[focus] - 1)); break;
            case 0x26: ScrollPane(focus, repeat); break;
            case 0x28: ScrollPane(focus, -repeat); break;
            case 0x24: offset[focus] = maxOffset[focus]; break;
            case 0x23: offset[focus] = 0; break;
            default: if (ch != '\0' && !char.IsControl(ch)) input += new string(ch, repeat); break;
        }
    }

    static Hit? OnMouse(Console.MouseRecord record)
    {
        const uint Moved = 0x1, Wheeled = 0x4, Horizontal = 0x8;
        int x = record.X - WindowLeft();
        int y = record.Y - WindowTop();
        bool left = (record.Buttons & 1) != 0;

        lock (sync)
        {
            if ((record.Flags & Wheeled) != 0)
            {
                ScrollPane(PaneAt(y) ?? focus, (short)(record.Buttons >> 16) > 0 ? 3 : -3);
                dirty = true;
                return null;
            }
            if ((record.Flags & Horizontal) != 0) return null;
            if ((record.Flags & Moved) != 0)
            {
                if (left && dragPane >= 0)
                {
                    ScrollToBar(dragPane, y);
                    dirty = true;
                }
                return null;
            }

            bool pressed = left && !leftDown;
            leftDown = left;
            if (!left) dragPane = -1;
            if (!pressed) return null;

            Hit? found = null;
            foreach (var hit in hits)
                if (hit.Row == y && x >= hit.Left && x < hit.Right) found = hit;
            if (found is not { } target) return null;

            dirty = true;
            switch (target.Kind)
            {
                case HitKind.Tab:
                case HitKind.Body:
                    focus = target.Pane;
                    return null;
                case HitKind.Follow:
                    focus = target.Pane;
                    offset[target.Pane] = 0;
                    return null;
                case HitKind.Bar:
                    focus = target.Pane;
                    dragPane = target.Pane;
                    ScrollToBar(target.Pane, y);
                    return null;
                default:
                    return target;
            }
        }
    }

    static int? PaneAt(int y)
    {
        for (int pane = 0; pane < 2; pane++)
            if (y >= paneTop[pane] - 1 && y < paneTop[pane] + paneHeight[pane]) return pane;
        return null;
    }

    static void ScrollPane(int pane, int rows) => offset[pane] = Math.Clamp(offset[pane] + rows, 0, maxOffset[pane]);

    static void ScrollToBar(int pane, int y)
    {
        int count = paneHeight[pane];
        double fraction = count <= 1 ? 1 : Math.Clamp((double)(y - paneTop[pane]) / (count - 1), 0, 1);
        offset[pane] = maxOffset[pane] - (int)Math.Round(fraction * maxOffset[pane]);
    }

    static void RenderLoop()
    {
        while (running)
        {
            int w = SafeWidth(), h = SafeHeight();
            int second = DateTime.Now.Second;

            if (dirty || w != lastWidth || h != lastHeight || second != lastSecond)
            {
                if (w != lastWidth || h != lastHeight) { try { System.Console.Clear(); } catch { } }
                lastWidth = w; lastHeight = h; lastSecond = second; dirty = false;
                Write(Compose(w, h));
            }
            Thread.Sleep(30);
        }
    }

    internal static string Compose(int width, int height)
    {
        int w = Math.Max(56, width);
        int h = Math.Max(18, height);
        int cardW = w - 2 * MarginX;
        int inner = cardW - 4;

        List<Entry> act, prx;
        List<(string, string, Level)> st;
        List<string> promptLines;
        IReadOnlyList<Button> buttons;
        string hintCopy, example, shownInput;
        bool prompt;
        int activityH, proxyH, focused, leftWidth;
        lock (sync)
        {
            st = [.. status];
            hintCopy = hint;
            example = inputExample;
            shownInput = secret ? new string('•', input.Length) : input;
            prompt = prompting;
            buttons = inputButtons;

            int buttonsWidth = buttons.Sum(b => b.Label.Length + 2) + Math.Max(0, buttons.Count - 1);
            leftWidth = Math.Max(8, inner - (buttonsWidth > 0 ? buttonsWidth + 1 : 0));
            promptLines = PromptLines(inputLabel, shownInput, prompt, leftWidth, h);

            int bodyTotal = Math.Max(2, h - 2 * MarginY - 9 - Math.Max(0, promptLines.Count - 2));
            proxyH = Math.Max(1, bodyTotal * 2 / 5);
            activityH = Math.Max(1, bodyTotal - proxyH);
            act = [.. activity];
            prx = [.. proxyLines];
            focused = focus;
            lastInner = inner;
        }

        var actRows = PaneRows(act, inner, proxyPane: false);
        var prxRows = PaneRows(prx, inner, proxyPane: true);

        int actOffset, prxOffset;
        lock (sync)
        {
            paneHeight[0] = activityH;
            paneHeight[1] = proxyH;
            maxOffset[0] = Math.Max(0, actRows.Count - activityH);
            maxOffset[1] = Math.Max(0, prxRows.Count - proxyH);
            actOffset = offset[0] = Math.Min(offset[0], maxOffset[0]);
            prxOffset = offset[1] = Math.Min(offset[1], maxOffset[1]);
        }

        string label = promptLines[^1];
        int room = leftWidth - 1 - label.Length;
        if (shownInput.Length > room) shownInput = room > 1 ? "…" + shownInput[^(room - 1)..] : "";

        var rows = new List<string>(h);
        var newHits = new List<Hit>();
        for (int i = 0; i < MarginY; i++) rows.Add(MarginRow(w));
        rows.Add(TopBorder(cardW));
        rows.Add(Card(BrandSpans(inner), inner));
        rows.Add(Card(StatusSpans(st), inner));

        int actTop = AddPane(rows, newHits, "ACTIVITY", 0, focused == 0, actRows, activityH, actOffset, inner);
        int prxTop = AddPane(rows, newHits, "PROXY", 1, focused == 1, prxRows, proxyH, prxOffset, inner);

        rows.Add(Card([new Span(new string('─', inner), Border)], inner));
        if (promptLines.Count > 1)
        {
            foreach (var line in promptLines.Take(promptLines.Count - 1)) rows.Add(Card([new Span(line, Dim)], inner));
        }
        else
        {
            var targets = new List<(int Start, int End, string Key)>();
            int hintRow = rows.Count;
            rows.Add(Card(HintSpans(hintCopy, targets), inner));
            foreach (var (start, end, key) in targets)
                newHits.Add(new Hit(hintRow, Col0 + start, Col0 + Math.Min(end, inner), HitKind.Command, Value: key));
        }

        int inputRow = rows.Count;
        var inputSpans = Fit(InputSpans(label, shownInput, example, prompt), leftWidth);
        if (buttons.Count > 0)
        {
            int pos = leftWidth + 1;
            inputSpans.Add(new Span(" ", Faint));
            for (int i = 0; i < buttons.Count; i++)
            {
                if (i > 0) { inputSpans.Add(new Span(" ", Faint)); pos++; }
                string chip = $" {buttons[i].Label} ";
                inputSpans.Add(new Span(chip, Ink, KeyBg));
                newHits.Add(new Hit(inputRow, Col0 + pos, Col0 + pos + chip.Length, HitKind.Button, Button: buttons[i]));
                pos += chip.Length;
            }
        }
        rows.Add(Card(inputSpans, inner));
        rows.Add(BottomBorder(cardW));
        for (int i = 0; i < MarginY; i++) rows.Add(MarginRow(w));

        lock (sync)
        {
            paneTop[0] = actTop;
            paneTop[1] = prxTop;
            hits = newHits;
        }

        var sb = new StringBuilder();
        sb.Append(Esc + "[?25l" + Esc + "[?7l");
        for (int i = 0; i < rows.Count; i++)
            sb.Append($"{Esc}[{i + 1};1H").Append(rows[i]);

        if (prompt)
        {
            int col = Col0 + 1 + label.Length + shownInput.Length;
            sb.Append($"{Esc}[{inputRow + 1};{col}H{Esc}[?25h");
        }
        return sb.ToString();
    }

    static int AddPane(List<string> rows, List<Hit> newHits, string title, int pane, bool focused,
        List<Span[]> lines, int count, int offset, int inner)
    {
        int tabRow = rows.Count;
        rows.Add(Card(TabSpans(title, focused, offset, inner, out int noteStart, out int noteLength), inner));
        newHits.Add(new Hit(tabRow, Col0, Col0 + inner, HitKind.Tab, pane));
        if (offset > 0 && noteLength > 0)
            newHits.Add(new Hit(tabRow, Col0 + noteStart, Col0 + noteStart + noteLength, HitKind.Follow, pane));

        int top = rows.Count;
        AppendPane(rows, lines, count, offset, inner);
        for (int row = top; row < top + count; row++)
        {
            newHits.Add(new Hit(row, Col0, Col0 + inner - 2, HitKind.Body, pane));
            newHits.Add(new Hit(row, Col0 + inner - 2, Col0 + inner + 2, HitKind.Bar, pane));
        }
        return top;
    }

    static List<Span[]> PaneRows(List<Entry> entries, int inner, bool proxyPane) =>
        entries.SelectMany(entry => LineRows(entry, MessageWidth(inner), proxyPane)).ToList();

    static int MessageWidth(int inner) => Math.Max(1, inner - 2 - Indent);

    static void AppendPane(List<string> rows, List<Span[]> lines, int count, int offset, int inner)
    {
        int content = inner - 2;
        int end = lines.Count - offset;
        int start = Math.Max(0, end - count);
        int pad = count - (end - start);

        bool scrollable = lines.Count > count;
        int thumbSize = scrollable ? Math.Max(1, count * count / lines.Count) : 0;
        int thumbTop = scrollable ? (int)Math.Round((double)start / (lines.Count - count) * (count - thumbSize)) : 0;

        for (int i = 0; i < count; i++)
        {
            var spans = new List<Span>();
            int used = 0;
            if (i >= pad)
                foreach (var span in lines[start + i - pad])
                {
                    spans.Add(span);
                    used += span.Text.Length;
                }

            spans.Add(new Span(new string(' ', Math.Max(0, content - used) + 1), Faint));
            spans.Add(scrollable && i >= thumbTop && i < thumbTop + thumbSize
                ? new Span("┃", Thumb)
                : new Span("│", Border));
            rows.Add(Card(spans, inner));
        }
    }

    static IEnumerable<Span[]> LineRows(Entry line, int messageWidth, bool proxyPane)
    {
        var parts = Wrap(line.Text, messageWidth);
        string color = TextColor(line.Level);

        yield return
        [
            new Span(line.Time.ToString("HH:mm:ss"), Faint),
            new Span("  ", Faint),
            GlyphSpan(line.Level),
            new Span(" ", Faint),
            new Span(parts[0], color),
        ];
        for (int i = 1; i < parts.Count; i++)
            yield return [new Span(new string(' ', Indent), Faint), new Span(parts[i], color)];
    }

    static List<string> PromptLines(string label, string value, bool prompting, int width, int height)
    {
        if (!prompting) return [label];

        var lines = Wrap(label, width);
        if (lines[^1].Length + value.Length >= width) lines.Add("");

        int maxExtra = Math.Max(0, height - 2 * MarginY - 11);
        if (lines.Count - 2 > maxExtra) lines = [.. lines.Take(maxExtra + 1), lines[^1]];
        return lines;
    }

    static List<string> Wrap(string text, int width)
    {
        width = Math.Max(1, width);
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            string rest = paragraph;
            while (rest.Length > width)
            {
                int cut = rest.LastIndexOf(' ', width);
                if (cut <= 0) cut = width;
                lines.Add(rest[..cut]);
                rest = rest[cut..].TrimStart(' ');
            }
            lines.Add(rest);
        }
        return lines;
    }

    static List<Span> Fit(IEnumerable<Span> spans, int width)
    {
        var fitted = new List<Span>();
        int used = 0;
        foreach (var span in spans)
        {
            if (used >= width) break;
            string text = used + span.Text.Length > width ? Clip(span.Text, width - used) : span.Text;
            fitted.Add(span with { Text = text });
            used += text.Length;
        }
        if (used < width) fitted.Add(new Span(new string(' ', width - used), Faint));
        return fitted;
    }

    static Span[] BrandSpans(int inner)
    {
        string clock = DateTime.Now.ToString("HH:mm:ss");
        int gap = Math.Max(1, inner - (Title.Length + 2) - clock.Length);
        return
        [
            new Span($" {Title} ", Ink, LightBg),
            new Span(new string(' ', gap), Dim),
            new Span(clock, Dim),
        ];
    }

    static List<Span> StatusSpans(List<(string Key, string Value, Level Kind)> badges)
    {
        var spans = new List<Span>();
        if (badges.Count == 0)
        {
            spans.Add(new Span("waiting for configuration", Faint));
            return spans;
        }
        foreach (var (key, value, kind) in badges)
        {
            spans.Add(new Span($" {key.ToUpperInvariant()} ", Dim, ChipBg));
            spans.Add(new Span(" ", Text));
            spans.Add(kind switch
            {
                Level.Ok => new Span($"✓ {value}", Bright),
                Level.Warn => new Span($"▲ {value}", Bright),
                Level.Error => new Span($"✕ {value}", Bright),
                Level.Detail => new Span(value, Faint),
                _ => new Span(value, Text),
            });
            spans.Add(new Span("   ", Text));
        }
        return spans;
    }

    static Span[] TabSpans(string label, bool focused, int offset, int inner, out int noteStart, out int noteLength)
    {
        string note = offset > 0
            ? focused ? "scrolled up · End to follow" : "scrolled up"
            : focused ? "scroll with the wheel or PgUp/PgDn · Tab switches" : "";
        int noteWidth = note.Length == 0 ? 0 : note.Length + 3;
        int dashes = Math.Max(0, inner - (label.Length + 2) - 1 - noteWidth);
        noteStart = label.Length + 2 + 1 + dashes;
        noteLength = note.Length == 0 ? 0 : note.Length + 2;

        var spans = new List<Span>
        {
            new($" {label} ", focused ? Ink : Dim, focused ? LightBg : ChipBg),
            new(" ", Border),
            new(new string('─', dashes), Border),
        };
        if (note.Length > 0)
        {
            spans.Add(new($" {note} ", offset > 0 ? Bright : Faint));
            spans.Add(new("─", Border));
        }
        return [.. spans];
    }

    static List<Span> HintSpans(string hintText, List<(int Start, int End, string Key)> targets)
    {
        var spans = new List<Span>();
        if (string.IsNullOrEmpty(hintText)) return spans;

        int pos = 0;
        int i = 0;
        while (i < hintText.Length)
        {
            if (hintText[i] == '[')
            {
                int close = hintText.IndexOf(']', i);
                if (close > i)
                {
                    string key = hintText[(i + 1)..close];
                    int next = hintText.IndexOf('[', close + 1);
                    if (next < 0) next = hintText.Length;
                    string chip = $" {key} ";
                    string text = hintText[(close + 1)..next];

                    spans.Add(new Span(chip, Ink, KeyBg));
                    spans.Add(new Span(text, Dim));
                    targets.Add((pos, pos + chip.Length + text.TrimEnd().Length, key));
                    pos += chip.Length + text.Length;
                    i = next;
                    continue;
                }
            }
            int after = hintText.IndexOf('[', i + 1);
            if (after < 0) after = hintText.Length;
            spans.Add(new Span(hintText[i..after], Dim));
            pos += after - i;
            i = after;
        }
        return spans;
    }

    static Span[] InputSpans(string label, string value, string example, bool prompting)
    {
        if (!prompting && label.Length == 0 && value.Length == 0)
            return [new Span("press a key", Faint)];
        if (value.Length > 0)
            return [new Span(label, Dim), new Span(value, Bright)];
        if (example.Length > 0)
            return [new Span(label, Dim), new Span(example, Faint)];
        return [new Span(label, Dim)];
    }

    static string Card(IReadOnlyList<Span> spans, int inner) =>
        Backdrop + new string(' ', MarginX)
        + Panel + Border + "│" + Text + " "
        + RenderInner(spans, inner)
        + Panel + " " + Border + "│"
        + Backdrop + new string(' ', MarginX) + Reset;

    static string RenderInner(IReadOnlyList<Span> spans, int width)
    {
        var sb = new StringBuilder();
        sb.Append(Panel);
        int used = 0;
        foreach (var span in spans)
        {
            if (used >= width || span.Text.Length == 0) continue;
            string t = span.Text;
            if (used + t.Length > width) t = Clip(t, width - used);
            sb.Append(span.Bg ?? Panel).Append(span.Fg).Append(t);
            used += t.Length;
            if (span.Bg != null) sb.Append(Panel);
        }
        if (used < width) sb.Append(Panel).Append(new string(' ', width - used));
        return sb.ToString();
    }

    static string MarginRow(int w) => Backdrop + new string(' ', w) + Reset;

    static string TopBorder(int cardW) =>
        Backdrop + new string(' ', MarginX)
        + Panel + Border + "╭" + new string('─', cardW - 2) + "╮"
        + Backdrop + new string(' ', MarginX) + Reset;

    static string BottomBorder(int cardW) =>
        Backdrop + new string(' ', MarginX)
        + Panel + Border + "╰" + new string('─', cardW - 2) + "╯"
        + Backdrop + new string(' ', MarginX) + Reset;

    static string Clip(string text, int max)
    {
        if (max <= 0) return "";
        if (text.Length <= max) return text;
        return max <= 1 ? "…" : text[..(max - 1)] + "…";
    }

    static Span GlyphSpan(Level level) => level switch
    {
        Level.Error => new Span("✕", Ink, LightBg),
        Level.Warn => new Span("▲", Ink, GreyBg),
        Level.Ok => new Span("✓", Bright),
        Level.Info => new Span("•", Dim),
        Level.Detail => new Span("·", Faint),
        Level.Proxy => new Span("»", Text),
        _ => new Span(" ", Dim),
    };

    static string TextColor(Level level) => level switch
    {
        Level.Error or Level.Warn => Bright,
        Level.Detail => Faint,
        _ => Text,
    };

    static string Fg(int r, int g, int b) => $"{Esc}[38;2;{r};{g};{b}m";
    static string Bg(int r, int g, int b) => $"{Esc}[48;2;{r};{g};{b}m";

    static int SafeWidth() { try { return Math.Max(56, System.Console.WindowWidth); } catch { return 100; } }
    static int SafeHeight() { try { return Math.Max(18, System.Console.WindowHeight); } catch { return 30; } }
    static int WindowTop() { try { return System.Console.WindowTop; } catch { return 0; } }
    static int WindowLeft() { try { return System.Console.WindowLeft; } catch { return 0; } }

    static void Write(string s)
    {
        lock (draw) { try { System.Console.Out.Write(s); System.Console.Out.Flush(); } catch { } }
    }
}
