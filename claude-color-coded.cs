// Claude Color Coded: right-click a group heading in the Claude desktop app's sidebar and choose Select color.
//
// This is a separate helper, not a change to the app. It reads heading names and positions through
// Windows UI Automation (the API screen readers use) and draws tinted strips over them in its own
// click-through window. Select color is its own window too, drawn to look like part of the app's menu.
// The only thing it saves is the color you pick for each group name.
//
// Build: build.cmd    Run: ClaudeColorCoded.exe    Quit: its tray icon    Diagnose: ClaudeColorCoded.exe --check
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

static class Native {
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    public struct POINT { public int X, Y; }
    public struct RECT { public int Left, Top, Right, Bottom; }
    public struct MouseInfo { public POINT Point; public uint Data, Flags, Time; public IntPtr Extra; }
    public struct SIZE { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct Blend { public byte Op, Flags, Alpha, Format; }

    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref POINT at, ref SIZE size, IntPtr source, ref POINT from, int key, ref Blend blend, int flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
}

class Row {
    public string Name;
    public Rectangle Bounds;
}

class Snapshot {
    public IntPtr Window = IntPtr.Zero;
    public Point WindowAt; // Where the app's window was when these positions were read.
    public Rectangle Area = Rectangle.Empty;
    public List<Row> Rows = new List<Row>();

    public string Signature() {
        return Window + "|" + Area + "|" + string.Join(";", Rows.Select(r => r.Name + r.Bounds));
    }

    public Row RowAt(Point point) {
        return Area.Contains(point) ? Rows.FirstOrDefault(r => r.Bounds.Contains(point)) : null;
    }

    // True when every heading moved the same distance, which only scrolling does. Expanding a group moves some and not others.
    public bool IsScrollOf(Snapshot before) {
        if (before == null || before.Area != Area || before.Rows.Count != Rows.Count || Rows.Count == 0) return false;
        int distance = Rows[0].Bounds.Y - before.Rows[0].Bounds.Y;
        if (distance == 0) return false;
        for (int i = 0; i < Rows.Count; i++)
            if (Rows[i].Name != before.Rows[i].Name || Rows[i].Bounds.Y - before.Rows[i].Bounds.Y != distance) return false;
        return true;
    }
}

// Follows a scroll by comparing the sidebar's pixels between frames, because UI Automation only reports
// new positions about 8 times a second. It runs only while the sidebar moves, and pixels are hashed in memory, never kept.
class ScrollTracker {
    const int QuietMs = 300, MaxShift = 300, MinMatches = 20;
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Action moved;
    readonly object gate = new object();
    Rectangle column;
    volatile int lastMotion;
    public readonly AutoResetEvent PollNow = new AutoResetEvent(false);
    public volatile bool Capturing;
    public volatile bool Active; // Stays on after capturing stops, until automation has been read again.
    public volatile int Offset;
    public volatile int Lead; // What was just measured is out of date by the time it's on screen, so strips are drawn this far ahead.
    // How long a strip takes to reach the screen after its frame was read. Measured on a 165 Hz screen at 300-1000 px/s: 0 left strips 6 px behind, 7 left 1 px.
    // ponytail: fixed for that screen. A 60 Hz screen would likely need more; work it out from the refresh rate if that one gets used.
    public static double LeadMs = 7;

    public ScrollTracker(Action moved) {
        this.moved = moved;
        new Thread(Run) { IsBackground = true }.Start();
    }

    public void Start(Rectangle list) {
        lastMotion = (int)clock.ElapsedMilliseconds;
        lock (gate) {
            if (Capturing) return;
            column = new Rectangle(list.X + 12, list.Y + 4, Math.Min(150, list.Width - 24), list.Height - 8);
            Capturing = true;
            Active = true;
        }
        wake.Set();
        PollNow.Set();
    }

    // Called once a read that began after the scroll stopped is in, so automation's positions can be trusted again.
    public void Finish() {
        lock (gate) { if (!Capturing) Active = false; }
    }

    void Run() {
        while (true) {
            wake.WaitOne();
            // ponytail: the screen can't be read on the lock screen. Automation's positions take over once this stops.
            try { Track(); } catch (Exception) { }
            Capturing = false;
        }
    }

    void Track() {
        int width = column.Width, height = column.Height;
        Lead = 0;
        var pixels = new int[width * height];
        int[] reference = new int[height], current = new int[height];
        var hasText = new bool[height];
        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb))
        using (Graphics graphics = Graphics.FromImage(bitmap)) {
            ReadLines(bitmap, graphics, pixels, reference, hasText);
            double lastMatch = clock.Elapsed.TotalMilliseconds;
            while (clock.ElapsedMilliseconds - lastMotion < QuietMs) {
                ReadLines(bitmap, graphics, pixels, current, hasText);
                int shift;
                // If a frame matches nothing, the last good frame stays the reference, so that frame's motion is still counted by the next match.
                if (FindShift(reference, current, hasText, out shift)) {
                    int[] swap = reference; reference = current; current = swap;
                    double now = clock.Elapsed.TotalMilliseconds;
                    if (shift != 0 || Lead != 0) { // A still frame after motion takes the lead back off, so strips land exactly.
                        Offset += shift;
                        Lead = (int)Math.Round(shift * LeadMs / Math.Max(now - lastMatch, 1)); // Speed, not the raw shift: one shift can span several frames.
                        if (shift != 0) lastMotion = (int)clock.ElapsedMilliseconds;
                        moved();
                    }
                    lastMatch = now;
                }
                // No sleep here: Thread.Sleep(1) really takes 15.5 ms on Windows, which capped this at about 45 updates a second.
                // ponytail: uses more CPU while the sidebar moves, and only then.
            }
        }
    }

    void ReadLines(Bitmap bitmap, Graphics graphics, int[] pixels, int[] hashes, bool[] hasText) {
        // CopyFromScreen leaves out layered windows by default, so the overlay's own strips aren't in what's compared.
        graphics.CopyFromScreen(column.Location, Point.Empty, column.Size);
        BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, column.Size), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        bitmap.UnlockBits(data);
        for (int y = 0; y < hashes.Length; y++) {
            int start = y * column.Width, first = pixels[start], hash = 17;
            bool text = false;
            for (int i = start; i < start + column.Width; i++) {
                hash = hash * 31 + pixels[i];
                if (pixels[i] != first) text = true;
            }
            hashes[y] = hash;
            hasText[y] = text;
        }
    }

    // Finds how far the lines moved since the reference frame, positive being down. Blank lines match anywhere, so only lines with text count.
    public static bool FindShift(int[] reference, int[] current, bool[] hasText, out int shift) {
        int height = current.Length, bestHits = -1, bestSeen = 0;
        shift = 0;
        for (int s = -MaxShift; s <= MaxShift; s++) {
            int hits = 0, seen = 0;
            for (int y = Math.Max(0, s); y < Math.Min(height, height + s); y++) {
                if (!hasText[y]) continue;
                seen++;
                if (current[y] == reference[y - s]) hits++;
            }
            if (hits > bestHits || (hits == bestHits && Math.Abs(s) < Math.Abs(shift))) { bestHits = hits; bestSeen = seen; shift = s; }
        }
        return bestHits >= MinMatches && bestHits * 2 >= bestSeen;
    }
}

static class Sidebar {
    // ponytail: leans on the app's English button label and a class name, so an app update can break it. --check shows when it has.
    const string Prefix = "New session in ";
    static readonly Condition IsButton = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
    static readonly Condition IsMenu = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu);

    public static IntPtr FindWindow() {
        foreach (Process process in Process.GetProcessesByName("claude")) {
            using (process) {
                IntPtr window = process.MainWindowHandle;
                // The Claude Code CLI is also claude.exe, so check it's the desktop app.
                if (window != IntPtr.Zero && process.MainModule.FileName.IndexOf(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) >= 0)
                    return window;
            }
        }
        return IntPtr.Zero;
    }

    static AutomationElementCollection Buttons(AutomationElement scope) {
        var request = new CacheRequest();
        request.Add(AutomationElement.NameProperty);
        using (request.Activate()) return scope.FindAll(TreeScope.Descendants, IsButton);
    }

    // The window's tree holds chat text too. Names here are only compared, and only headings are kept.
    public static AutomationElement FindList(IntPtr window) {
        foreach (AutomationElement button in Buttons(AutomationElement.FromHandle(window))) {
            if (!button.Cached.Name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            AutomationElement element = TreeWalker.RawViewWalker.GetParent(button);
            while (element != null) {
                if (element.Current.ClassName.StartsWith("dframe-nav-scroll", StringComparison.Ordinal) || element.Current.Name == "Sidebar")
                    return element;
                element = TreeWalker.RawViewWalker.GetParent(element);
            }
        }
        return null;
    }

    public static Snapshot Read(IntPtr window, AutomationElement list) {
        var snapshot = new Snapshot { Window = window, Area = ToRectangle(list.Current.BoundingRectangle) };
        foreach (AutomationElement button in Buttons(list)) {
            string name = button.Cached.Name;
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            // The button's parent is the full heading row. Rows scrolled out of view are kept: none on screen doesn't mean none exist.
            Rectangle row = ToRectangle(TreeWalker.RawViewWalker.GetParent(button).Current.BoundingRectangle);
            if (row.Width > 0 && row.Height > 0)
                snapshot.Rows.Add(new Row { Name = name.Substring(Prefix.Length), Bounds = row });
        }
        return snapshot;
    }

    // Where the app's own open menu is, or empty if none is open.
    public static Rectangle FindMenu(IntPtr window) {
        AutomationElement menu = AutomationElement.FromHandle(window).FindFirst(TreeScope.Descendants, IsMenu);
        return menu == null ? Rectangle.Empty : ToRectangle(menu.Current.BoundingRectangle);
    }

    static Rectangle ToRectangle(System.Windows.Rect rect) {
        return rect.IsEmpty ? Rectangle.Empty : new Rectangle((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);
    }
}

// A heading's highlight: one color, or two for a left-to-right gradient. A single color is held as both.
struct Tint {
    public readonly Color From, To;
    public Tint(Color from, Color to) { From = from; To = to; }
    public Tint(Color color) : this(color, color) { }
    public bool IsGradient { get { return From.ToArgb() != To.ToArgb(); } }

    public Brush BrushFor(Rectangle box) {
        if (!IsGradient) return new SolidBrush(From);
        box.Inflate(1, 0); // Otherwise GDI+ wraps the last color round onto the first column.
        return new LinearGradientBrush(box, From, To, LinearGradientMode.Horizontal);
    }
}

static class ColorFile {
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeColorCoded", "colors.txt");
    static readonly Regex Line = new Regex("^(.+)=#([0-9a-fA-F]{6})(?:-#([0-9a-fA-F]{6}))?$");

    static Color FromHex(string hex) { return Color.FromArgb(Convert.ToInt32("FF" + hex, 16)); }
    static string ToHex(Color color) { return string.Format("#{0:x2}{1:x2}{2:x2}", color.R, color.G, color.B); }

    public static Dictionary<string, Tint> Parse(IEnumerable<string> lines) {
        var colors = new Dictionary<string, Tint>();
        foreach (string line in lines) {
            Match match = Line.Match(line);
            if (!match.Success) continue;
            Color from = FromHex(match.Groups[2].Value);
            colors[match.Groups[1].Value] = new Tint(from, match.Groups[3].Success ? FromHex(match.Groups[3].Value) : from);
        }
        return colors;
    }

    public static IEnumerable<string> Format(Dictionary<string, Tint> colors) {
        return colors.Select(c => c.Key + "=" + ToHex(c.Value.From) + (c.Value.IsGradient ? "-" + ToHex(c.Value.To) : ""));
    }

    public static Dictionary<string, Tint> Load() {
        return Parse(File.Exists(FilePath) ? File.ReadAllLines(FilePath, Encoding.UTF8) : new string[0]);
    }

    public static void Save(Dictionary<string, Tint> colors) {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllLines(FilePath, Format(colors), Encoding.UTF8);
    }
}

// The tinted strips. Click-through, and owned by the Claude window so it stays with it in the window stack.
// It covers the sidebar's screen and stays put, and the strips are redrawn to follow the app's window. Measured with real drags, with no helper running: moving any other window
// during a drag left the app's window 260 ms behind the cursor, whatever the flags, against 5 ms with nothing else happening. A full-screen window redrawing also left it at 5 ms.
class Overlay : Form {
    public const double Strength = 0.3;
    readonly Dictionary<string, Tint> colors;
    public List<Row> Rows = new List<Row>();
    public Rectangle Column; // The sidebar, where the rows' positions were measured.
    public Point Shift; // How far the app's window has moved since then.
    public int ScrollOffset;
    public Rectangle Hole; // The app's own menu while it's open. This window sits above the whole app, so strips must not be drawn across it.

    public Overlay(Dictionary<string, Tint> colors) {
        this.colors = colors;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = TransparencyKey = Color.FromArgb(1, 2, 3); // Pixels of this exact color turn see-through, so it can't be one anybody would pick.
        Opacity = Strength;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams {
        get {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= 0x20 | 0x80 | 0x08000000; // WS_EX_TRANSPARENT, WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE
            return cp;
        }
    }

    Rectangle ColumnNow {
        get {
            Rectangle box = Column;
            box.Offset(Shift.X - Left, Shift.Y - Top);
            return box;
        }
    }

    // Only ever the sidebar's part of this window, which would be slow to redraw whole.
    public void RedrawColumn() { Invalidate(ColumnNow); }

    public void MoveColumn(Point shift) {
        RedrawColumn();
        Shift = shift;
        FitToScreen();
        RedrawColumn();
        Update();
    }

    // The screen the sidebar is on, leaving out its taskbar, and a pixel short for a hidden taskbar. Measured: a window covering a whole screen counts as a fullscreen app, and the taskbar
    // loses its always-on-top. Only moves when the sidebar crosses to another screen.
    // ponytail: while the sidebar straddles two screens, the part on the other one has no strips. Fixes itself once most of it is across.
    public void FitToScreen() {
        Rectangle column = Column;
        column.Offset(Shift);
        Rectangle area = Screen.FromRectangle(column).WorkingArea;
        Bounds = new Rectangle(area.X, area.Y, area.Width, area.Height - 1);
    }

    protected override void OnPaint(PaintEventArgs e) {
        // Smoothing stays off: blended edge pixels wouldn't match the transparency color and would show as fringes.
        e.Graphics.SmoothingMode = SmoothingMode.None;
        e.Graphics.IntersectClip(ColumnNow);
        if (!Hole.IsEmpty) e.Graphics.ExcludeClip(new Rectangle(Hole.X - Left, Hole.Y - Top, Hole.Width, Hole.Height));
        foreach (Row row in Rows) {
            Tint tint;
            if (!colors.TryGetValue(row.Name, out tint)) continue;
            Rectangle box = row.Bounds;
            box.Offset(Shift.X - Left, Shift.Y - Top + ScrollOffset);
            using (GraphicsPath path = Rounded(box, 16))
            using (Brush brush = tint.BrushFor(box))
                e.Graphics.FillPath(brush, path);
        }
    }

    public static GraphicsPath Rounded(Rectangle box, int diameter) {
        int d = Math.Min(diameter, Math.Min(box.Width, box.Height));
        var path = new GraphicsPath();
        path.AddArc(box.X, box.Y, d, d, 180, 90);
        path.AddArc(box.Right - d, box.Y, d, d, 270, 90);
        path.AddArc(box.Right - d, box.Bottom - d, d, d, 0, 90);
        path.AddArc(box.X, box.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}

// A window drawn to look like part of the app's own menus. Sizes come from the app's stylesheet; colors are read from the real menu on screen.
// It never takes keyboard focus, so the app's window stays active behind it and the app's menu stays open.
abstract class MenuWindow : Form {
    protected const int Gutter = 6, ItemHeight = 28, Corner = 14, ItemCorner = 8, ItemPad = 10, Baseline = 19, SeparatorGap = 9;
    // ponytail: the app's font (Anthropic Sans) isn't installed on Windows, so this is the nearest system font. Its text won't be a pixel match.
    static readonly Font LabelFont = new Font("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
    protected static Color Fill = Color.FromArgb(0x30, 0x30, 0x30), Edge = Color.FromArgb(0x38, 0x38, 0x38), Ink = Color.FromArgb(0xF0, 0xF0, 0xF0), Highlight = Color.FromArgb(0x3F, 0x3F, 0x3F);

    protected MenuWindow() {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams {
        get {
            CreateParams cp = base.CreateParams;
            // WS_EX_TOOLWINDOW, WS_EX_NOACTIVATE, WS_EX_LAYERED, WS_EX_TOPMOST. Topmost is set here because the form's TopMost property takes focus from the app when shown, and the app's menu then ignores keys.
            cp.ExStyle |= 0x80 | 0x08000000 | 0x00080000 | 0x8;
            return cp;
        }
    }

    protected override void WndProc(ref Message m) {
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    public static Color Mix(Color from, Color to, double amount) {
        Func<int, int, int> channel = (a, b) => (int)Math.Round(a + (b - a) * amount);
        return Color.FromArgb(channel(from.R, to.R), channel(from.G, to.G), channel(from.B, to.B));
    }

    // Takes its colors from the real menu, so light mode and app restyles match too, and returns the menu's true
    // rectangle: automation sometimes rounds its width up a pixel, which would show as a step in the edge.
    // Reads halfway down the left side: the row covers the menu's top, and the strips have a hole there.
    public static Rectangle ReadLook(Rectangle menu) {
        using (var shot = new Bitmap(menu.Width + 4, menu.Height, PixelFormat.Format32bppRgb))
        using (Graphics g = Graphics.FromImage(shot)) {
            g.CopyFromScreen(menu.Location, Point.Empty, shot.Size);
            int middle = menu.Height / 2;
            Edge = shot.GetPixel(0, middle);
            Fill = shot.GetPixel(3, middle);
            bool dark = Fill.GetBrightness() < 0.5f;
            Ink = Mix(Fill, dark ? Color.White : Color.Black, dark ? 0.93 : 0.9); // The stylesheet's text-primary.
            Highlight = Mix(Fill, dark ? Color.White : Color.Black, dark ? 0.075 : 0.05); // The stylesheet's fill-ghost-hover.
            for (int x = shot.Width - 1; x > menu.Width / 2; x--)
                if (shot.GetPixel(x, middle).ToArgb() == Edge.ToArgb()) return new Rectangle(menu.X, menu.Y, x + 1, menu.Height);
        }
        return menu;
    }

    protected void MoveOnScreen(Point at) {
        Rectangle area = Screen.FromPoint(at).WorkingArea;
        Location = new Point(Math.Min(at.X, area.Right - Width), Math.Min(at.Y, area.Bottom - Height));
    }

    protected abstract void Draw(Graphics g);

    // Per-pixel alpha gives smooth corners like the real menu's. Windows ignores normal painting for these windows, so this replaces OnPaint.
    protected void Render() {
        using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bitmap)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half; // Whole-number rectangles land exactly on pixels.
            g.TextRenderingHint = TextRenderingHint.AntiAlias; // Gray edges, like the app's text.
            Draw(g);
            IntPtr screen = Native.GetDC(IntPtr.Zero), memory = Native.CreateCompatibleDC(screen), image = bitmap.GetHbitmap(Color.FromArgb(0)), old = Native.SelectObject(memory, image);
            var at = new Native.POINT { X = Left, Y = Top };
            var size = new Native.SIZE { Width = Width, Height = Height };
            var from = new Native.POINT();
            var blend = new Native.Blend { Alpha = 255, Format = 1 }; // AC_SRC_ALPHA
            Native.UpdateLayeredWindow(Handle, screen, ref at, ref size, memory, ref from, 0, ref blend, 2); // ULW_ALPHA
            Native.SelectObject(memory, old);
            Native.DeleteObject(image);
            Native.DeleteDC(memory);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    protected void DrawFrame(Graphics g, int height) {
        using (GraphicsPath outer = Overlay.Rounded(new Rectangle(0, 0, Width, height), Corner * 2))
        using (var brush = new SolidBrush(Edge)) g.FillPath(brush, outer);
        using (GraphicsPath inner = Overlay.Rounded(new Rectangle(1, 1, Width - 2, height - 2), (Corner - 1) * 2))
        using (var brush = new SolidBrush(Fill)) g.FillPath(brush, inner);
    }

    protected void DrawHighlight(Graphics g, Rectangle item) {
        using (GraphicsPath path = Overlay.Rounded(item, ItemCorner * 2))
        using (var brush = new SolidBrush(Highlight)) g.FillPath(brush, path);
    }

    protected void DrawLabel(Graphics g, string text, int x, int itemTop) {
        FontFamily family = LabelFont.FontFamily;
        float ascent = LabelFont.Size * family.GetCellAscent(FontStyle.Regular) / family.GetEmHeight(FontStyle.Regular);
        using (var brush = new SolidBrush(Ink)) g.DrawString(text, LabelFont, brush, x, itemTop + Baseline - ascent, StringFormat.GenericTypographic);
    }

    protected void DrawSeparator(Graphics g, int y) {
        using (var brush = new SolidBrush(Edge)) g.FillRectangle(brush, Gutter + ItemPad, y, Width - (Gutter + ItemPad) * 2 + 1, 1);
    }
}

// The Select color row. The app's menu can't be changed from outside, so this is a separate window that takes the place of the menu's top edge.
// It sits above the menu's first item rather than at the bottom next to the red Delete item, so a slip can't land on that.
// ponytail: opens on click rather than hover, the app's arrow keys can't reach it, and sizes assume 100% display scaling. Scale them if a scaled screen gets used.
class ColorPanel : MenuWindow {
    const int FirstAppItem = Gutter + ItemHeight + SeparatorGap, Above = FirstAppItem - Gutter;
    bool hover, expanded;
    public Action Clicked;

    // Runs down past the menu's top to cover its rounded corners and continue its straight edges.
    public static Rectangle BoundsFor(Rectangle menu) {
        return new Rectangle(menu.X, menu.Y - Above, menu.Width, Above + Corner);
    }

    // Where this window is solid and takes clicks, on screen. Below it, the app's own items show through a hole.
    public Rectangle RowArea { get { return new Rectangle(Left, Top, Width, FirstAppItem); } }

    Rectangle Item { get { return new Rectangle(Gutter, Gutter, Width - Gutter * 2 + 1, ItemHeight); } }

    public void Place(Rectangle menu) {
        Bounds = BoundsFor(menu);
        hover = expanded = false;
        Render();
        Show();
    }

    public bool Expanded {
        set { expanded = value; if (Visible) Render(); }
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        bool over = Item.Contains(e.Location);
        if (over != hover) { hover = over; Render(); }
    }

    protected override void OnMouseLeave(EventArgs e) {
        hover = false;
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left && Item.Contains(e.Location)) Clicked();
    }

    protected override void Draw(Graphics g) {
        DrawFrame(g, Height + Corner * 2); // Runs off the bottom of the window, so only the rounded top shows.
        Rectangle item = Item;
        if (hover || expanded) DrawHighlight(g, item);
        DrawLabel(g, "Select color", item.X + ItemPad, item.Y);
        int mid = item.Y + item.Height / 2, x = item.Right - 15;
        using (var pen = new Pen(Ink, 1.5f)) g.DrawLines(pen, new[] { new PointF(x, mid - 4), new PointF(x + 4, mid), new PointF(x, mid + 4) });
        DrawSeparator(g, item.Bottom + 4);

        // The hole over the app's first item, so it shows through and stays clickable. Fully clear pixels let the mouse through.
        g.CompositingMode = CompositingMode.SourceCopy;
        g.SmoothingMode = SmoothingMode.None;
        using (var clear = new SolidBrush(Color.Transparent)) g.FillRectangle(clear, item.X, FirstAppItem, item.Width, Height - FirstAppItem);
    }
}

// The list of colors that opens from the Select color row.
class ColorMenu : MenuWindow {
    static readonly KeyValuePair<string, Color>[] Choices = {
        Choice("Red", 0xEF4444), Choice("Orange", 0xF97316), Choice("Yellow", 0xEAB308), Choice("Green", 0x22C55E),
        Choice("Teal", 0x14B8A6), Choice("Blue", 0x3B82F6), Choice("Purple", 0xA855F7), Choice("Pink", 0xEC4899),
    };

    static readonly int CustomItem = Choices.Length, GradientItem = Choices.Length + 1, NoColorItem = Choices.Length + 2;

    Tint? current;
    int hover = -1;
    Action<Tint?> pick;
    Action<bool> openPicker;

    static KeyValuePair<string, Color> Choice(string name, int rgb) {
        return new KeyValuePair<string, Color>(name, Color.FromArgb(unchecked((int)0xFF000000) | rgb));
    }

    public ColorMenu() {
        Width = 176;
        Height = ItemBox(NoColorItem).Bottom + Gutter;
    }

    public void Open(Tint? current, Point at, Action<Tint?> pick, Action<bool> openPicker) {
        this.current = current;
        this.pick = pick;
        this.openPicker = openPicker;
        hover = -1;
        MoveOnScreen(at);
        Render();
        Show();
    }

    Rectangle ItemBox(int index) {
        return new Rectangle(Gutter, Gutter + ItemHeight * index + (index >= CustomItem ? SeparatorGap : 0), Width - Gutter * 2, ItemHeight);
    }

    int ItemAt(Point point) {
        for (int i = 0; i <= NoColorItem; i++) if (ItemBox(i).Contains(point)) return i;
        return -1;
    }

    bool IsCurrent(int item) {
        if (!current.HasValue) return false;
        Tint tint = current.Value;
        bool preset = !tint.IsGradient && Choices.Any(c => c.Value.ToArgb() == tint.From.ToArgb());
        if (item == GradientItem) return tint.IsGradient;
        if (item == CustomItem) return !tint.IsGradient && !preset;
        return item < CustomItem && preset && Choices[item].Value.ToArgb() == tint.From.ToArgb();
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        int item = ItemAt(e.Location);
        if (item != hover) { hover = item; Render(); }
    }

    protected override void OnMouseLeave(EventArgs e) {
        hover = -1;
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        int item = ItemAt(e.Location);
        if (e.Button != MouseButtons.Left || item < 0) return;
        if (item == CustomItem || item == GradientItem) openPicker(item == GradientItem);
        else pick(item == NoColorItem ? (Tint?)null : new Tint(Choices[item].Value));
    }

    protected override void Draw(Graphics g) {
        DrawFrame(g, Height);
        for (int i = 0; i <= NoColorItem; i++) {
            Rectangle box = ItemBox(i), dot = new Rectangle(box.X + ItemPad, box.Y + 7, 14, 14);
            if (i == hover) DrawHighlight(g, box);
            if (i != NoColorItem) {
                Tint gradient = current.HasValue && current.Value.IsGradient ? current.Value : ColorPicker.StartingGradient;
                using (Brush brush = i < CustomItem ? new SolidBrush(Choices[i].Value) : i == CustomItem ? ColorPicker.HueBrush(dot) : gradient.BrushFor(dot))
                    g.FillEllipse(brush, dot);
            }
            DrawLabel(g, i < CustomItem ? Choices[i].Key : i == CustomItem ? "Custom color…" : i == GradientItem ? "Gradient…" : "No color", box.X + ItemPad + 22, box.Y);
            if (IsCurrent(i)) {
                int mid = box.Y + box.Height / 2, x = box.Right - 22;
                using (var pen = new Pen(Ink, 1.5f)) g.DrawLines(pen, new[] { new PointF(x, mid), new PointF(x + 4, mid + 4), new PointF(x + 12, mid - 5) });
            }
        }
        DrawSeparator(g, ItemBox(CustomItem).Y - 5);
    }
}

// Picks any color, or the two colors of a gradient. Mouse only, like the other menu windows: they never take keyboard focus, or the app's menu would stop responding.
// ponytail: that rules out a box for typing a hex code. Add one if exact colors are wanted, in a window that closes the app's menu first.
class ColorPicker : MenuWindow {
    public static readonly Tint StartingGradient = new Tint(Color.FromArgb(0x3B, 0x82, 0xF6), Color.FromArgb(0xA8, 0x55, 0xF7));
    const int Pad = 12, Inner = 188, Gap = 10, PreviewHeight = 24, SquareHeight = 120, HueHeight = 14;
    readonly double[] hue = new double[2], saturation = new double[2], brightness = new double[2];
    bool gradient, overApply;
    int slot, dragging; // Which of the two colors is being edited, and what's being dragged: 0 nothing, 1 the square, 2 the hue bar.
    Action<Tint> pick;

    public ColorPicker() { Width = Pad * 2 + Inner; }

    Rectangle ChipBox(int index) { return new Rectangle(Pad + index * (Inner + Gap) / 2, Pad, (Inner - Gap) / 2, ItemHeight); }
    Rectangle PreviewBox { get { return new Rectangle(Pad, gradient ? Pad + ItemHeight + Gap : Pad, Inner, PreviewHeight); } }
    Rectangle SquareBox { get { return new Rectangle(Pad, PreviewBox.Bottom + Gap, Inner, SquareHeight); } }
    Rectangle HueBox { get { return new Rectangle(Pad, SquareBox.Bottom + Gap, Inner, HueHeight); } }
    Rectangle ApplyBox { get { return new Rectangle(Gutter, HueBox.Bottom + SeparatorGap + Gap / 2, Width - Gutter * 2, ItemHeight); } }

    Color Slot(int index) { return FromHsv(hue[index], saturation[index], brightness[index]); }
    Tint Result { get { return new Tint(Slot(0), Slot(gradient ? 1 : 0)); } }

    public static Color FromHsv(double h, double s, double v) {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        double[] rgb = h < 60 ? new[] { c, x, 0 } : h < 120 ? new[] { x, c, 0 } : h < 180 ? new[] { 0, c, x } : h < 240 ? new[] { 0, x, c } : h < 300 ? new[] { x, 0, c } : new[] { c, 0, x };
        return Color.FromArgb((int)Math.Round((rgb[0] + m) * 255), (int)Math.Round((rgb[1] + m) * 255), (int)Math.Round((rgb[2] + m) * 255));
    }

    public static void ToHsv(Color color, out double h, out double s, out double v) {
        int max = Math.Max(color.R, Math.Max(color.G, color.B)), min = Math.Min(color.R, Math.Min(color.G, color.B));
        h = color.GetHue();
        s = max == 0 ? 0 : (max - min) / (double)max;
        v = max / 255.0;
    }

    public static Brush HueBrush(Rectangle box) {
        box.Inflate(1, 0); // Otherwise GDI+ wraps the last color round onto the first column.
        var brush = new LinearGradientBrush(box, Color.Red, Color.Red, LinearGradientMode.Horizontal);
        brush.InterpolationColors = new ColorBlend { Colors = Enumerable.Range(0, 7).Select(i => FromHsv(i * 60 % 360, 1, 1)).ToArray(), Positions = Enumerable.Range(0, 7).Select(i => i / 6f).ToArray() };
        return brush;
    }

    public void Open(Tint? current, bool gradient, Point at, Action<Tint> pick) {
        this.gradient = gradient;
        this.pick = pick;
        slot = dragging = 0;
        overApply = false;
        Tint start = current ?? StartingGradient;
        ToHsv(start.From, out hue[0], out saturation[0], out brightness[0]);
        ToHsv(start.IsGradient ? start.To : StartingGradient.To, out hue[1], out saturation[1], out brightness[1]);
        Height = ApplyBox.Bottom + Gutter;
        MoveOnScreen(at);
        Render();
        Show();
    }

    static double Clamp(double value) { return Math.Max(0, Math.Min(1, value)); }

    void Track(Point point) {
        Rectangle box = dragging == 1 ? SquareBox : HueBox;
        double across = Clamp((point.X - box.X) / (double)(box.Width - 1));
        if (dragging == 2) hue[slot] = 360 * across;
        else {
            saturation[slot] = across;
            brightness[slot] = 1 - Clamp((point.Y - box.Y) / (double)(box.Height - 1));
        }
        Render();
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button != MouseButtons.Left) return;
        if (gradient) for (int i = 0; i < 2; i++) if (ChipBox(i).Contains(e.Location)) { slot = i; Render(); return; }
        dragging = SquareBox.Contains(e.Location) ? 1 : HueBox.Contains(e.Location) ? 2 : 0;
        if (dragging != 0) Track(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        if (dragging != 0) { Track(e.Location); return; }
        bool over = ApplyBox.Contains(e.Location);
        if (over != overApply) { overApply = over; Render(); }
    }

    protected override void OnMouseLeave(EventArgs e) {
        if (!overApply) return;
        overApply = false;
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        if (dragging != 0) { dragging = 0; return; }
        if (e.Button == MouseButtons.Left && ApplyBox.Contains(e.Location)) pick(Result);
    }

    static void DrawHandle(Graphics g, double x, double y) {
        var ring = new RectangleF((float)x - 5, (float)y - 5, 10, 10);
        using (var dark = new Pen(Color.FromArgb(150, 0, 0, 0), 3.5f)) g.DrawEllipse(dark, ring);
        using (var light = new Pen(Color.White, 2f)) g.DrawEllipse(light, ring);
    }

    protected override void Draw(Graphics g) {
        DrawFrame(g, Height);
        if (gradient) for (int i = 0; i < 2; i++) {
            Rectangle chip = ChipBox(i);
            if (i == slot) DrawHighlight(g, chip);
            using (var dot = new SolidBrush(Slot(i))) g.FillEllipse(dot, chip.X + 8, chip.Y + 7, 14, 14);
            DrawLabel(g, i == 0 ? "Left" : "Right", chip.X + 28, chip.Y);
        }

        // The preview is as see-through as the strips, so it shows roughly how the highlight will look.
        int alpha = (int)(255 * Overlay.Strength);
        Tint result = Result;
        using (GraphicsPath path = Overlay.Rounded(PreviewBox, 16))
        using (Brush brush = new Tint(Color.FromArgb(alpha, result.From), Color.FromArgb(alpha, result.To)).BrushFor(PreviewBox))
            g.FillPath(brush, path);

        Rectangle square = SquareBox, oversize = square;
        oversize.Inflate(1, 1); // Otherwise GDI+ wraps the last color round onto the first line.
        using (GraphicsPath path = Overlay.Rounded(square, 16)) {
            using (var pale = new LinearGradientBrush(oversize, Color.White, FromHsv(hue[slot], 1, 1), LinearGradientMode.Horizontal)) g.FillPath(pale, path);
            using (var dark = new LinearGradientBrush(oversize, Color.FromArgb(0, 0, 0, 0), Color.Black, LinearGradientMode.Vertical)) g.FillPath(dark, path);
        }
        Rectangle bar = HueBox;
        using (GraphicsPath path = Overlay.Rounded(bar, HueHeight))
        using (Brush brush = HueBrush(bar))
            g.FillPath(brush, path);
        DrawHandle(g, square.X + saturation[slot] * (square.Width - 1), square.Y + (1 - brightness[slot]) * (square.Height - 1));
        DrawHandle(g, bar.X + hue[slot] / 360 * (bar.Width - 1), bar.Y + bar.Height / 2.0);

        Rectangle apply = ApplyBox;
        if (overApply) DrawHighlight(g, apply);
        DrawLabel(g, "Apply", apply.X + ItemPad, apply.Y);
        DrawSeparator(g, apply.Y - 5);
    }
}

class App : ApplicationContext {
    const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_MOUSEWHEEL = 0x20A;
    const int GWLP_HWNDPARENT = -8;
    const uint GA_ROOT = 2;
    const int SettleMs = 250; // Automation's positions lag by up to 160 ms, so they can't be trusted until this long after the window stops.

    readonly Dictionary<string, Tint> colors = ColorFile.Load();
    readonly ColorMenu menu = new ColorMenu();
    readonly ColorPanel panel = new ColorPanel();
    readonly ColorPicker picker = new ColorPicker();
    readonly AutoResetEvent menuWake = new AutoResetEvent(false);
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 40 };
    readonly Native.HookProc hookProc; // Kept in a field so the garbage collector can't free it while Windows still calls it.
    readonly IntPtr hook;
    readonly AutoResetEvent pressed = new AutoResetEvent(false);
    volatile int lastMove = Environment.TickCount - SettleMs;
    volatile bool stripsQueued;
    readonly ScrollTracker tracker;
    volatile Snapshot latest = new Snapshot();
    Snapshot shown;
    Overlay overlay;
    IntPtr owner = IntPtr.Zero;
    Row menuRow; // The heading that was right-clicked, and the window that took the click.
    IntPtr menuTarget;
    Rectangle menuArea; // The app's own menu, while the Select color row is attached to it.
    volatile bool closeAppMenu, menuWatching;

    public App() {
        GC.KeepAlive(menu.Handle); // The mouse hook and the tracker post to the menu, so its window must exist first.
        tracker = new ScrollTracker(() => menu.BeginInvoke((Action)ShowOffset));

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Quit Claude Color Coded", null, (s, e) => ExitThread());
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "Claude Color Coded", ContextMenuStrip = trayMenu, Visible = true };

        hookProc = OnMouse;
        hook = Native.SetWindowsHookEx(WH_MOUSE_LL, hookProc, Native.GetModuleHandle(null), 0);

        timer.Tick += (s, e) => { Apply(); KeepAboveOwner(); };
        timer.Start();

        // UI Automation runs off this thread: the mouse hook lives here, and a slow read must never lag the mouse.
        new Thread(PollLoop) { IsBackground = true }.Start();
        new Thread(FollowDrag) { IsBackground = true }.Start();

        panel.Clicked = ToggleColorList;
        new Thread(WatchAppMenu) { IsBackground = true }.Start();
    }

    protected override void ExitThreadCore() {
        timer.Stop();
        Native.UnhookWindowsHookEx(hook);
        tray.Visible = false;
        base.ExitThreadCore();
    }

    void PollLoop() {
        IntPtr window = IntPtr.Zero;
        AutomationElement list = null;
        string last = "";
        int failures = 0;
        while (true) {
            // Measured: while the app's menu is open the app hides its page from automation, so a read finds no headings and would wipe the strips. Keep what's drawn.
            // While the window moves, automation's positions arrive late and row by row, and FollowDrag covers that time.
            if (menuWatching || Moving) { Thread.Sleep(50); continue; }
            int moveBefore = lastMove;
            var snapshot = new Snapshot();
            bool fast = false;
            bool scrollEnded = tracker.Active && !tracker.Capturing; // Checked before reading, so this read is newer than the scroll.
            try {
                if (!Native.IsWindow(window)) { window = Sidebar.FindWindow(); list = null; }
                snapshot.Window = window;
                if (window != IntPtr.Zero && !Native.IsIconic(window)) {
                    if (list == null) list = Sidebar.FindList(window);
                    if (list != null) {
                        Native.RECT at;
                        Native.GetWindowRect(window, out at);
                        snapshot = Sidebar.Read(window, list);
                        snapshot.WindowAt = new Point(at.Left, at.Top);
                        if (snapshot.Rows.Count == 0) list = null;
                    }
                    fast = list != null && Native.GetForegroundWindow() == window;
                }
                failures = 0;
            } catch (Exception) {
                // Measured: while the app is busy, reads fail every second or two and the next one works.
                // So keep what's drawn and retry. Only a long run of failures means the page really changed.
                if (++failures < 20) { Thread.Sleep(20); continue; }
                failures = 0;
                list = null;
                snapshot = new Snapshot { Window = window };
            }
            if (lastMove != moveBefore) continue; // The window moved during this read, so its positions are a mix.
            string signature = snapshot.Signature();
            if (signature != last) { last = signature; latest = snapshot; }
            if (scrollEnded) tracker.Finish();
            tracker.PollNow.WaitOne(fast || tracker.Active ? 80 : 1000);
        }
    }

    // Runs only from a right-click on a heading until the app's menu closes. The app builds the menu a moment after the click, so give it a second to appear.
    void WatchAppMenu() {
        const int AppearMs = 1000, FadeInMs = 200;
        const uint WM_KEYDOWN = 0x100, WM_KEYUP = 0x101;
        IntPtr VK_ESCAPE = new IntPtr(0x1B), KeyDownInfo = new IntPtr(0x00010001), KeyUpInfo = new IntPtr(unchecked((int)0xC0010001));
        while (true) {
            menuWake.WaitOne();
            menuWatching = true;
            Rectangle placed = Rectangle.Empty;
            int deadline = Environment.TickCount + AppearMs, lookAt = 0;
            while (true) {
                if (menuWake.WaitOne(0)) { placed = Rectangle.Empty; deadline = Environment.TickCount + AppearMs; } // Right-clicked again.
                Rectangle found;
                try { found = Sidebar.FindMenu(owner); }
                catch (Exception) {
                    if (Native.IsWindow(owner)) { Thread.Sleep(20); continue; } // Reads fail now and then while the app is busy, and the next one works.
                    found = Rectangle.Empty; // The app closed.
                }
                if (found.IsEmpty) {
                    if (!placed.IsEmpty) { placed = Rectangle.Empty; menu.BeginInvoke((Action)HidePanel); }
                    if (Environment.TickCount - deadline > 0) break;
                    continue;
                }
                if (closeAppMenu) {
                    // Only ever sent while the menu is confirmed open: with no menu open, Escape would go to the app itself and could stop a running session.
                    closeAppMenu = false;
                    Native.PostMessage(menuTarget, WM_KEYDOWN, VK_ESCAPE, KeyDownInfo);
                    Native.PostMessage(menuTarget, WM_KEYUP, VK_ESCAPE, KeyUpInfo);
                } else if (found != placed) {
                    // Show at once in the last colors seen. The menu is still fading in, so its real colors can only be read a moment later.
                    placed = found;
                    lookAt = Environment.TickCount + FadeInMs;
                    menu.BeginInvoke((Action)(() => ShowPanel(found)));
                } else if (lookAt != 0 && Environment.TickCount - lookAt > 0) {
                    lookAt = 0;
                    Rectangle exact = MenuWindow.ReadLook(found);
                    menu.BeginInvoke((Action)(() => ShowPanel(exact)));
                }
                Thread.Sleep(1); // Really about 15 ms, which paces these reads while the menu is open.
            }
            menuWatching = false;
        }
    }

    void ShowPanel(Rectangle area) {
        menuArea = area;
        panel.Place(area);
        if (overlay != null) { overlay.Hole = area; overlay.RedrawColumn(); }
    }

    void HidePanel() {
        menuArea = Rectangle.Empty;
        panel.Hide();
        menu.Hide();
        picker.Hide();
        if (overlay != null) { overlay.Hole = Rectangle.Empty; overlay.RedrawColumn(); }
    }

    void ToggleColorList() {
        if (menu.Visible || picker.Visible) { menu.Hide(); picker.Hide(); panel.Expanded = false; return; }
        Row row = menuRow;
        Tint tint;
        Tint? current = colors.TryGetValue(row.Name, out tint) ? tint : (Tint?)null;
        var at = new Point(panel.Right + 4, panel.Top);
        Action<Tint?> choose = picked => {
            if (picked.HasValue) colors[row.Name] = picked.Value;
            else colors.Remove(row.Name);
            ColorFile.Save(colors);
            closeAppMenu = true;
            panel.Hide();
            menu.Hide();
            picker.Hide();
            if (overlay != null) overlay.RedrawColumn(); // Keeps its hole until the watcher sees the app's menu gone, so strips never cross it.
        };
        panel.Expanded = true;
        menu.Open(current, at, choose, gradient => {
            menu.Hide();
            picker.Open(current, gradient, at, picked => choose(picked));
        });
    }

    bool Moving { get { return Environment.TickCount - lastMove < SettleMs; } }

    // The rows were read with the window at WindowAt, so wherever it is now, the strips belong that same distance away.
    Point ShiftOf(Snapshot snapshot) {
        Native.RECT now;
        Native.GetWindowRect(owner, out now);
        return new Point(now.Left - snapshot.WindowAt.X, now.Top - snapshot.WindowAt.Y);
    }

    // Keeps the strips on the app's window while the left button is held after a press on it, which is when it gets dragged. Reading a window's position never involves the app.
    // Measured with real drags: the obvious way, subscribing to the app's window-move events, slowed the window itself from 10 ms behind the cursor to 90 ms.
    // ponytail: moves made without that press (keyboard snapping, say) wait for the next automation read. Left and right swapped in Windows settings would also miss this.
    void FollowDrag() {
        const int VK_LBUTTON = 1;
        while (true) {
            pressed.WaitOne();
            Native.RECT was;
            Native.GetWindowRect(owner, out was);
            Native.timeBeginPeriod(1); // Otherwise Sleep(1) really takes 15.5 ms. Only for as long as the button is held.
            Thread.Sleep(15); // Windows records the press only after the mouse hook that woke this returns.
            bool held;
            do {
                held = Native.GetAsyncKeyState(VK_LBUTTON) < 0;
                Thread.Sleep(1);
                Native.RECT now;
                Native.GetWindowRect(owner, out now);
                if (stripsQueued || (now.Left == was.Left && now.Top == was.Top)) continue; // One redraw at a time, which reads the latest position, so a slow one can't build a backlog.
                was = now;
                lastMove = Environment.TickCount;
                stripsQueued = true;
                menu.BeginInvoke((Action)MoveStrips);
            } while (held); // One last look after the release, for a move that came just before it.
            Native.timeEndPeriod(1);
        }
    }

    void MoveStrips() {
        stripsQueued = false;
        if (overlay != null && shown != null && shown.Rows.Count > 0) overlay.MoveColumn(ShiftOf(shown));
    }

    void Apply() {
        if (tracker.Active || Moving) return; // The pixels and FollowDrag are more current than automation's positions until things stop.
        Snapshot snapshot = latest;
        if (snapshot == shown && tracker.Offset == 0) return;
        Snapshot previous = shown;
        shown = snapshot;
        tracker.Offset = 0;

        if (snapshot.Window != owner) {
            if (overlay != null) overlay.Dispose();
            overlay = null;
            owner = snapshot.Window;
            if (owner != IntPtr.Zero) {
                overlay = new Overlay(colors);
                Native.SetWindowLongPtr(overlay.Handle, GWLP_HWNDPARENT, owner);
            }
        }
        if (overlay == null) return;
        if (snapshot.Rows.Count == 0) { overlay.Hide(); return; }
        overlay.RedrawColumn(); // Where the strips were.
        overlay.Rows = snapshot.Rows;
        overlay.Column = snapshot.Area;
        overlay.Shift = ShiftOf(snapshot);
        overlay.ScrollOffset = 0;
        overlay.FitToScreen();
        overlay.Show();
        overlay.RedrawColumn();

        // Catches scrolling the wheel hook can't see, like dragging the scrollbar. Foreground only, so another app's pixels are never read.
        if (colors.Count > 0 && Native.GetForegroundWindow() == owner && snapshot.IsScrollOf(previous)) tracker.Start(snapshot.Area);
    }

    void ShowOffset() {
        if (overlay == null || !overlay.IsHandleCreated) return;
        overlay.ScrollOffset = tracker.Offset + tracker.Lead;
        overlay.RedrawColumn();
        overlay.Update();
    }

    // Owning a window doesn't lift it above its owner until the owner is next activated, so place it directly.
    void KeepAboveOwner() {
        const uint GW_HWNDPREV = 3, SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
        if (overlay == null || !overlay.IsHandleCreated || !overlay.Visible || !Native.IsWindow(owner)) return;
        IntPtr above = Native.GetWindow(owner, GW_HWNDPREV);
        if (above != overlay.Handle) Native.SetWindowPos(overlay.Handle, above, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    // Called by Windows for every mouse event on the PC, so it returns at once for everything but button presses and the wheel.
    IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam) {
        int message = (int)wParam;
        if (code < 0 || (message != WM_LBUTTONDOWN && message != WM_RBUTTONDOWN && message != WM_MBUTTONDOWN && message != WM_MOUSEWHEEL))
            return Native.CallNextHookEx(hook, code, wParam, lParam);

        var info = (Native.MouseInfo)Marshal.PtrToStructure(lParam, typeof(Native.MouseInfo));
        var point = new Point(info.Point.X, info.Point.Y);

        if (message == WM_MOUSEWHEEL) {
            // Starting here, before the app has moved anything, means the very first frame of the scroll is followed.
            if (shown != null && colors.Count > 0 && shown.Area.Contains(point) && ClaudeIsAt(info.Point)) tracker.Start(shown.Area);
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }

        if (message == WM_LBUTTONDOWN && ClaudeIsAt(info.Point)) pressed.Set();

        // Every click goes through to the app now, so its own menu always opens. Ours only follows along.
        bool onOurs = (menu.Visible && menu.Bounds.Contains(point)) || (picker.Visible && picker.Bounds.Contains(point)) || (panel.Visible && panel.RowArea.Contains(point));
        bool inAppMenu = menuArea.Contains(point) && ClaudeIsAt(info.Point); // Up to the app. If that closes its menu, the watcher hides ours.
        if (onOurs || inAppMenu) return Native.CallNextHookEx(hook, code, wParam, lParam);

        if (panel.Visible) menu.BeginInvoke((Action)HidePanel);
        if (message == WM_RBUTTONDOWN) {
            Row row = RowUnder(info.Point, point);
            if (row != null) WatchForAppMenu(row, Native.WindowFromPoint(info.Point));
        }
        return Native.CallNextHookEx(hook, code, wParam, lParam);
    }

    void WatchForAppMenu(Row row, IntPtr target) {
        menuRow = row;
        menuTarget = target;
        closeAppMenu = false;
        menuWake.Set();
    }

    Row RowUnder(Native.POINT raw, Point point) {
        // While scrolling or moving, the saved row positions are out of date, so leave the click to the app.
        if (shown == null || owner == IntPtr.Zero || tracker.Active || Moving) return null;
        Row row = shown.RowAt(point);
        return row != null && ClaudeIsAt(raw) ? row : null;
    }

    // True only when the Claude window is what's actually on screen at that spot, not another app covering it.
    bool ClaudeIsAt(Native.POINT raw) {
        IntPtr root = Native.GetAncestor(Native.WindowFromPoint(raw), GA_ROOT);
        return root == owner || (overlay != null && overlay.IsHandleCreated && root == overlay.Handle);
    }

}

static class Program {
    [STAThread]
    static int Main(string[] args) {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // Per-monitor: UI Automation rectangles are then real screen pixels.
        if (args.Contains("--check")) return Check();

        bool isFirst;
        using (new Mutex(true, "ClaudeColorCoded-single-instance", out isFirst)) {
            if (!isFirst) return 0;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new App());
        }
        return 0;
    }

    static void Expect(bool passed, string what) {
        if (passed) return;
        Console.WriteLine("FAILED: " + what);
        Environment.Exit(2);
    }

    // Exits 0 if the logic holds and the sidebar's headings can be read, so it also shows when an app update has broken the lookup.
    static int Check() {
        Dictionary<string, Tint> parsed = ColorFile.Parse(new[] { "a=b=#112233", "g=#112233-#445566", "not a color line", "c=#nothex", "d=#112233-#nothex" });
        Expect(parsed.Count == 2 && parsed["a=b"].From.ToArgb() == Color.FromArgb(0x11, 0x22, 0x33).ToArgb(), "parse keeps '=' in names and skips bad lines");
        Expect(!parsed["a=b"].IsGradient && parsed["g"].IsGradient && parsed["g"].To.ToArgb() == Color.FromArgb(0x44, 0x55, 0x66).ToArgb(), "old one-color lines still load, and two colors make a gradient");
        Expect(ColorFile.Format(parsed).SequenceEqual(new[] { "a=b=#112233", "g=#112233-#445566" }), "format round-trips");

        foreach (int rgb in new[] { 0xEF4444, 0x22C55E, 0x3B82F6, 0xA855F7, 0x808080, 0xFFFFFF, 0x000000, 0xFF00FF }) {
            Color color = Color.FromArgb(unchecked((int)0xFF000000) | rgb);
            double h, s, v;
            ColorPicker.ToHsv(color, out h, out s, out v);
            Expect(ColorPicker.FromHsv(h, s, v).ToArgb() == color.ToArgb(), "the picker opens on the exact color it was given: " + rgb.ToString("x6"));
        }
        Expect(ColorPicker.FromHsv(360, 1, 1).ToArgb() == Color.Red.ToArgb(), "the far right of the hue bar is red again");

        var sample = new Snapshot { Area = new Rectangle(0, 50, 400, 500) };
        sample.Rows.Add(new Row { Name = "x", Bounds = new Rectangle(8, 100, 358, 32) });
        sample.Rows.Add(new Row { Name = "scrolled away", Bounds = new Rectangle(8, 10, 358, 32) });
        Expect(sample.RowAt(new Point(10, 110)) != null && sample.RowAt(new Point(10, 140)) == null, "right-click hit test");
        Expect(sample.RowAt(new Point(10, 20)) == null, "a row scrolled out of the list can't be right-clicked");

        var scrolled = new Snapshot { Area = sample.Area };
        scrolled.Rows.Add(new Row { Name = "x", Bounds = new Rectangle(8, 60, 358, 32) });
        scrolled.Rows.Add(new Row { Name = "scrolled away", Bounds = new Rectangle(8, -30, 358, 32) });
        Expect(scrolled.IsScrollOf(sample), "every row moving the same distance is a scroll");
        scrolled.Rows[1].Bounds.Y = 10;
        Expect(!scrolled.IsScrollOf(sample), "only some rows moving is a layout change, not a scroll");

        // Scroll tracking: every line unique, so only the true shift can line them all up.
        int[] before = Enumerable.Range(0, 400).Select(y => y * 37 + 1).ToArray();
        bool[] text = Enumerable.Repeat(true, 400).ToArray();
        int shift;
        Expect(ScrollTracker.FindShift(before, before, text, out shift) && shift == 0, "an unchanged frame has no shift");
        int[] after = Enumerable.Range(0, 400).Select(y => y >= 40 ? before[y - 40] : -y - 1).ToArray();
        Expect(ScrollTracker.FindShift(before, after, text, out shift) && shift == 40, "content moved down 40 lines");
        for (int y = 200; y < 232; y++) after[y] = -y - 1000;
        Expect(ScrollTracker.FindShift(before, after, text, out shift) && shift == 40, "a hovered row changing color doesn't throw it off");
        int[] unrelated = Enumerable.Range(0, 400).Select(y => -y - 1).ToArray();
        Expect(!ScrollTracker.FindShift(before, unrelated, text, out shift), "a frame that matches nothing is reported as unsure");
        bool[] blank = new bool[400];
        Expect(!ScrollTracker.FindShift(before, after, blank, out shift), "blank lines alone prove nothing");

        // Measured from the app's real menu at (210,235), 181 wide: its first item starts 6 below its top, so the row's bottom edge must land 14 below, past the rounded corner.
        Expect(ColorPanel.BoundsFor(new Rectangle(210, 235, 181, 301)) == new Rectangle(210, 198, 181, 51), "Select color row sits on the menu's top edge");
        Expect(MenuWindow.Mix(Color.FromArgb(0x30, 0x30, 0x30), Color.White, 0.075).R == 0x40, "hover color matches the stylesheet's 7.5% white");
        Console.WriteLine("logic: ok");

        // The first query can come back nearly empty: it's what switches the app's accessibility tree on.
        for (int attempt = 0; attempt < 10; attempt++) {
            IntPtr window = Sidebar.FindWindow();
            AutomationElement list = window == IntPtr.Zero ? null : Sidebar.FindList(window);
            if (list != null) {
                Snapshot snapshot = Sidebar.Read(window, list);
                Console.WriteLine("list area: " + snapshot.Area);
                foreach (Row row in snapshot.Rows) Console.WriteLine(row.Name + " | " + row.Bounds);
                return snapshot.Rows.Count > 0 ? 0 : 1;
            }
            Thread.Sleep(1000);
        }
        Console.WriteLine("no group headings found: is the Code tab showing, with the sidebar open?");
        return 1;
    }
}
