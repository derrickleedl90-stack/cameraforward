using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace ScreenZoom
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using (var instance = new Mutex(true, "Local\\ScreenZoom.Desktop", out created))
            {
                if (!created) return;
                try { Application.Run(new Home()); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Screen Zoom", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }
    }

    internal sealed class Home : Form
    {
        private readonly System.Windows.Forms.Timer startTimer = new System.Windows.Forms.Timer { Interval = 100 };
        private ZoomView view;
        private DateTime readyAt;
        private int registeredHotkeys;
        private int initialZoomSteps;

        public Home()
        {
            ShowInTaskbar = false;
            startTimer.Tick += StartWhenReady;
        }

        protected override void SetVisibleCore(bool value)
        {
            // Keep a native message window for global shortcuts, without ever
            // displaying a home window, taskbar button, or tray icon.
            if (!IsHandleCreated) CreateHandle();
            base.SetVisibleCore(false);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Keys[] keys = { Keys.Z, Keys.Q, Keys.Up, Keys.Down };
            for (int index = 0; index < keys.Length; index++)
            {
                if (!Native.RegisterHotKey(Handle, index + 1, 0x4003, (uint)keys[index]))
                {
                    for (int id = 1; id <= registeredHotkeys; id++) Native.UnregisterHotKey(Handle, id);
                    registeredHotkeys = 0;
                    throw new InvalidOperationException("Ctrl + Alt + " + keys[index] + " is already in use. Screen Zoom could not start.");
                }
                registeredHotkeys++;
            }
        }

        private void Schedule(int seconds, int zoomSteps = 0)
        {
            if (view != null || startTimer.Enabled) return;
            initialZoomSteps = zoomSteps;
            Hide();
            readyAt = DateTime.UtcNow.AddSeconds(seconds);
            startTimer.Start();
        }

        private void StartWhenReady(object sender, EventArgs e)
        {
            if (DateTime.UtcNow < readyAt) return;
            // Wait for the initiating chord/button to be released so no modifier
            // key-up is swallowed after its key-down reached another application.
            for (int key = 1; key < 256; key++)
                if ((Native.GetAsyncKeyState(key) & 0x8000) != 0) return;
            startTimer.Stop();
            Bitmap capture = null;
            try
            {
                Rectangle bounds = SystemInformation.VirtualScreen;
                Point anchor = Cursor.Position;
                capture = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                using (Graphics g = Graphics.FromImage(capture))
                    g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
                view = new ZoomView(capture, bounds, anchor);
                capture = null; // The view owns the bitmap from here on.
                view.FormClosed += delegate { view = null; };
                if (initialZoomSteps != 0) view.Zoom(initialZoomSteps);
                view.Show();
            }
            catch (Exception ex)
            {
                if (capture != null) capture.Dispose();
                if (view != null) { view.Dispose(); view = null; }
                MessageBox.Show(this, "Could not start zoom.\r\n" + ex.Message, "Screen Zoom", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 1) { Schedule(1); return; }
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 2) { Close(); return; }
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 3)
            {
                if (view != null) view.Zoom(1); else Schedule(1, 1);
                return;
            }
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == 4)
            {
                if (view != null) view.Zoom(-1);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                startTimer.Dispose();
                if (view != null) view.Dispose();
                for (int id = 1; id <= registeredHotkeys; id++) Native.UnregisterHotKey(Handle, id);
                registeredHotkeys = 0;
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class ZoomView : Form
    {
        private readonly Bitmap capture;
        private readonly Point anchor;
        private readonly Rectangle desktop;
        private readonly Native.HookProc keyboardProc;
        private readonly System.Windows.Forms.Timer safetyTimer = new System.Windows.Forms.Timer { Interval = 250 };
        private readonly System.Windows.Forms.Timer frameTimer = new System.Windows.Forms.Timer { Interval = 33 };
        private IntPtr keyboardHook;
        private int zoomPercent = 100;
        private int wheelRemainder;
        private readonly HashSet<int> pressedKeys = new HashSet<int>();
        private bool closing;

        public ZoomView(Bitmap capture, Rectangle desktop, Point pointer)
        {
            this.capture = capture;
            this.desktop = desktop;
            anchor = new Point(pointer.X - desktop.X, pointer.Y - desktop.Y);
            keyboardProc = Keyboard;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = desktop;
            TopMost = true;
            ShowInTaskbar = false;
            DoubleBuffered = true;
            BackColor = Color.Black;
            frameTimer.Tick += RefreshDesktop;
            safetyTimer.Tick += delegate
            {
                // Fail open if Windows switches desktops, another application
                // takes focus, or the monitor arrangement changes.
                if (!SystemInformation.VirtualScreen.Equals(desktop) || Native.GetForegroundWindow() != Handle)
                    Unlock();
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Exclude our magnified window from capture so each frame contains
            // the original desktop, never a recursively magnified previous frame.
            if (!Native.SetWindowDisplayAffinity(Handle, 0x11))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable live desktop capture.");
        }

        private void RefreshDesktop(object sender, EventArgs e)
        {
            if (closing) return;
            if (!SystemInformation.VirtualScreen.Equals(desktop) || Native.GetForegroundWindow() != Handle)
            {
                Unlock();
                return;
            }
            try
            {
                // Reuse the bitmap; capture and painting both run on the UI
                // thread, so frames cannot overwrite a bitmap being painted.
                using (Graphics g = Graphics.FromImage(capture))
                    g.CopyFromScreen(desktop.Location, Point.Empty, desktop.Size, CopyPixelOperation.SourceCopy);
                Invalidate();
            }
            catch (Exception ex)
            {
                // Release input instead of leaving a stale frame locked onscreen.
                Unlock();
                MessageBox.Show("Live capture stopped. The screen has been unlocked.\r\n" + ex.Message,
                    "Screen Zoom", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            keyboardHook = Native.SetWindowsHookEx(13, keyboardProc, Native.GetModuleHandle(null), 0);
            if (keyboardHook == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Unlock();
                MessageBox.Show("Could not lock keyboard input: " + new Win32Exception(error).Message, "Screen Zoom");
                return;
            }
            safetyTimer.Start();
            frameTimer.Start();
        }

        private IntPtr Keyboard(int code, IntPtr message, IntPtr data)
        {
            if (code < 0 || closing) return Native.CallNextHookEx(keyboardHook, code, message, data);
            int msg = message.ToInt32();
            int key = Marshal.ReadInt32(data);
            bool keyDown = msg == 0x100 || msg == 0x104;
            bool keyUp = msg == 0x101 || msg == 0x105;
            if (keyDown) pressedKeys.Add(key);
            if (keyUp) pressedKeys.Remove(key);
            // Track hook events directly: swallowed modifier keys need not be
            // reflected in Windows asynchronous key state. Capture begins with
            // all keys released, so this set starts in a known state.
            bool control = pressedKeys.Contains((int)Keys.LControlKey) || pressedKeys.Contains((int)Keys.RControlKey) || pressedKeys.Contains((int)Keys.ControlKey);
            bool alt = pressedKeys.Contains((int)Keys.LMenu) || pressedKeys.Contains((int)Keys.RMenu) || pressedKeys.Contains((int)Keys.Menu);
            // Queue work and return immediately: a slow low-level hook can be
            // silently removed by Windows. Never paint or capture in this hook.
            if (keyDown)
            {
                if (key == (int)Keys.Escape) BeginInvoke((Action)Unlock);
                else if (control && alt && key == (int)Keys.Up)
                    BeginInvoke((Action)delegate { Zoom(1); });
                else if (control && alt && key == (int)Keys.Down)
                    BeginInvoke((Action)delegate { Zoom(-1); });
                else if (key == (int)Keys.Oemplus || key == (int)Keys.Add)
                    BeginInvoke((Action)delegate { Zoom(1); });
                else if (key == (int)Keys.OemMinus || key == (int)Keys.Subtract)
                    BeginInvoke((Action)delegate { Zoom(-1); });
            }
            return new IntPtr(1);
        }

        internal void Zoom(int direction)
        {
            if (closing) return;
            zoomPercent = Math.Max(100, Math.Min(800, zoomPercent + direction * 3));
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            wheelRemainder += e.Delta;
            while (Math.Abs(wheelRemainder) >= 120)
            {
                int direction = Math.Sign(wheelRemainder);
                wheelRemainder -= direction * 120;
                Zoom(direction);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            float zoom = zoomPercent / 100f;
            float width = capture.Width / zoom;
            float height = capture.Height / zoom;
            // Keep the original pointer position at the same screen coordinate.
            // The current pointer position is deliberately never consulted.
            float x = Math.Max(0, Math.Min(capture.Width - width, anchor.X * (1f - 1f / zoom)));
            float y = Math.Max(0, Math.Min(capture.Height - height, anchor.Y * (1f - 1f / zoom)));
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.DrawImage(capture, ClientRectangle, new RectangleF(x, y, width, height), GraphicsUnit.Pixel);
        }

        private void Unlock()
        {
            if (closing) return;
            closing = true;
            ReleaseHook();
            Close();
        }

        private void ReleaseHook()
        {
            frameTimer.Stop();
            safetyTimer.Stop();
            if (keyboardHook != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(keyboardHook);
                keyboardHook = IntPtr.Zero;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x007E && IsHandleCreated) BeginInvoke((Action)Unlock); // WM_DISPLAYCHANGE
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            closing = true;
            ReleaseHook();
            if (disposing) { frameTimer.Dispose(); safetyTimer.Dispose(); capture.Dispose(); }
            base.Dispose(disposing);
        }
    }

    internal static class Native
    {
        internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] internal static extern IntPtr GetModuleHandle(string module);
        [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr SetWindowsHookEx(int kind, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    }
}
