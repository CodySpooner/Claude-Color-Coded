// Claude Color Coded: right-click a group heading in the Claude desktop app's sidebar and choose Select font color or Select color.
//
// This is a separate helper, not a change to the app. It reads heading names and positions through
// Windows UI Automation (the API screen readers use) and draws over them in its own click-through
// windows: tinted strips, and recolored copies of the names. The color choices are its own windows too, drawn to look like part of the app's menu.
// The only thing it saves is the colors you pick for each group name.
//
// Build: build.cmd    Run: ClaudeColorCoded.exe    Quit: its tray icon    Diagnose: ClaudeColorCoded.exe --check
using System;
using System.Collections.Concurrent;
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

[assembly: System.Reflection.AssemblyTitle("Claude Color Coded")]
[assembly: System.Reflection.AssemblyProduct("Claude Color Coded")]
[assembly: System.Reflection.AssemblyCompany("Xody")]
[assembly: System.Reflection.AssemblyVersion("1.0.1.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("1.0.1")]

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
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screen, ref POINT at, ref SIZE size, IntPtr source, ref POINT from, int key, ref Blend blend, int flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr item);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr item);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
    [StructLayout(LayoutKind.Sequential)] public struct BitmapInfo { public int Size, Width, Height; public short Planes, Bits; public int Compression, ImageSize, XPerMeter, YPerMeter, Used, Important, Spare; }
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr to, int x, int y, int width, int height, IntPtr from, int fromX, int fromY, uint operation);
    [DllImport("gdi32.dll")] public static extern bool GdiFlush();
    [DllImport("user32.dll")] public static extern bool ValidateRect(IntPtr window, IntPtr rect);
    [DllImport("msvcrt.dll", CallingConvention = CallingConvention.Cdecl)] public static extern unsafe int memcmp(int* a, int* b, UIntPtr bytes);
    [DllImport("dxgi.dll")] public static extern int CreateDXGIFactory1(ref Guid id, out IntPtr factory);
    [DllImport("d3d11.dll")] public static extern int D3D11CreateDevice(IntPtr adapter, int driver, IntPtr software, uint flags, IntPtr levels, uint levelCount, uint version, out IntPtr device, out int level, out IntPtr context);
}

class Row {
    public string Name;
    public Rectangle Bounds;
    public AutomationElement Element; // The heading's row in the app, for finding its name's letters.
}

// Where a heading's letters sit in its row. Known even for rows scrolled out of view, so their names can be colored the moment they come into it.
struct NamePlace {
    public Rectangle Box; // From the row's corner.
    public Size RowSize; // Another size means a resized sidebar or a zoom, so it gets looked up again.
    public Rectangle On(Rectangle row) { return new Rectangle(row.X + Box.X, row.Y + Box.Y, Box.Width, Box.Height); }
}

class Snapshot {
    public IntPtr Window = IntPtr.Zero;
    public Point WindowAt; // Where the app's window was when these positions were read.
    public Rectangle Area = Rectangle.Empty;
    public List<Row> Rows = new List<Row>();

    public string Signature() {
        return Window + "|" + Area + "|" + string.Join(";", Rows.Select(r => r.Name + r.Bounds));
    }

    // The offset is how far the rows have scrolled since this was read.
    public Row RowAt(Point point, int offset) {
        return Area.Contains(point) ? Rows.FirstOrDefault(r => r.Bounds.Contains(point.X, point.Y - offset)) : null;
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

// Follows the sidebar when it moves. A window drawn over the app can only ever land a frame or two after what it covers, so while the sidebar moves the colors don't float over it at all:
// each new frame of the app's pixels is read, colored, and shown as one picture over the sidebar, so text and color can't separate. Clicks still pass through to the real app beneath.
// It runs only during a scroll and for a moment after a click in the sidebar, and pixels are kept in memory for that long and never saved.
unsafe class ScrollTracker {
    const int QuietMs = 300, MaxShift = 300, MinMatches = 20;
    const int FirstFrameMs = 30, FrameWaitMs = 20, GiveUpMs = 100; // A frame that fits no scroll isn't shown. If that lasts, it's a layout change and automation's positions take over.
    public const int EdgeInset = 4;
    const int SideInset = 12, MatchWidth = 150; // The part of each line compared between frames, clear of the scrollbar and of the buttons that appear on hovering.
    // A click in the sidebar can move rows, as expanding a group does. Measured: the rows jump in a single frame 35 to 65 ms after the click. Automation has their new places up to 50 ms after that, and can report the old ones again first.
    // So for a moment after a click each frame is checked against where the colored rows should be. One that doesn't fit isn't shown until automation offers positions that do, and then frame and positions go up together.
    const int ClickWatchMs = 400, HoldMs = 250;
    const int StrayAnywhere = 60; // More lines of text than this changing means rows moved. A click's highlights alone change about 30.
    const uint SRCCOPY = 0x00CC0020, WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    readonly AutoResetEvent wake = new AutoResetEvent(false);
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly Action ended;
    readonly Action<Snapshot> adopted;
    readonly object gate = new object();
    DesktopFrames frames;
    Point screenAt = new Point(int.MinValue, 0), triedAt = new Point(int.MinValue, 0);
    Scene next, offered;
    volatile int lastMotion, clickedAt;
    volatile bool stop, watchLayout, gaveUp;
    public readonly AutoResetEvent PollNow = new AutoResetEvent(false);
    public volatile bool Capturing;
    public volatile bool Active; // Stays on after capturing stops, until automation has been read again.
    public volatile bool Holding; // Keeping the last picture up, as the newest frame doesn't fit the positions known.
    public volatile int Offset;

    public ScrollTracker(Action ended, Action<Snapshot> adopted) {
        this.ended = ended;
        this.adopted = adopted;
        new Thread(Run) { IsBackground = true }.Start();
    }

    // Getting ready to read a screen takes 200 ms, too long to do on the first wheel notch, so it's done as soon as the sidebar is seen on that screen.
    public void Prepare(Point at) {
        lock (gate) screenAt = at;
        wake.Set();
    }

    public bool WatchingLayout { get { return watchLayout && clock.ElapsedMilliseconds - clickedAt < ClickWatchMs; } }

    // Positions from automation, to use once a frame agrees with them.
    public void Offer(Scene scene) { offered = scene; }

    // Only ever called on the app's main thread, as Stop is.
    public void Start(Func<Scene> build, bool clicked) {
        lastMotion = (int)clock.ElapsedMilliseconds;
        watchLayout = clicked; // A wheel notch ends the watch: from then on automation's positions are behind the pixels again.
        if (clicked) { clickedAt = lastMotion; gaveUp = false; }
        lock (gate) {
            if (Capturing) return;
            next = build();
            stop = false;
            Capturing = true;
            Active = true;
        }
        wake.Set();
        PollNow.Set();
    }

    // Other buttons can open menus and a drag moves the window, and the picture must not sit over that.
    public void Stop() { stop = true; }

    // Called once a read that began after the scroll stopped is in, so automation's positions can be trusted again.
    public void Finish() {
        lock (gate) { if (!Capturing) Active = false; }
    }

    public static void Exclude(IntPtr stripsWindow, IntPtr namesWindow, bool excluded) {
        Native.SetWindowDisplayAffinity(stripsWindow, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
        Native.SetWindowDisplayAffinity(namesWindow, excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    void Run() {
        while (true) {
            wake.WaitOne();
            Point at;
            Scene scene;
            lock (gate) { at = screenAt; scene = next; next = null; }
            if (at != triedAt && at.X != int.MinValue && (frames == null || !frames.Covers(at))) {
                triedAt = at;
                try { frames = DesktopFrames.Open(at); } catch (Exception) { frames = null; }
            }
            if (scene == null) continue;
            // ponytail: the screen can't be read on the lock screen. Automation's positions take over once this stops.
            try { Track(scene); } catch (Exception) { frames = null; triedAt = new Point(int.MinValue, 0); }
            Capturing = false;
            ended();
        }
    }

    void Track(Scene scene) {
        Rectangle part = scene.Part;
        int width = part.Width, height = part.Height, lines = height - 2 * EdgeInset;
        if (width <= 2 * SideInset || lines <= 0) return;
        int[] reference = new int[lines], current = new int[lines];
        var hasText = new bool[lines];
        IntPtr screen = Native.GetDC(IntPtr.Zero), target = Native.GetDC(scene.NamesWindow);
        // Measured: left out like this, our own windows never appear in what's read, and it takes effect at once. A screen recording also loses the colors for the length of a scroll.
        Exclude(scene.StripsWindow, scene.NamesWindow, true);
        bool duplicating = frames != null && frames.Covers(part) && frames.Start(part.Size);
        var raw = new PixelBuffer(width, height);
        var last = new PixelBuffer(width, height);
        var picture = new PixelBuffer(width, height);
        try {
            // The first frame comes back blank and a new one only arrives when something redraws, so nudge one along. This has to be the sidebar before it moves, as everything is measured from it.
            if (duplicating) Native.BitBlt(target, 0, 0, 1, 1, target, 0, 0, SRCCOPY);
            int began = (int)clock.ElapsedMilliseconds;
            while (!NextFrame(duplicating, part, raw, screen))
                if (duplicating && clock.ElapsedMilliseconds - began > FirstFrameMs) { frames.Stop(); duplicating = false; }
            Hash(raw.Pixels, width, reference, hasText);
            offered = null;
            scene.Prepare();
            scene.Names.Mirroring = true;
            scene.Bake(raw.Pixels, picture.Pixels, Offset);
            Present(scene, target, picture);
            PixelBuffer swap = raw; raw = last; last = swap;
            var looks = new Dictionary<string, int[]>();
            scene.Remember(looks, reference, hasText, Offset, Cursor.Position);
            Scene waiting = null;
            long lastMatch = clock.ElapsedMilliseconds, heldAt = 0;

            while (!stop && (Holding || clock.ElapsedMilliseconds - lastMotion < QuietMs)) {
                Scene offer = Interlocked.Exchange(ref offered, null);
                if (offer != null) {
                    if (offer.Part != part) break;
                    offer.Prepare();
                    waiting = offer;
                }
                // ponytail: a wheel notch during a hold isn't counted, so if automation can't catch up the colors can be off for the rest of that scroll, until it stops. Takes a click and a notch within 100 ms.
                bool late = Holding && clock.ElapsedMilliseconds - heldAt > HoldMs;
                if (waiting != null && (late || waiting.Fits(looks, reference, 0, Cursor.Position))) {
                    scene = waiting;
                    waiting = null;
                    Offset = 0;
                    scene.Remember(looks, reference, hasText, 0, Cursor.Position);
                    adopted(scene.From);
                    Holding = false;
                    lastMatch = clock.ElapsedMilliseconds;
                    lastMotion = (int)lastMatch;
                    scene.Bake(last.Pixels, picture.Pixels, Offset);
                    Present(scene, target, picture);
                } else if (late) { // Something that won't settle, like a row fading. One hold per click, so it can't keep the picture still.
                    gaveUp = true;
                    Holding = false;
                    lastMatch = clock.ElapsedMilliseconds;
                    scene.Bake(last.Pixels, picture.Pixels, Offset);
                    Present(scene, target, picture);
                }
                if (!NextFrame(duplicating, part, raw, screen)) continue;
                if (Native.memcmp(raw.Pixels, last.Pixels, new UIntPtr((uint)(width * height * 4))) == 0) continue; // Something else on screen changed.
                Hash(raw.Pixels, width, current, hasText);
                int shift, stray;
                bool fits = FindShift(reference, current, hasText, out shift, out stray);
                int[] swapLines;
                if (Holding || (!gaveUp && WatchingLayout)) {
                    int at = Holding ? Offset : Offset + shift; // While holding, the lines compared against have already moved on with the frames.
                    // A big change with a colored row out of view below could bring that row in, which only automation can tell.
                    bool small = !Holding && stray <= StrayAnywhere;
                    if (!fits || !scene.Fits(looks, current, at, Cursor.Position) || (!small && scene.MoreBelow(at, current.Length))) {
                        if (!Holding) { Holding = true; heldAt = clock.ElapsedMilliseconds; PollNow.Set(); }
                        swapLines = reference; reference = current; current = swapLines;
                        swap = raw; raw = last; last = swap; // The newest frame, ready for when the positions come.
                        continue;
                    }
                    if (Holding) { Holding = false; shift = 0; }
                }
                // If a frame matches nothing, the last good frame stays both the reference and what's showing, so that frame's motion is still counted by the next match.
                if (!fits) {
                    if (clock.ElapsedMilliseconds - lastMatch > GiveUpMs) break;
                    continue;
                }
                swapLines = reference; reference = current; current = swapLines;
                lastMatch = clock.ElapsedMilliseconds;
                if (shift != 0) { Offset += shift; lastMotion = (int)lastMatch; }
                scene.Bake(raw.Pixels, picture.Pixels, Offset);
                Present(scene, target, picture);
                swap = raw; raw = last; last = swap;
            }
            if (!Holding) scene.Settle(last.Pixels, picture.Pixels, Offset); // While holding, the last frame isn't what's showing.
        } finally {
            Holding = false;
            if (duplicating) frames.Stop();
            raw.Dispose();
            last.Dispose();
            picture.Dispose();
            Native.ReleaseDC(IntPtr.Zero, screen);
            Native.ReleaseDC(scene.NamesWindow, target);
        }
    }

    bool NextFrame(bool duplicating, Rectangle part, PixelBuffer into, IntPtr screen) {
        if (duplicating) return frames.Next(FrameWaitMs, part, into.Pixels);
        // Windows' older way, used when Desktop Duplication isn't available, as on a rotated screen. Measured at 11 ms a read, so about 80 frames a second.
        Native.BitBlt(into.Dc, 0, 0, part.Width, part.Height, screen, part.X, part.Y, SRCCOPY);
        Native.GdiFlush();
        return true;
    }

    static void Present(Scene scene, IntPtr target, PixelBuffer picture) {
        Native.BitBlt(target, scene.Part.X - scene.WindowAt.X, scene.Part.Y - scene.WindowAt.Y, scene.Part.Width, scene.Part.Height, picture.Dc, 0, 0, SRCCOPY);
        Native.GdiFlush();
    }

    static void Hash(int* pixels, int width, int[] hashes, bool[] hasText) {
        int span = Math.Min(MatchWidth, width - 2 * SideInset);
        for (int y = 0; y < hashes.Length; y++) {
            int* line = pixels + (y + EdgeInset) * width + SideInset;
            int first = line[0], hash = 17;
            bool text = false;
            for (int i = 0; i < span; i++) {
                hash = hash * 31 + line[i];
                if (line[i] != first) text = true;
            }
            hashes[y] = hash;
            hasText[y] = text;
        }
    }

    // Finds how far the lines moved since the reference frame, positive being down. Blank lines match anywhere, so only lines with text count.
    public static bool FindShift(int[] reference, int[] current, bool[] hasText, out int shift, out int stray) {
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
        stray = bestSeen - bestHits; // Lines of text that don't fit that shift.
        return bestHits >= MinMatches && bestHits * 2 >= bestSeen;
    }
}

// Pixels that both this code and Windows' drawing calls can use directly.
unsafe sealed class PixelBuffer : IDisposable {
    public readonly IntPtr Dc;
    public readonly int* Pixels;
    readonly IntPtr bitmap, original;

    public PixelBuffer(int width, int height) {
        var info = new Native.BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 }; // A negative height keeps the top line first.
        Dc = Native.CreateCompatibleDC(IntPtr.Zero);
        IntPtr bits;
        bitmap = Native.CreateDIBSection(Dc, ref info, 0, out bits, IntPtr.Zero, 0);
        original = Native.SelectObject(Dc, bitmap);
        Pixels = (int*)bits;
    }

    public void Dispose() {
        Native.SelectObject(Dc, original);
        Native.DeleteObject(bitmap);
        Native.DeleteDC(Dc);
    }
}

// Windows' Desktop Duplication: each new screen image, from the graphics card. Measured at 0.5 ms to copy the sidebar and 160 frames a second, where the older way takes 11 ms a read.
// Windows' C# compiler has no DirectX definitions, so its objects are called through their method tables. The numbers are each method's place in its table, from the DirectX headers.
// Kept running only while the picture is up: while it runs, Windows can't use some of its faster ways of drawing other apps.
// ponytail: on an HDR screen these frames may not match the screen's colors exactly. Tell Open to return null there if that gets seen.
unsafe class DesktopFrames {
    delegate uint ReleaseFn(IntPtr self);
    delegate int QueryFn(IntPtr self, ref Guid id, out IntPtr result);
    delegate int EnumFn(IntPtr self, uint index, out IntPtr result);
    delegate int DescFn(IntPtr self, byte* description);
    delegate int DuplicateFn(IntPtr self, IntPtr device, out IntPtr duplication);
    delegate int AcquireFn(IntPtr self, uint waitMs, byte* info, out IntPtr resource);
    delegate int ReleaseFrameFn(IntPtr self);
    delegate int CreateTextureFn(IntPtr self, uint* description, IntPtr initial, out IntPtr texture);
    delegate void CopyRegionFn(IntPtr self, IntPtr to, uint toSub, uint x, uint y, uint z, IntPtr from, uint fromSub, uint* box);
    delegate int MapFn(IntPtr self, IntPtr resource, uint sub, uint type, uint flags, IntPtr* mapped);
    delegate void UnmapFn(IntPtr self, IntPtr resource, uint sub);
    const int NoNewFrame = unchecked((int)0x887A0027), IdentityRotation = 1;

    IntPtr device, context, output, staging, duplication;
    Rectangle bounds;
    AcquireFn acquire;
    ReleaseFrameFn releaseFrame;
    CopyRegionFn copyRegion;
    MapFn map;
    UnmapFn unmap;

    static T Method<T>(IntPtr self, int place) where T : class {
        return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), place * IntPtr.Size), typeof(T));
    }

    static void Release(IntPtr self) { if (self != IntPtr.Zero) Method<ReleaseFn>(self, 2)(self); }

    static IntPtr Query(IntPtr self, string id) {
        Guid guid = new Guid(id);
        IntPtr result;
        Check(Method<QueryFn>(self, 0)(self, ref guid, out result));
        return result;
    }

    static void Check(int result) { if (result < 0) throw new ExternalException("DirectX", result); }

    // For the screen holding this point. Null where it can't work, as on a rotated screen, whose frames come sideways.
    public static DesktopFrames Open(Point at) {
        Guid factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        IntPtr factory, adapter;
        Check(Native.CreateDXGIFactory1(ref factoryId, out factory));
        DesktopFrames found = null;
        for (uint a = 0; found == null && Method<EnumFn>(factory, 12)(factory, a, out adapter) >= 0; a++) {
            IntPtr candidate;
            for (uint o = 0; found == null && Method<EnumFn>(adapter, 7)(adapter, o, out candidate) >= 0; o++) {
                byte* description = stackalloc byte[128];
                Method<DescFn>(candidate, 7)(candidate, description);
                int* corners = (int*)(description + 64); // After the 32-character device name.
                Rectangle bounds = Rectangle.FromLTRB(corners[0], corners[1], corners[2], corners[3]);
                if (bounds.Contains(at) && *(int*)(description + 84) == IdentityRotation) {
                    found = new DesktopFrames { bounds = bounds };
                    int level;
                    Check(Native.D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, 7, out found.device, out level, out found.context));
                    found.output = Query(candidate, "00cddea8-939b-4b83-a340-a685226666cc"); // IDXGIOutput1
                    found.copyRegion = Method<CopyRegionFn>(found.context, 46);
                    found.map = Method<MapFn>(found.context, 14);
                    found.unmap = Method<UnmapFn>(found.context, 15);
                }
                Release(candidate);
            }
            Release(adapter);
        }
        Release(factory);
        return found;
    }

    public bool Covers(Point at) { return bounds.Contains(at); }
    public bool Covers(Rectangle part) { return bounds.Contains(part); }

    public bool Start(Size size) {
        uint* description = stackalloc uint[11];
        description[0] = (uint)size.Width; description[1] = (uint)size.Height; description[2] = 1; description[3] = 1;
        description[4] = 87; description[5] = 1; description[6] = 0; // 32-bit BGRA, no multisampling.
        description[7] = 3; description[8] = 0; description[9] = 0x20000; description[10] = 0; // Readable by this code.
        if (Method<CreateTextureFn>(device, 5)(device, description, IntPtr.Zero, out staging) < 0) return false;
        if (Method<DuplicateFn>(output, 22)(output, device, out duplication) < 0) { Release(staging); staging = IntPtr.Zero; return false; }
        acquire = Method<AcquireFn>(duplication, 8);
        releaseFrame = Method<ReleaseFrameFn>(duplication, 14);
        return true;
    }

    public void Stop() {
        Release(duplication);
        Release(staging);
        duplication = staging = IntPtr.Zero;
    }

    // False if no new picture of the screen came in time. Only the mouse moving also counts as none, and that's what the blank first frame looks like.
    public bool Next(int waitMs, Rectangle part, int* pixels) {
        byte* info = stackalloc byte[64];
        IntPtr resource;
        int result = acquire(duplication, (uint)waitMs, info, out resource);
        if (result == NoNewFrame) return false;
        Check(result);
        bool hasPicture = *(long*)info != 0; // When the screen was last updated.
        if (hasPicture) {
            IntPtr texture = Query(resource, "6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // ID3D11Texture2D
            uint* box = stackalloc uint[6];
            box[0] = (uint)(part.X - bounds.X); box[1] = (uint)(part.Y - bounds.Y); box[2] = 0;
            box[3] = box[0] + (uint)part.Width; box[4] = box[1] + (uint)part.Height; box[5] = 1;
            copyRegion(context, staging, 0, 0, 0, 0, texture, 0, box);
            IntPtr* mapped = stackalloc IntPtr[2];
            Check(map(context, staging, 0, 1, 0, mapped));
            byte* from = (byte*)mapped[0];
            int pitch = *(int*)(mapped + 1), lineBytes = part.Width * 4;
            for (int y = 0; y < part.Height; y++) Buffer.MemoryCopy(from + y * pitch, pixels + y * part.Width, lineBytes, lineBytes);
            unmap(context, staging, 0);
            Release(texture);
        }
        Release(resource);
        releaseFrame(duplication);
        return hasPicture;
    }
}

// What's colored in the sidebar for the length of one scroll, and how to paint it into a frame of the app's pixels. All of it is worked out the same way the separate windows' pixels are at rest, so nothing visibly changes when a scroll starts or stops.
unsafe class Scene {
    const int NoStrip = -1;
    class Item {
        public string Name;
        public Rectangle Row;
        public int[] Strip; // The row's strip color by pixel, or NoStrip outside its rounded corners. Null if it has no highlight.
        public Rectangle Letters;
        public int[] Ink; // The name's color by column. Null if it has no font color.
        public Tint Font;
    }
    static readonly Dictionary<string, int[]> stripCache = new Dictionary<string, int[]>(); // Most strips are the same shape and few colors.
    readonly IEnumerable<Row> rows;
    readonly Point shift;
    readonly IDictionary<string, Tint> colors, fontColors;
    readonly IDictionary<string, NamePlace> places;
    readonly IDictionary<string, NameShape> shapes;
    readonly List<Item> items = new List<Item>();
    public readonly Rectangle Part; // The piece of the screen the picture covers.
    public readonly Point WindowAt;
    public readonly IntPtr StripsWindow, NamesWindow;
    public readonly NameLayer Names;
    public Snapshot From; // The read these positions came from, when automation offered them after a click.

    public Scene(IEnumerable<Row> rows, Point shift, Rectangle part, Point windowAt, IntPtr stripsWindow, NameLayer names, IntPtr namesWindow, IDictionary<string, Tint> colors, IDictionary<string, Tint> fontColors, IDictionary<string, NamePlace> places, IDictionary<string, NameShape> shapes) {
        this.rows = rows;
        this.shift = shift;
        this.colors = colors;
        this.fontColors = fontColors;
        this.places = places;
        this.shapes = shapes;
        Part = part;
        WindowAt = windowAt;
        StripsWindow = stripsWindow;
        Names = names;
        NamesWindow = namesWindow;
    }

    public void Prepare() {
        foreach (Row row in rows) {
            Tint strip, font;
            NamePlace place = new NamePlace();
            bool hasStrip = colors.TryGetValue(row.Name, out strip);
            bool hasName = fontColors.TryGetValue(row.Name, out font) && places.TryGetValue(row.Name, out place) && place.RowSize == row.Bounds.Size;
            if (!hasStrip && !hasName) continue;
            Rectangle at = row.Bounds;
            at.Offset(shift);
            var item = new Item { Name = row.Name, Row = at };
            if (hasStrip) item.Strip = StripPixels(strip, at.Size);
            if (hasName) {
                item.Font = font;
                item.Letters = place.On(at);
                item.Ink = new int[item.Letters.Width];
                for (int x = 0; x < item.Ink.Length; x++) item.Ink[x] = MenuWindow.Mix(font.From, font.To, x / (double)Math.Max(1, item.Ink.Length - 1)).ToArgb();
            }
            items.Add(item);
        }
    }

    // Drawn just as the strips' window draws them, so their edges land on the same pixels.
    static int[] StripPixels(Tint tint, Size size) {
        string key = tint.From.ToArgb() + "-" + tint.To.ToArgb() + "-" + size;
        int[] pixels;
        if (stripCache.TryGetValue(key, out pixels)) return pixels;
        pixels = new int[size.Width * size.Height];
        using (var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppRgb))
        using (Graphics g = Graphics.FromImage(bitmap)) {
            g.Clear(ClickThroughWindow.Clear);
            Overlay.DrawStrip(g, new Rectangle(Point.Empty, size), tint);
            BitmapData data = bitmap.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.ReadOnly, bitmap.PixelFormat);
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            bitmap.UnlockBits(data);
        }
        int clear = ClickThroughWindow.Clear.ToArgb() & 0xFFFFFF;
        for (int i = 0; i < pixels.Length; i++) pixels[i] = (pixels[i] & 0xFFFFFF) == clear ? NoStrip : pixels[i] & 0xFFFFFF;
        stripCache[key] = pixels;
        return pixels;
    }

    // Where a row's lines are among a frame's line hashes. The row the mouse is on doesn't count, as it changes without moving.
    bool Comparable(Item item, int lineCount, int offset, Point cursor, out int firstLine) {
        Rectangle row = InFrame(item.Row, offset);
        firstLine = row.Top - ScrollTracker.EdgeInset;
        return firstLine >= 0 && firstLine + row.Height <= lineCount && !row.Contains(cursor.X - Part.X, cursor.Y - Part.Y);
    }

    // Keeps how each colored row in view looks, by name. Lines without text can't tell one place from another, so they stay 0 and aren't compared.
    public void Remember(IDictionary<string, int[]> looks, int[] lines, bool[] hasText, int offset, Point cursor) {
        foreach (Item item in items) {
            int first;
            if (!Comparable(item, lines.Length, offset, cursor, out first)) continue;
            var look = new int[item.Row.Height];
            for (int i = 0; i < look.Length; i++) if (hasText[first + i]) look[i] = lines[first + i];
            looks[item.Name] = look;
        }
    }

    // Is each colored row in view where these positions say, going by how it looked before? Rows not seen before can't be checked.
    public bool Fits(IDictionary<string, int[]> looks, int[] lines, int offset, Point cursor) {
        foreach (Item item in items) {
            int first;
            int[] look;
            if (!looks.TryGetValue(item.Name, out look) || look.Length != item.Row.Height || !Comparable(item, lines.Length, offset, cursor, out first)) continue;
            int seen = 0, same = 0;
            for (int i = 0; i < look.Length; i++) {
                if (look[i] == 0) continue;
                seen++;
                if (lines[first + i] == look[i]) same++;
            }
            if (same * 10 < seen * 9) return false;
        }
        return true;
    }

    public bool MoreBelow(int offset, int lineCount) {
        return items.Any(item => InFrame(item.Row, offset).Bottom - ScrollTracker.EdgeInset > lineCount);
    }

    Rectangle InFrame(Rectangle box, int offset) {
        box.Offset(-Part.X, offset - Part.Y);
        return box;
    }

    // The blend Windows gives the strips' see-through window, each part rounded on its own. Measured against the real screen: right on all 136,728 color values tried.
    public static int WithStrip(int app, int strip) {
        return Channel(app >> 16, strip >> 16) << 16 | Channel(app >> 8, strip >> 8) << 8 | Channel(app, strip);
    }

    static int Channel(int app, int strip) {
        return ((strip & 255) * Overlay.Alpha + 127) / 255 + ((app & 255) * (255 - Overlay.Alpha) + 127) / 255;
    }

    // ponytail: a pixel that lands exactly on the see-through color shows the app beneath it, which is all but the same color.
    public void Bake(int* raw, int* into, int offset) {
        int width = Part.Width, height = Part.Height;
        Buffer.MemoryCopy(raw, into, width * height * 4, width * height * 4);
        foreach (Item item in items) {
            Rectangle row = InFrame(item.Row, offset);
            if (row.Bottom <= 0 || row.Top >= height) continue;
            if (item.Strip != null) {
                int right = Math.Min(width, row.Right);
                for (int y = Math.Max(0, row.Top); y < Math.Min(height, row.Bottom); y++) {
                    int* line = into + y * width;
                    int stripLine = (y - row.Top) * row.Width - row.Left;
                    for (int x = Math.Max(0, row.Left); x < right; x++) {
                        int tint = item.Strip[stripLine + x];
                        if (tint != NoStrip) line[x] = WithStrip(line[x], tint);
                    }
                }
            }
            if (item.Ink != null) BakeName(item, raw, into, offset);
        }
    }

    // Worked out from this frame's own pixels, so a row coming into view, or one the mouse is lighting up, is right straight away. Keep the sums the same as NameShape's.
    void BakeName(Item item, int* raw, int* into, int offset) {
        int width = Part.Width, height = Part.Height;
        Rectangle box = InFrame(item.Letters, offset), seen = Rectangle.Intersect(box, new Rectangle(0, 0, width, height));
        int beside = NameShape.LineBeside(seen, 0, height);
        if (seen.IsEmpty || beside < 0) return;
        int* rawLine = raw + beside * width;
        int* tintedLine = into + beside * width;
        double strongest = 0;
        for (int y = seen.Top; y < seen.Bottom; y++) {
            int* line = raw + y * width;
            for (int x = seen.Left; x < seen.Right; x++) {
                int p = line[x], b = rawLine[x];
                double lift = (((p >> 16) & 255) - ((b >> 16) & 255) + ((p >> 8) & 255) - ((b >> 8) & 255) + (p & 255) - (b & 255)) / 3.0;
                if (Math.Abs(lift) > Math.Abs(strongest)) strongest = lift;
            }
        }
        if (Math.Abs(strongest) < NameShape.MinContrast) return;
        for (int y = seen.Top; y < seen.Bottom; y++) {
            int* from = raw + y * width;
            int* to = into + y * width;
            for (int x = seen.Left; x < seen.Right; x++) {
                int p = from[x], b = rawLine[x];
                double red = ColorPicker.Clamp((((p >> 16) & 255) - ((b >> 16) & 255)) / strongest);
                double green = ColorPicker.Clamp((((p >> 8) & 255) - ((b >> 8) & 255)) / strongest);
                double blue = ColorPicker.Clamp(((p & 255) - (b & 255)) / strongest);
                if (Math.Max(red, Math.Max(green, blue)) < NameShape.Untouched) continue;
                int ink = item.Ink[x - box.Left], under = tintedLine[x];
                to[x] = Blend((under >> 16) & 255, (ink >> 16) & 255, red) << 16 | Blend((under >> 8) & 255, (ink >> 8) & 255, green) << 8 | Blend(under & 255, ink & 255, blue);
            }
        }
    }

    static int Blend(int from, int to, double amount) { return (int)Math.Round(from + (to - from) * amount); }

    // When the scroll ends the names go back to their own window, so each one in view gets its letters and background from this last frame. Otherwise it would flash the app's color until the next read.
    public void Settle(int* raw, int* baked, int offset) {
        foreach (Item item in items) {
            if (item.Ink == null) continue;
            Rectangle row = InFrame(item.Row, offset), seen = Rectangle.Intersect(InFrame(item.Letters, offset), new Rectangle(Point.Empty, Part.Size));
            int beside = NameShape.LineBeside(seen, 0, Part.Height);
            if (seen.IsEmpty || beside < 0) continue;
            NameShape shape;
            bool known = shapes.TryGetValue(item.Name, out shape) && shape.RowSize == item.Row.Size && shape.On(row).Contains(seen);
            Rectangle columns = known ? shape.On(row) : seen;
            Color[] tinted = ReadLine(baked, beside, columns);
            if (!known) {
                var pixels = new Color[seen.Width, seen.Height];
                for (int x = 0; x < seen.Width; x++)
                    for (int y = 0; y < seen.Height; y++) pixels[x, y] = Color.FromArgb(raw[(seen.Y + y) * Part.Width + seen.X + x] | unchecked((int)0xFF000000));
                shape = NameShape.Measure(pixels, ReadLine(raw, beside, seen));
                if (shape == null) continue;
                shape.Offset = new Point(seen.X - row.X, seen.Y - row.Y);
                shape.RowSize = item.Row.Size;
            } else if (tinted.SequenceEqual(shape.Above)) continue;
            shape.Above = tinted;
            shape.Render(item.Font);
            shapes[item.Name] = shape;
        }
    }

    Color[] ReadLine(int* pixels, int y, Rectangle columns) {
        var line = new Color[columns.Width];
        for (int x = 0; x < columns.Width; x++) line[x] = Color.FromArgb(pixels[y * Part.Width + columns.X + x] | unchecked((int)0xFF000000));
        return line;
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

    // Measured: for a moment as a menu opens, every heading's button reports the same parent, as tall as the whole list. Drawn, that's one strip filling the sidebar.
    public static bool IsRow(Rectangle box, Rectangle list) {
        return box.Width > 0 && box.Height > 0 && box.Height < list.Height / 2;
    }

    public static Snapshot Read(IntPtr window, AutomationElement list) {
        var snapshot = new Snapshot { Window = window, Area = ToRectangle(list.Current.BoundingRectangle) };
        foreach (AutomationElement button in Buttons(list)) {
            string name = button.Cached.Name;
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;
            // The button's parent is the full heading row. Rows scrolled out of view are kept: none on screen doesn't mean none exist.
            AutomationElement parent = TreeWalker.RawViewWalker.GetParent(button);
            Rectangle row = ToRectangle(parent.Current.BoundingRectangle);
            if (IsRow(row, snapshot.Area))
                snapshot.Rows.Add(new Row { Name = name.Substring(Prefix.Length), Bounds = row, Element = parent });
        }
        return snapshot;
    }

    // New positions for rows already found, which costs next to nothing and keeps nothing. Throws if one has gone: measured, all of them do as soon as the app's menu hides its page.
    public static Snapshot Reread(IntPtr window, AutomationElement list, Snapshot found) {
        var snapshot = new Snapshot { Window = window, Area = ToRectangle(list.Current.BoundingRectangle) };
        foreach (Row known in found.Rows) {
            Rectangle row = ToRectangle(known.Element.Current.BoundingRectangle);
            if (row.Width > 0 && row.Height > 0)
                snapshot.Rows.Add(new Row { Name = known.Name, Bounds = row, Element = known.Element });
        }
        return snapshot;
    }

    // The rectangle hugging a heading's letters, kept to the part of the row that text can use. Only a raw walk finds it: the normal search skips text nested in a button.
    // ponytail: leans on an empty class name and a class prefix, so an app update can break it. Names then just keep the app's color.
    public static Rectangle FindName(Row row) {
        Rectangle name = Rectangle.Empty, room = Rectangle.Empty;
        var todo = new Stack<AutomationElement>();
        todo.Push(row.Element);
        while (todo.Count > 0) {
            AutomationElement element = todo.Pop();
            if (element.Current.ControlType == ControlType.Text) {
                string kind = element.Current.ClassName;
                if (kind == "" && element.Current.Name == row.Name) name = ToRectangle(element.Current.BoundingRectangle);
                else if (kind.StartsWith("flex min-w-0", StringComparison.Ordinal)) room = ToRectangle(element.Current.BoundingRectangle);
            }
            for (AutomationElement child = TreeWalker.RawViewWalker.GetFirstChild(element); child != null; child = TreeWalker.RawViewWalker.GetNextSibling(child)) todo.Push(child);
        }
        if (name.IsEmpty) return name;
        name.Inflate(1, 0); // Letters can hang a pixel outside their box.
        if (!room.IsEmpty) name.Intersect(room);
        return name;
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
    public const string Highlights = "colors.txt", FontColors = "font-colors.txt";
    static readonly Regex Line = new Regex("^(.+)=#([0-9a-fA-F]{6})(?:-#([0-9a-fA-F]{6}))?$");

    static string PathOf(string file) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeColorCoded", file); }

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

    public static IEnumerable<string> Format(IDictionary<string, Tint> colors) {
        return colors.Select(c => c.Key + "=" + ToHex(c.Value.From) + (c.Value.IsGradient ? "-" + ToHex(c.Value.To) : ""));
    }

    public static Dictionary<string, Tint> Load(string file) {
        string path = PathOf(file);
        return Parse(File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8) : new string[0]);
    }

    public static void Save(string file, IDictionary<string, Tint> colors) {
        string path = PathOf(file);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllLines(path, Format(colors), Encoding.UTF8);
    }
}

// The letters of a group's name, read off the screen. The app's text can't be changed, so the name is repainted over itself in the chosen color.
// Measuring the real pixels keeps the app's own font and its colored subpixel edges, which drawing the text again with a system font wouldn't.
class NameShape {
    public const int MinContrast = 20, BesideGap = 2; // How far from the letters the background is read, clear of anything hanging off them.
    public const double Untouched = 0.02;
    readonly double[,,] coverage; // How much of each pixel's red, green and blue the letters cover, 0 to 1.
    public Point Offset; // From the row's corner.
    public Size RowSize; // Another size means a resized sidebar or a zoom, so the name gets read again.
    public Color[] Above; // The background beside the letters, by column. Strips and hovering change it, so it's read again and again.
    public Bitmap Image;

    NameShape(double[,,] coverage) { this.coverage = coverage; }

    public static Color[,] Read(Rectangle box) {
        var pixels = new Color[box.Width, box.Height];
        using (var bitmap = new Bitmap(box.Width, box.Height, PixelFormat.Format32bppRgb))
        using (Graphics g = Graphics.FromImage(bitmap)) {
            g.CopyFromScreen(box.Location, Point.Empty, box.Size);
            for (int x = 0; x < box.Width; x++)
                for (int y = 0; y < box.Height; y++) pixels[x, y] = bitmap.GetPixel(x, y);
        }
        return pixels;
    }

    public static Color[] ReadLine(int y, Rectangle columns) {
        Color[,] line = Read(new Rectangle(columns.X, y, columns.Width, 1));
        return Enumerable.Range(0, columns.Width).Select(x => line[x, 0]).ToArray();
    }

    // The line to read the background from: just over the letters, or just under them when the list's edge has cut off the line above. Negative if neither is in view.
    public static int LineBeside(Rectangle letters, int top, int bottom) {
        int above = letters.Top - BesideGap, below = letters.Bottom + BesideGap - 1;
        return above >= top ? above : below < bottom ? below : -1;
    }

    // Null if these pixels hold no text. The background is judged column by column because a gradient strip's changes across the name.
    public static NameShape Measure(Color[,] pixels, Color[] above) {
        int width = pixels.GetLength(0), height = pixels.GetLength(1);
        double strongest = 0;
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++) {
                double lift = (pixels[x, y].R - above[x].R + pixels[x, y].G - above[x].G + pixels[x, y].B - above[x].B) / 3.0;
                if (Math.Abs(lift) > Math.Abs(strongest)) strongest = lift;
            }
        if (Math.Abs(strongest) < MinContrast) return null;
        var coverage = new double[width, height, 3];
        for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++) {
                coverage[x, y, 0] = ColorPicker.Clamp((pixels[x, y].R - above[x].R) / strongest);
                coverage[x, y, 1] = ColorPicker.Clamp((pixels[x, y].G - above[x].G) / strongest);
                coverage[x, y, 2] = ColorPicker.Clamp((pixels[x, y].B - above[x].B) / strongest);
            }
        return new NameShape(coverage) { Above = above };
    }

    public Rectangle On(Rectangle row) {
        return new Rectangle(row.X + Offset.X, row.Y + Offset.Y, coverage.GetLength(0), coverage.GetLength(1));
    }

    // Each pixel is solid, worked out against the background, rather than see-through: anything see-through would let the app's own letters show round the edges.
    // A gradient runs across the name, not the whole row.
    public Color ColorAt(int x, int y, Tint tint) {
        double red = coverage[x, y, 0], green = coverage[x, y, 1], blue = coverage[x, y, 2];
        if (Math.Max(red, Math.Max(green, blue)) < Untouched) return ClickThroughWindow.Clear;
        Color ink = MenuWindow.Mix(tint.From, tint.To, x / (double)Math.Max(1, coverage.GetLength(0) - 1)), under = Above[x];
        Color result = Color.FromArgb(MenuWindow.Mix(under, ink, red).R, MenuWindow.Mix(under, ink, green).G, MenuWindow.Mix(under, ink, blue).B);
        return result.ToArgb() == ClickThroughWindow.Clear.ToArgb() ? Color.FromArgb(result.R, result.G, result.B + 1) : result; // Or the pixel would vanish.
    }

    public void Render(Tint tint) {
        var fresh = new Bitmap(coverage.GetLength(0), coverage.GetLength(1), PixelFormat.Format32bppRgb);
        for (int x = 0; x < fresh.Width; x++)
            for (int y = 0; y < fresh.Height; y++) fresh.SetPixel(x, y, ColorAt(x, y, tint));
        if (Image != null) Image.Dispose();
        Image = fresh;
    }
}

// A window that draws over the app without ever taking a click or the focus.
abstract class ClickThroughWindow : Form {
    public static readonly Color Clear = Color.FromArgb(1, 2, 3); // Pixels of this exact color turn see-through, so it can't be one anybody would pick.

    protected ClickThroughWindow() {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = TransparencyKey = Clear;
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
}

// The tinted strips. Click-through, and owned by the Claude window so it stays with it in the window stack.
// It covers the sidebar's screen and stays put, and the strips are redrawn to follow the app's window. Measured with real drags, with no helper running: moving any other window
// during a drag left the app's window 260 ms behind the cursor, whatever the flags, against 5 ms with nothing else happening. A full-screen window redrawing also left it at 5 ms.
class Overlay : ClickThroughWindow {
    public const double Strength = 0.3;
    public const int Alpha = (int)(Strength * 255); // What Windows Forms hands Windows for that opacity.
    readonly Dictionary<string, Tint> colors;
    public readonly NameLayer Names;
    public List<Row> Rows = new List<Row>();
    public Rectangle Column; // The sidebar, where the rows' positions were measured.
    public Point Shift; // How far the app's window has moved since then.
    public int ScrollOffset;
    public Rectangle Hole; // The app's own menu while it's open. This window sits above the whole app, so strips must not be drawn across it.

    public Overlay(Dictionary<string, Tint> colors, IDictionary<string, Tint> fontColors, IDictionary<string, NameShape> shapes) {
        this.colors = colors;
        Opacity = Strength;
        Names = new NameLayer(this, fontColors, shapes);
    }

    protected override void Dispose(bool disposing) {
        if (disposing) Names.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnVisibleChanged(EventArgs e) {
        base.OnVisibleChanged(e);
        Names.Sync();
    }

    Rectangle ColumnNow {
        get {
            Rectangle box = Column;
            box.Offset(Shift.X - Left, Shift.Y - Top);
            return box;
        }
    }

    // How far a row's position is from where it lands in these windows.
    public Point RowShift { get { return new Point(Shift.X - Left, Shift.Y - Top + ScrollOffset); } }

    public void ClipToColumn(Graphics g) {
        g.IntersectClip(ColumnNow);
        if (!Hole.IsEmpty) g.ExcludeClip(new Rectangle(Hole.X - Left, Hole.Y - Top, Hole.Width, Hole.Height));
    }

    // Only ever the sidebar's part of these windows, which would be slow to redraw whole.
    public void RedrawColumn() {
        Invalidate(ColumnNow);
        Names.RedrawNames();
    }

    // For when more than the names' own spots needs wiping: the picture a scroll leaves, or a name that's lost its color.
    public void WipeNames() { Names.Invalidate(ColumnNow); }

    public Rectangle PartOnScreen { get { return Rectangle.Intersect(new Rectangle(Column.X + Shift.X, Column.Y + Shift.Y, Column.Width, Column.Height), Bounds); } }

    public void PaintNow() {
        Update();
        Names.Update();
    }

    public void MoveColumn(Point shift) {
        RedrawColumn();
        Shift = shift;
        FitToScreen();
        RedrawColumn();
        PaintNow();
    }

    // The screen the sidebar is on, leaving out its taskbar, and a pixel short for a hidden taskbar. Measured: a window covering a whole screen counts as a fullscreen app, and the taskbar
    // loses its always-on-top. Only moves when the sidebar crosses to another screen.
    // ponytail: while the sidebar straddles two screens, the part on the other one has no strips. Fixes itself once most of it is across.
    public void FitToScreen() {
        Rectangle column = Column;
        column.Offset(Shift);
        Rectangle area = Screen.FromRectangle(column).WorkingArea;
        Bounds = new Rectangle(area.X, area.Y, area.Width, area.Height - 1);
        Names.Bounds = Bounds;
    }

    protected override void OnPaint(PaintEventArgs e) {
        // Smoothing stays off: blended edge pixels wouldn't match the transparency color and would show as fringes.
        e.Graphics.SmoothingMode = SmoothingMode.None;
        ClipToColumn(e.Graphics);
        foreach (Row row in Rows) {
            Tint tint;
            if (!colors.TryGetValue(row.Name, out tint)) continue;
            Rectangle box = row.Bounds;
            box.Offset(RowShift);
            DrawStrip(e.Graphics, box, tint);
        }
    }

    public static void DrawStrip(Graphics g, Rectangle box, Tint tint) {
        using (GraphicsPath path = Rounded(box, 16))
        using (Brush brush = tint.BrushFor(box))
            g.FillPath(brush, path);
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

// The recolored names, in their own window above the strips' see-through one so they keep their full strength. It's solid where it draws, so during a scroll it also shows the whole baked picture of the sidebar.
class NameLayer : ClickThroughWindow {
    readonly Overlay strips;
    readonly IDictionary<string, Tint> fontColors;
    readonly IDictionary<string, NameShape> shapes;
    public volatile bool Mirroring; // The scroll tracker is drawing here directly, so painting must leave it alone.

    public NameLayer(Overlay strips, IDictionary<string, Tint> fontColors, IDictionary<string, NameShape> shapes) {
        this.strips = strips;
        this.fontColors = fontColors;
        this.shapes = shapes;
    }

    public void Sync() { Visible = strips.Visible; }

    protected override void WndProc(ref Message m) {
        const int WM_PAINT = 0xF;
        if (m.Msg == WM_PAINT && Mirroring) { Native.ValidateRect(Handle, IntPtr.Zero); return; }
        base.WndProc(ref m);
    }

    // Just the names' own spots, as they're all that's ever on this window at rest. Redrawing the rest during a drag would slow the app's window down for nothing.
    public void RedrawNames() {
        Point shift = strips.RowShift;
        foreach (Row row in strips.Rows) {
            NameShape shape;
            if (!fontColors.ContainsKey(row.Name) || !shapes.TryGetValue(row.Name, out shape) || shape.Image == null || shape.RowSize != row.Bounds.Size) continue;
            Rectangle box = shape.On(row.Bounds);
            box.Offset(shift);
            Invalidate(box);
        }
    }

    protected override void OnPaint(PaintEventArgs e) {
        strips.ClipToColumn(e.Graphics);
        // Copied exactly: a blended pixel would match neither its background nor the see-through color.
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        Point shift = strips.RowShift;
        foreach (Row row in strips.Rows) {
            NameShape shape;
            if (!fontColors.ContainsKey(row.Name) || !shapes.TryGetValue(row.Name, out shape) || shape.Image == null || shape.RowSize != row.Bounds.Size) continue;
            var at = new Point(row.Bounds.X + shape.Offset.X + shift.X, row.Bounds.Y + shape.Offset.Y + shift.Y);
            e.Graphics.DrawImage(shape.Image, new Rectangle(at, shape.Image.Size));
        }
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

    // Measured: ordinary windows can end up stacked above always-on-top ones, and showing a window doesn't raise it, so this could open hidden behind the app's window.
    protected override void OnVisibleChanged(EventArgs e) {
        base.OnVisibleChanged(e);
        const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
        if (Visible) Native.SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE); // HWND_TOPMOST
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

// The Select font color and Select color rows. The app's menu can't be changed from outside, so this is a separate window that takes the place of the menu's top edge.
// It sits above the menu's first item rather than at the bottom next to the red Delete item, so a slip can't land on that.
// ponytail: opens on click rather than hover, the app's arrow keys can't reach it, and sizes assume 100% display scaling. Scale them if a scaled screen gets used.
class ColorPanel : MenuWindow {
    public const int FontRow = 0, HighlightRow = 1;
    static readonly string[] Labels = { "Select font color", "Select color" };
    const int FirstAppItem = Gutter + ItemHeight * 2 + SeparatorGap, Above = FirstAppItem - Gutter;
    int hover = -1, expanded = -1;
    public Action<int> Clicked;

    // Runs down past the menu's top to cover its rounded corners and continue its straight edges.
    // ponytail: for a heading right at the top of the screen the rows stay on screen by covering part of the app's first item. Scrolling down a little frees it.
    public static Rectangle BoundsFor(Rectangle menu) {
        return new Rectangle(menu.X, Math.Max(menu.Y - Above, Screen.FromRectangle(menu).WorkingArea.Top), menu.Width, Above + Corner);
    }

    // Where this window is solid and takes clicks, on screen. Below it, the app's own items show through a hole.
    public Rectangle RowArea { get { return new Rectangle(Left, Top, Width, FirstAppItem); } }

    Rectangle Item(int row) { return new Rectangle(Gutter, Gutter + ItemHeight * row, Width - Gutter * 2 + 1, ItemHeight); }

    int RowAt(Point point) {
        for (int row = 0; row < Labels.Length; row++) if (Item(row).Contains(point)) return row;
        return -1;
    }

    public int TopOf(int row) { return Top + ItemHeight * row; }

    public void Place(Rectangle menu) {
        Bounds = BoundsFor(menu);
        hover = expanded = -1;
        Render();
        Show();
    }

    public int Expanded {
        set { expanded = value; if (Visible) Render(); }
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        int over = RowAt(e.Location);
        if (over != hover) { hover = over; Render(); }
    }

    protected override void OnMouseLeave(EventArgs e) {
        hover = -1;
        Render();
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        int row = RowAt(e.Location);
        if (e.Button == MouseButtons.Left && row >= 0) Clicked(row);
    }

    protected override void Draw(Graphics g) {
        DrawFrame(g, Height + Corner * 2); // Runs off the bottom of the window, so only the rounded top shows.
        for (int row = 0; row < Labels.Length; row++) {
            Rectangle item = Item(row);
            if (row == hover || row == expanded) DrawHighlight(g, item);
            DrawLabel(g, Labels[row], item.X + ItemPad, item.Y);
            int mid = item.Y + item.Height / 2, x = item.Right - 15;
            using (var pen = new Pen(Ink, 1.5f)) g.DrawLines(pen, new[] { new PointF(x, mid - 4), new PointF(x + 4, mid), new PointF(x, mid + 4) });
        }
        DrawSeparator(g, Item(Labels.Length - 1).Bottom + 4);

        // The hole over the app's first item, so it shows through and stays clickable. Fully clear pixels let the mouse through.
        g.CompositingMode = CompositingMode.SourceCopy;
        g.SmoothingMode = SmoothingMode.None;
        using (var clear = new SolidBrush(Color.Transparent)) g.FillRectangle(clear, Gutter, FirstAppItem, Item(0).Width, Height - FirstAppItem);
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
    double strength; // How solid the result will look: a highlight is see-through, a name isn't.
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

    public void Open(Tint? current, bool gradient, Point at, double strength, Action<Tint> pick) {
        this.gradient = gradient;
        this.strength = strength;
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

    public static double Clamp(double value) { return Math.Max(0, Math.Min(1, value)); }

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

        // The preview is as see-through as the result, so it shows roughly how it will look.
        int alpha = (int)(255 * strength);
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
    const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_MOUSEWHEEL = 0x20A;
    const int GWLP_HWNDPARENT = -8;
    const uint GA_ROOT = 2;
    const int SettleMs = 250; // Automation's positions lag by up to 160 ms, so they can't be trusted until this long after the window stops.
    const int MaxLateReads = 15; // About 1.2 s with the app in front.

    readonly Dictionary<string, Tint> colors = ColorFile.Load(ColorFile.Highlights);
    readonly ConcurrentDictionary<string, Tint> fontColors = new ConcurrentDictionary<string, Tint>(ColorFile.Load(ColorFile.FontColors)); // Also read off the UI thread, as are the shapes.
    readonly ConcurrentDictionary<string, NameShape> shapes = new ConcurrentDictionary<string, NameShape>();
    readonly ConcurrentDictionary<string, NamePlace> places = new ConcurrentDictionary<string, NamePlace>();
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
    Snapshot shown, offered;
    Overlay overlay;
    IntPtr owner = IntPtr.Zero;
    Row menuRow; // The heading that was right-clicked, and the window that took the click.
    IntPtr menuTarget;
    Rectangle menuArea; // The app's own menu, while the color rows are attached to it.
    int listFor = -1; // Which color row the open list or picker belongs to.
    volatile bool closeAppMenu, menuWatching;

    public App() {
        GC.KeepAlive(menu.Handle); // The mouse hook and the tracker post to the menu, so its window must exist first.
        tracker = new ScrollTracker(() => menu.BeginInvoke((Action)EndPicture), snapshot => menu.BeginInvoke((Action)(() => { shown = snapshot; Place(snapshot); })));

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

    // Searching the tree takes 30 ms, with the app working through it too, and measured, each search keeps about 16 KB of Windows' memory for good, whatever gets cleaned up. So rows found are held on to and searched for again only now and then.
    // A heading that newly appears can wait this long for its colors, unless a click moved rows, which searches at once.
    // ponytail: measured, that still comes to about 12 MB an hour with Claude in front. On a page with no sidebar it searches once a second, which works out at about 60 MB an hour.
    const int SearchInFrontMs = 5000, SearchBehindMs = 30000;

    void PollLoop() {
        IntPtr window = IntPtr.Zero;
        AutomationElement list = null;
        Snapshot found = null;
        string last = "";
        int failures = 0, still = 0, lateReads = 0, searchedAt = Environment.TickCount - SearchBehindMs;
        while (true) {
            // Measured: while the app's menu is open the app hides its page from automation, so a read finds no headings and would wipe the strips. Keep what's drawn.
            // While the window moves, automation's positions arrive late and row by row, and FollowDrag covers that time.
            if (menuWatching || Moving) { still = 0; Thread.Sleep(50); continue; }
            int moveBefore = lastMove;
            var snapshot = new Snapshot();
            bool fast = false, searched = false;
            bool scrollEnded = tracker.Active && !tracker.Capturing; // Checked before reading, so this read is newer than the scroll.
            try {
                if (!Native.IsWindow(window)) { window = Sidebar.FindWindow(); list = null; found = null; }
                snapshot.Window = window;
                if (window != IntPtr.Zero && !Native.IsIconic(window)) {
                    bool front = Native.GetForegroundWindow() == window;
                    bool searchDue = Environment.TickCount - searchedAt > (front ? SearchInFrontMs : SearchBehindMs);
                    if (list == null && (front || searchDue)) {
                        list = Sidebar.FindList(window);
                        searchedAt = Environment.TickCount;
                    }
                    if (list != null) {
                        Native.RECT at;
                        Native.GetWindowRect(window, out at);
                        searched = found == null || searchDue;
                        snapshot = searched ? Sidebar.Read(window, list) : Sidebar.Reread(window, list, found);
                        snapshot.WindowAt = new Point(at.Left, at.Top);
                        if (searched) { found = snapshot; searchedAt = Environment.TickCount; }
                        else if (snapshot.Rows.Count != found.Rows.Count) found = null; // A row has gone, so look again next time.
                        if (snapshot.Rows.Count == 0) { list = null; found = null; }
                    }
                    fast = list != null && front;
                }
                failures = 0;
            } catch (Exception) {
                // Measured: while the app is busy, reads fail every second or two and the next one works.
                // So keep what's drawn and retry. Only a long run of failures means the page really changed.
                found = null;
                if (++failures < 20) { Thread.Sleep(20); continue; }
                failures = 0;
                list = null;
                snapshot = new Snapshot { Window = window };
            }
            if (lastMove != moveBefore) continue; // The window moved during this read, so its positions are a mix.
            string signature = snapshot.Signature();
            if (signature != last) {
                last = signature;
                latest = snapshot;
                still = 0;
                if (tracker.WatchingLayout) {
                    menu.BeginInvoke((Action)Apply); // Rather than wait for the timer, when the picture may be waiting on this.
                    if (!searched) { found = null; tracker.PollNow.Set(); } // Rows moved after a click, and there may be new ones among them.
                }
            } else still++;
            // Measured: automation can still report the old positions a good while after a scroll stops, and handing over to those would throw the strips back to where they were.
            // So wait for it to catch up. If it never does, the layout changed during the scroll, or the tracking was off, and automation is right.
            if (scrollEnded && (CaughtUp(snapshot) || ++lateReads > MaxLateReads)) { lateReads = 0; tracker.Finish(); }
            // Automation's positions arrive late, so names are only read once they've held for a couple of reads.
            if (still >= 2 && fast && !tracker.Active) {
                try { ReadNames(snapshot); } catch (Exception) { } // Reads fail now and then while the app is busy, and the next one works.
            }
            tracker.PollNow.WaitOne(tracker.Holding ? 1 : fast || tracker.Active ? 80 : 1000);
        }
    }

    bool CaughtUp(Snapshot snapshot) {
        Snapshot before = shown;
        if (before == null || before.Rows.Count != snapshot.Rows.Count || snapshot.Rows.Count == 0 || before.Rows[0].Name != snapshot.Rows[0].Name) return true;
        return snapshot.Rows[0].Bounds.Y - snapshot.WindowAt.Y == before.Rows[0].Bounds.Y - before.WindowAt.Y + tracker.Offset;
    }

    // Only with the app in front and its sidebar still, so these are the app's own pixels. What's read stays in memory.
    void ReadNames(Snapshot snapshot) {
        foreach (Row row in snapshot.Rows) {
            if (!fontColors.ContainsKey(row.Name)) continue;
            NamePlace place;
            if (!places.TryGetValue(row.Name, out place) || place.RowSize != row.Bounds.Size) {
                Rectangle letters = Sidebar.FindName(row);
                if (letters.IsEmpty) continue;
                place = new NamePlace { Box = new Rectangle(letters.X - row.Bounds.X, letters.Y - row.Bounds.Y, letters.Width, letters.Height), RowSize = row.Bounds.Size };
                places[row.Name] = place;
            }
            Rectangle seen = Rectangle.Intersect(place.On(row.Bounds), snapshot.Area);
            int beside = NameShape.LineBeside(seen, snapshot.Area.Top, snapshot.Area.Bottom);
            if (seen.IsEmpty || beside < 0) continue;
            int mid = seen.Y + seen.Height / 2;
            if (!ClaudeIsAt(new Native.POINT { X = seen.Left, Y = mid }) || !ClaudeIsAt(new Native.POINT { X = seen.Left + seen.Width / 2, Y = mid }) || !ClaudeIsAt(new Native.POINT { X = seen.Right - 1, Y = mid })) continue;
            NameShape shape;
            bool has = shapes.TryGetValue(row.Name, out shape);
            bool known = has && shape.RowSize == row.Bounds.Size && shape.On(row.Bounds).Contains(seen);
            string name = row.Name;
            if (has && !known) {
                // Layout moved this name since it was read. Its old copy has to go before the screen can be read here, or that copy would be what's read.
                menu.BeginInvoke((Action)(() => { NameShape gone; shapes.TryRemove(name, out gone); if (overlay != null) overlay.WipeNames(); }));
                continue;
            }
            Color[] above = NameShape.ReadLine(beside, known ? shape.On(row.Bounds) : seen);
            if (!known) {
                shape = NameShape.Measure(NameShape.Read(seen), above);
                if (shape == null) continue;
                shape.Offset = new Point(seen.X - row.Bounds.X, seen.Y - row.Bounds.Y);
                shape.RowSize = row.Bounds.Size;
            } else if (above.SequenceEqual(shape.Above)) continue;
            NameShape found = shape;
            menu.BeginInvoke((Action)(() => {
                found.Above = above;
                shapes[name] = found;
                RenderName(name);
            }));
        }
    }

    void RenderName(string name) {
        Tint tint;
        NameShape shape;
        if (fontColors.TryGetValue(name, out tint) && shapes.TryGetValue(name, out shape)) shape.Render(tint);
        if (overlay == null) return;
        overlay.RedrawColumn();
        overlay.WipeNames();
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

    // Clicking the row whose list is open closes it. Clicking the other one swaps to its list.
    void ToggleColorList(int item) {
        bool wasOpen = (menu.Visible || picker.Visible) && listFor == item;
        menu.Hide();
        picker.Hide();
        panel.Expanded = -1;
        if (wasOpen) return;
        listFor = item;
        bool font = item == ColorPanel.FontRow;
        IDictionary<string, Tint> chosen = font ? (IDictionary<string, Tint>)fontColors : colors;
        Row row = menuRow;
        Tint tint;
        Tint? current = chosen.TryGetValue(row.Name, out tint) ? tint : (Tint?)null;
        var at = new Point(panel.Right + 4, panel.TopOf(item));
        Action<Tint?> choose = picked => {
            if (picked.HasValue) chosen[row.Name] = picked.Value;
            else chosen.Remove(row.Name);
            ColorFile.Save(font ? ColorFile.FontColors : ColorFile.Highlights, chosen);
            closeAppMenu = true;
            panel.Hide();
            menu.Hide();
            picker.Hide();
            RenderName(row.Name); // The strips keep their hole until the watcher sees the app's menu gone, so they never cross it.
        };
        panel.Expanded = item;
        menu.Open(current, at, choose, gradient => {
            menu.Hide();
            picker.Open(current, gradient, at, font ? 1 : Overlay.Strength, picked => choose(picked));
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
        tracker.Stop();
        if (overlay != null && shown != null && shown.Rows.Count > 0) overlay.MoveColumn(ShiftOf(shown));
    }

    void Apply() {
        Snapshot snapshot = latest;
        // Just after a click rows may have moved, so automation's positions are wanted. The tracker checks them against the pixels before using them, as they can be out of date.
        if (tracker.Capturing && tracker.WatchingLayout) {
            if (snapshot.Rows.Count == 0) tracker.Stop(); // A menu opened, which hides the page from automation and mustn't have colors over it.
            else if (snapshot != shown && snapshot != offered && snapshot.Window == owner) {
                offered = snapshot;
                Scene scene = SceneOf(snapshot.Rows, ShiftOf(snapshot));
                scene.From = snapshot;
                tracker.Offer(scene);
            }
            return;
        }
        if (tracker.Active || Moving) return; // The pixels and FollowDrag are more current than automation's positions until things stop.
        if (snapshot == shown && tracker.Offset == 0) return;
        Snapshot previous = shown;
        shown = snapshot;
        tracker.Offset = 0;

        if (snapshot.Window != owner) {
            if (overlay != null) overlay.Dispose();
            overlay = null;
            owner = snapshot.Window;
            if (owner != IntPtr.Zero) {
                overlay = new Overlay(colors, fontColors, shapes);
                Native.SetWindowLongPtr(overlay.Handle, GWLP_HWNDPARENT, owner);
                Native.SetWindowLongPtr(overlay.Names.Handle, GWLP_HWNDPARENT, owner);
            }
        }
        if (overlay == null) return;
        if (snapshot.Rows.Count == 0) { overlay.Hide(); return; }
        Place(snapshot);
        tracker.Prepare(snapshot.Area.Location);

        // Catches scrolling the wheel hook can't see, like dragging the scrollbar. Foreground only here, because no click or wheel turn says the user is in the app.
        // The wheel and click paths check only the cursor point, so a window overlapping part of the sidebar is read along with it, for those moments, and only redrawn in place.
        if (Native.GetForegroundWindow() == owner && snapshot.IsScrollOf(previous)) FollowScroll();
    }

    void Place(Snapshot snapshot) {
        overlay.RedrawColumn(); // Where the strips were.
        overlay.Rows = snapshot.Rows;
        overlay.Column = snapshot.Area;
        overlay.Shift = ShiftOf(snapshot);
        overlay.ScrollOffset = 0;
        overlay.FitToScreen();
        overlay.Show();
        overlay.RedrawColumn();
    }

    void FollowScroll() { Follow(false); }
    void FollowClick() { Follow(true); }

    // On the main thread, which owns all that's read here. Nothing to follow if nothing's colored, or while a menu is open or the window is moving and positions are out of date.
    void Follow(bool clicked) {
        if (overlay == null || !overlay.Visible || (colors.Count == 0 && fontColors.Count == 0) || menuWatching || Moving || overlay.Hole != Rectangle.Empty) return;
        tracker.Start(() => SceneOf(overlay.Rows, overlay.Shift), clicked);
    }

    Scene SceneOf(IEnumerable<Row> rows, Point shift) {
        return new Scene(rows, shift, overlay.PartOnScreen, overlay.Location, overlay.Handle, overlay.Names, overlay.Names.Handle, new Dictionary<string, Tint>(colors), fontColors, places, shapes);
    }

    // The strips go in place under the picture first. Then the names' window replaces the picture in one go, with pixels that match it, and it's back to the two windows.
    void EndPicture() {
        if (overlay == null || tracker.Capturing) return;
        overlay.ScrollOffset = tracker.Offset;
        overlay.Invalidate();
        overlay.Update();
        overlay.Names.Mirroring = false;
        overlay.WipeNames();
        overlay.Names.Update();
        ScrollTracker.Exclude(overlay.Handle, overlay.Names.Handle, false);
    }

    // Owning a window doesn't lift it above its owner until the owner is next activated, so place it directly. The names go above the strips.
    void KeepAboveOwner() {
        if (overlay == null || !overlay.IsHandleCreated || !overlay.Visible || !Native.IsWindow(owner)) return;
        PlaceAbove(owner, overlay.Handle);
        if (overlay.Names.Visible) PlaceAbove(overlay.Handle, overlay.Names.Handle);
    }

    static void PlaceAbove(IntPtr below, IntPtr window) {
        const uint GW_HWNDPREV = 3, SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
        IntPtr above = Native.GetWindow(below, GW_HWNDPREV);
        if (above != window) Native.SetWindowPos(window, above, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    // Called by Windows for every mouse event on the PC, so it returns at once for everything but button presses and the wheel.
    IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam) {
        int message = (int)wParam;
        if (code < 0 || (message != WM_LBUTTONDOWN && message != WM_LBUTTONUP && message != WM_RBUTTONDOWN && message != WM_MBUTTONDOWN && message != WM_MOUSEWHEEL))
            return Native.CallNextHookEx(hook, code, wParam, lParam);

        var info = (Native.MouseInfo)Marshal.PtrToStructure(lParam, typeof(Native.MouseInfo));
        var point = new Point(info.Point.X, info.Point.Y);
        bool inList = shown != null && shown.Area.Contains(point) && ClaudeIsAt(info.Point);

        if (message == WM_MOUSEWHEEL) {
            // Starting here, before the app has moved anything, means the very first frame of the scroll is followed.
            if (inList) FollowScroll();
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }
        if (message == WM_LBUTTONUP) { // The app acts on a click at the release, however long the button was held.
            if (inList) FollowClick();
            return Native.CallNextHookEx(hook, code, wParam, lParam);
        }

        // A click elsewhere could start a window drag, and other buttons can open menus, which the picture must not sit over.
        if (message == WM_LBUTTONDOWN && inList) FollowClick();
        else tracker.Stop();
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
        // While moving, the saved row positions are out of date, so leave the click to the app. A scroll is exact, so its distance can be allowed for.
        if (shown == null || owner == IntPtr.Zero || Moving) return null;
        Row row = shown.RowAt(point, tracker.Offset);
        return row != null && ClaudeIsAt(raw) ? row : null;
    }

    // True only when the Claude window is what's actually on screen at that spot, not another app covering it.
    bool ClaudeIsAt(Native.POINT raw) {
        IntPtr root = Native.GetAncestor(Native.WindowFromPoint(raw), GA_ROOT);
        return root == owner || (overlay != null && overlay.IsHandleCreated && (root == overlay.Handle || root == overlay.Names.Handle));
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

    // A row with a highlight and a font color, in a frame of plain background holding one solid letter pixel and one half-covered one.
    static unsafe void CheckBaking() {
        const int W = 40, H = 40, Background = 0x202020, Solid = 0xE0E0E0, Half = 0x808080;
        Color red = Color.FromArgb(0xFF, 0, 0), blue = Color.FromArgb(0, 0, 0xFF);
        var row = new Row { Name = "x", Bounds = new Rectangle(102, 210, 30, 20) };
        var shapes = new Dictionary<string, NameShape>();
        var scene = new Scene(new[] { row }, Point.Empty, new Rectangle(100, 200, W, H), Point.Empty, IntPtr.Zero, null, IntPtr.Zero,
            new Dictionary<string, Tint> { { "x", new Tint(red) } }, new Dictionary<string, Tint> { { "x", new Tint(blue) } },
            new Dictionary<string, NamePlace> { { "x", new NamePlace { Box = new Rectangle(4, 6, 10, 6), RowSize = row.Bounds.Size } } }, shapes);
        scene.Prepare();
        int tinted = Scene.WithStrip(Background, red.ToArgb());
        Expect(Scene.WithStrip(31 << 16 | 158 << 8 | 31, 197 << 16 | 249 << 8 | 34) == (81 << 16 | 185 << 8 | 32), "the strip blends as measured on the real screen");
        foreach (int offset in new[] { 0, 5, -16 }) { // At rest, scrolled down, and with the name cut off by the list's top edge so only the line under it is left to read the background from.
            int[] raw = Enumerable.Repeat(Background, W * H).ToArray(), baked = new int[W * H];
            int letterY = 17 + offset;
            raw[letterY * W + 8] = Solid;
            raw[letterY * W + 9] = Half;
            fixed (int* from = raw, to = baked) {
                scene.Bake(from, to, offset);
                Expect((baked[letterY * W + 8] & 0xFFFFFF) == 0x0000FF, "a solid letter pixel takes the font color, at offset " + offset);
                Color halfway = MenuWindow.Mix(Color.FromArgb(tinted | unchecked((int)0xFF000000)), blue, 0.5);
                Expect((baked[letterY * W + 9] & 0xFFFFFF) == (halfway.ToArgb() & 0xFFFFFF), "a half-covered edge lands halfway to the strip's background, at offset " + offset);
                Expect((baked[letterY * W + 12] & 0xFFFFFF) == tinted, "the name's empty pixels just show the strip, at offset " + offset);
                Expect(baked[(H - 1) * W + 39] == Background, "outside the row the app's pixels are untouched, at offset " + offset);
                if (offset >= 0) Expect(baked[(10 + offset) * W + 2] == Background, "a strip's rounded corner leaves the app's pixel, at offset " + offset);
                if (offset != 0) continue;
                // What the names' window then draws at rest must be the same pixels.
                scene.Settle(from, to, offset);
                NameShape shape = shapes["x"];
                Expect(shape.Offset == new Point(4, 6) && (shape.ColorAt(2, 1, new Tint(blue)).ToArgb() & 0xFFFFFF) == 0x0000FF, "the name's shape comes from the last frame");
                for (int y = 0; y < 6; y++)
                    for (int x = 0; x < 10; x++) {
                        Color rest = shape.ColorAt(x, y, new Tint(blue));
                        int expected = rest.ToArgb() == ClickThroughWindow.Clear.ToArgb() ? tinted : rest.ToArgb() & 0xFFFFFF;
                        Expect((baked[(16 + y) * W + 6 + x] & 0xFFFFFF) == expected, "the picture and the names' window agree on every pixel");
                    }
            }
        }

        // Is a colored row where its position says? The row is 6 lines into the hashes, which start EdgeInset below the frame's top.
        Point away = new Point(0, 0), onRow = new Point(110, 220);
        int count = H - 2 * ScrollTracker.EdgeInset;
        int[] lines = new int[count], moved = new int[count];
        var hasText = new bool[count];
        for (int y = 9; y < 15; y++) { lines[y] = 100 + y; moved[y + 5] = 100 + y; hasText[y] = true; }
        var looks = new Dictionary<string, int[]>();
        scene.Remember(looks, lines, hasText, 0, away);
        Expect(scene.Fits(looks, lines, 0, away), "a row that hasn't moved fits");
        Expect(!scene.Fits(looks, moved, 0, away), "a row that moved doesn't fit its old position, as stale automation would have it");
        Expect(scene.Fits(looks, moved, 5, away), "a row fits at the distance it really moved");
        Expect(scene.Fits(looks, moved, 0, onRow), "the row under the mouse changes by itself, so it isn't judged");
        var hovered = new Dictionary<string, int[]>();
        scene.Remember(hovered, lines, hasText, 0, onRow);
        Expect(hovered.Count == 0, "a lit-up row's look isn't kept");
        Expect(!scene.MoreBelow(0, count) && !scene.MoreBelow(-30, count) && scene.MoreBelow(40, count), "a colored row out of view below is noticed, and one above isn't");
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
        Expect(sample.RowAt(new Point(10, 110), 0) != null && sample.RowAt(new Point(10, 140), 0) == null, "right-click hit test");
        Expect(sample.RowAt(new Point(10, 20), 0) == null, "a row scrolled out of the list can't be right-clicked");
        Expect(sample.RowAt(new Point(10, 140), 30) != null && sample.RowAt(new Point(10, 110), 30) == null, "right-clicking mid-scroll allows for the distance scrolled");
        Expect(Sidebar.IsRow(new Rectangle(8, 100, 358, 32), sample.Area) && !Sidebar.IsRow(sample.Area, sample.Area), "a heading row is kept, and a parent as tall as the whole list isn't");

        var scrolled = new Snapshot { Area = sample.Area };
        scrolled.Rows.Add(new Row { Name = "x", Bounds = new Rectangle(8, 60, 358, 32) });
        scrolled.Rows.Add(new Row { Name = "scrolled away", Bounds = new Rectangle(8, -30, 358, 32) });
        Expect(scrolled.IsScrollOf(sample), "every row moving the same distance is a scroll");
        scrolled.Rows[1].Bounds.Y = 10;
        Expect(!scrolled.IsScrollOf(sample), "only some rows moving is a layout change, not a scroll");

        // Scroll tracking: every line unique, so only the true shift can line them all up.
        int[] before = Enumerable.Range(0, 400).Select(y => y * 37 + 1).ToArray();
        bool[] text = Enumerable.Repeat(true, 400).ToArray();
        int shift, stray;
        Expect(ScrollTracker.FindShift(before, before, text, out shift, out stray) && shift == 0 && stray == 0, "an unchanged frame has no shift");
        int[] after = Enumerable.Range(0, 400).Select(y => y >= 40 ? before[y - 40] : -y - 1).ToArray();
        Expect(ScrollTracker.FindShift(before, after, text, out shift, out stray) && shift == 40, "content moved down 40 lines");
        for (int y = 200; y < 232; y++) after[y] = -y - 1000;
        Expect(ScrollTracker.FindShift(before, after, text, out shift, out stray) && shift == 40 && stray >= 32, "a hovered row changing color doesn't throw it off, and shows up as lines that don't fit");
        int[] unrelated = Enumerable.Range(0, 400).Select(y => -y - 1).ToArray();
        Expect(!ScrollTracker.FindShift(before, unrelated, text, out shift, out stray), "a frame that matches nothing is reported as unsure");
        bool[] blank = new bool[400];
        Expect(!ScrollTracker.FindShift(before, after, blank, out shift, out stray), "blank lines alone prove nothing");

        // Recoloring a name, over a background that changes by column as a gradient strip's does. Column 0 has no letter in it, column 1 is all letter, column 2 half.
        Color[] above = { Color.FromArgb(0x20, 0x40, 0x60), Color.FromArgb(0x50, 0x10, 0x10), Color.FromArgb(0x30, 0x30, 0x30) };
        NameShape shape = NameShape.Measure(new[,] { { above[0] }, { Color.FromArgb(0xB0, 0x70, 0x70) }, { Color.FromArgb(0x60, 0x60, 0x60) } }, above);
        var red = new Tint(Color.Red);
        Expect(shape != null && shape.ColorAt(0, 0, red).ToArgb() == ClickThroughWindow.Clear.ToArgb(), "a pixel with no letter in it stays see-through");
        Expect(shape.ColorAt(1, 0, red).ToArgb() == Color.Red.ToArgb(), "a pixel fully inside a letter takes the color");
        Expect(shape.ColorAt(2, 0, red).ToArgb() == MenuWindow.Mix(above[2], Color.Red, 0.5).ToArgb(), "a half-covered edge lands halfway to the background");
        Expect(NameShape.Measure(new[,] { { above[0] }, { above[1] }, { above[2] } }, above) == null, "pixels with no letters give no shape");
        NameShape edge = NameShape.Measure(new[,] { { Color.FromArgb(0x60, 0x60, 0x60) }, { Color.FromArgb(0x60, 0, 0) } }, new[] { Color.Black, Color.Black });
        Expect(edge.ColorAt(1, 0, new Tint(Color.White)).ToArgb() == Color.FromArgb(0xFF, 0, 0).ToArgb(), "a subpixel edge only changes the color channels the letter covers");
        NameShape dark = NameShape.Measure(new[,] { { Color.FromArgb(0x20, 0x20, 0x20) }, { Color.White } }, new[] { Color.White, Color.White });
        Expect(dark != null && dark.ColorAt(0, 0, red).ToArgb() == Color.Red.ToArgb() && dark.ColorAt(1, 0, red).ToArgb() == ClickThroughWindow.Clear.ToArgb(), "dark letters on a light background work too");
        NameShape wide = NameShape.Measure(new[,] { { Color.White }, { Color.White }, { Color.White } }, new[] { Color.Black, Color.Black, Color.Black });
        var sweep = new Tint(Color.Red, Color.Blue);
        Expect(wide.ColorAt(0, 0, sweep).ToArgb() == Color.Red.ToArgb() && wide.ColorAt(2, 0, sweep).ToArgb() == Color.Blue.ToArgb(), "a gradient runs from the name's first column to its last");
        Expect(wide.ColorAt(1, 0, new Tint(ClickThroughWindow.Clear)).ToArgb() != ClickThroughWindow.Clear.ToArgb(), "a letter can't land on the see-through color and vanish");

        CheckBaking();

        // Measured from the app's real menu at (210,235), 181 wide: its first item starts 6 below its top, so the rows' bottom edge must land 14 below, past the rounded corner.
        Expect(ColorPanel.BoundsFor(new Rectangle(210, 235, 181, 301)) == new Rectangle(210, 170, 181, 79), "the color rows sit on the menu's top edge");
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        Expect(ColorPanel.BoundsFor(new Rectangle(work.X + 210, work.Y + 20, 181, 301)).Y == work.Y, "at the top of the screen the color rows stay on it");
        Expect(MenuWindow.Mix(Color.FromArgb(0x30, 0x30, 0x30), Color.White, 0.075).R == 0x40, "hover color matches the stylesheet's 7.5% white");
        Console.WriteLine("logic: ok");

        // The first query can come back nearly empty: it's what switches the app's accessibility tree on.
        for (int attempt = 0; attempt < 10; attempt++) {
            try {
                IntPtr window = Sidebar.FindWindow();
                AutomationElement list = window == IntPtr.Zero ? null : Sidebar.FindList(window);
                if (list != null) {
                    Snapshot snapshot = Sidebar.Read(window, list), again = Sidebar.Reread(window, list, snapshot);
                    Console.WriteLine("list area: " + snapshot.Area);
                    foreach (Row row in snapshot.Rows) Console.WriteLine(row.Name + " | " + row.Bounds);
                    Expect(again.Rows.Select(r => r.Name).SequenceEqual(snapshot.Rows.Select(r => r.Name)), "rows found once can be read again without a search");
                    return snapshot.Rows.Count > 0 ? 0 : 1;
                }
            } catch (Exception) { } // Measured: reads fail now and then while the app's layout changes, and the next one works.
            Thread.Sleep(1000);
        }
        Console.WriteLine("no group headings found: is the Code tab showing, with the sidebar open?");
        return 1;
    }
}
