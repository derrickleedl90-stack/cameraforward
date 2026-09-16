using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Drawing;
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
                bool initialized = false;
                try
                {
                    initialized = Native.MagInitialize();
                    if (!initialized) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows magnification could not start.");
                    using (var home = new Home()) Application.Run(home);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Screen Zoom", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { if (initialized) Native.MagUninitialize(); }
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
                if (!Native.RegisterHotKey(Handle, index + 1, 0x4006, (uint)keys[index]))
                {
                    for (int id = 1; id <= registeredHotkeys; id++) Native.UnregisterHotKey(Handle, id);
                    registeredHotkeys = 0;
                    throw new InvalidOperationException("Ctrl + Shift + " + keys[index] + " is already in use. Screen Zoom could not start.");
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
            try
            {
                Rectangle bounds = SystemInformation.VirtualScreen;
                Point anchor = Cursor.Position;
                view = new ZoomView(bounds, anchor);
                view.FormClosed += delegate { view = null; };
                if (initialZoomSteps != 0) view.Zoom(initialZoomSteps);
                view.Show();
            }
            catch (Exception ex)
            {
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
        private MagnifierSurface magnifier;
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

        public ZoomView(Rectangle desktop, Point pointer)
        {
            this.desktop = desktop;
            anchor = new Point(pointer.X - desktop.X, pointer.Y - desktop.Y);
            keyboardProc = Keyboard;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = desktop;
            TopMost = true;
            ShowInTaskbar = false;
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

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= 0x80000; // WS_EX_LAYERED, required by the magnifier.
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!Native.SetLayeredWindowAttributes(Handle, 0, 255, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the magnifier host.");
            magnifier = new MagnifierSurface(Handle, ClientSize, AdjustWheel);
            // This filter applies ONLY to the magnifier's source. The resulting
            // window remains available to Jump Desktop and other capture tools.
            if (!Native.MagSetWindowFilterList(magnifier.Handle, 0, 1, new IntPtr[] { Handle }))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure the magnifier source.");
            UpdateMagnifier();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (magnifier != null) { magnifier.Dispose(); magnifier = null; }
            base.OnHandleDestroyed(e);
        }

        private void UpdateMagnifier()
        {
            if (magnifier == null || closing) return;
            float zoom = zoomPercent / 100f;
            int width = (int)Math.Ceiling(desktop.Width / (double)zoom);
            int height = (int)Math.Ceiling(desktop.Height / (double)zoom);
            int x = Math.Max(0, Math.Min(desktop.Width - width, (int)Math.Round(anchor.X * (1.0 - 1.0 / zoom))));
            int y = Math.Max(0, Math.Min(desktop.Height - height, (int)Math.Round(anchor.Y * (1.0 - 1.0 / zoom))));
            var source = new Native.Rect { Left = desktop.Left + x, Top = desktop.Top + y,
                Right = desktop.Left + x + width, Bottom = desktop.Top + y + height };
            var transform = new Native.MagTransform { M00 = zoom, M11 = zoom, M22 = 1f };
            if (!Native.MagSetWindowTransform(magnifier.Handle, ref transform) ||
                !Native.MagSetWindowSource(magnifier.Handle, source))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not refresh the live magnifier.");
            Native.InvalidateRect(magnifier.Handle, IntPtr.Zero, false);
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
                UpdateMagnifier();
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
            bool shift = pressedKeys.Contains((int)Keys.LShiftKey) || pressedKeys.Contains((int)Keys.RShiftKey) || pressedKeys.Contains((int)Keys.ShiftKey);
            // Queue work and return immediately: a slow low-level hook can be
            // silently removed by Windows. Never paint or capture in this hook.
            if (keyDown)
            {
                if (key == (int)Keys.Escape) BeginInvoke((Action)Unlock);
                else if (control && shift && key == (int)Keys.Up)
                    BeginInvoke((Action)delegate { Zoom(1); });
                else if (control && shift && key == (int)Keys.Down)
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
            // The frame timer applies the new zoom and handles native failures.
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            AdjustWheel(e.Delta);
        }

        private void AdjustWheel(int delta)
        {
            wheelRemainder += delta;
            while (Math.Abs(wheelRemainder) >= 120)
            {
                int direction = Math.Sign(wheelRemainder);
                wheelRemainder -= direction * 120;
                Zoom(direction);
            }
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
            if (disposing) { frameTimer.Dispose(); safetyTimer.Dispose(); }
            base.Dispose(disposing);
        }
    }

    // Subclass the native child so mouse-wheel input is handled exactly once
    // whether Windows delivers it to the host or directly to this child.
    internal sealed class MagnifierSurface : NativeWindow, IDisposable
    {
        private readonly Action<int> wheel;
        public MagnifierSurface(IntPtr parent, Size size, Action<int> wheel)
        {
            this.wheel = wheel;
            IntPtr window = Native.CreateWindowEx(0, "Magnifier", "", 0x50000000,
                0, 0, size.Width, size.Height, parent, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
            if (window == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the Windows magnifier control.");
            AssignHandle(window);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x020A) // WM_MOUSEWHEEL
            {
                wheel(unchecked((short)((m.WParam.ToInt64() >> 16) & 0xffff)));
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            IntPtr window = Handle;
            if (window == IntPtr.Zero) return;
            ReleaseHandle();
            Native.DestroyWindow(window);
        }
    }

    internal static class Native
    {
        internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct MagTransform
        {
            internal float M00, M01, M02, M10, M11, M12, M20, M21, M22;
        }
        [DllImport("Magnification.dll", SetLastError = true)] internal static extern bool MagInitialize();
        [DllImport("Magnification.dll")] internal static extern bool MagUninitialize();
        [DllImport("Magnification.dll", SetLastError = true)] internal static extern bool MagSetWindowTransform(IntPtr window, ref MagTransform transform);
        [DllImport("Magnification.dll", SetLastError = true)] internal static extern bool MagSetWindowSource(IntPtr window, Rect source);
        [DllImport("Magnification.dll", SetLastError = true)] internal static extern bool MagSetWindowFilterList(IntPtr window, uint mode, int count, [In] IntPtr[] windows);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
        [DllImport("user32.dll")] internal static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool InvalidateRect(IntPtr window, IntPtr rect, bool erase);
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
