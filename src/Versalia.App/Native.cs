using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Versalia.App
{
    /// <summary>Un monitor en píxeles físicos del escritorio virtual.</summary>
    internal sealed class MonitorInfo
    {
        public int Index;
        public int X, Y, Width, Height;
        public double Dpi = 96;
        public bool IsPrimary;
        public string Name = "";
        public double Right => X + Width;
        public double Bottom => Y + Height;
        public long Area => (long)Width * Height;
        public System.Windows.Rect Rect => new System.Windows.Rect(X, Y, Width, Height);
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);
        [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int type, out uint x, out uint y);

        public static List<MonitorInfo> GetMonitors()
        {
            var list = new List<MonitorInfo>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, IntPtr rect, IntPtr data) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (!GetMonitorInfo(h, ref info)) return true;
                double dpi = 96;
                try { if (GetDpiForMonitor(h, 0, out uint dx, out _) == 0 && dx > 0) dpi = dx; } catch { }
                list.Add(new MonitorInfo
                {
                    X = info.rcMonitor.Left, Y = info.rcMonitor.Top,
                    Width = info.rcMonitor.Right - info.rcMonitor.Left,
                    Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                    Dpi = dpi, IsPrimary = (info.dwFlags & 1) != 0, Name = info.szDevice,
                });
                return true;
            }, IntPtr.Zero);
            // De izquierda a derecha, para que «la pantalla de la izquierda» sea siempre la primera.
            list.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            for (int i = 0; i < list.Count; i++) list[i].Index = i;
            return list;
        }

        private const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x20;
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);

        public static void AddExStyle(IntPtr hwnd, int flags) =>
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | flags);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public static void PlaceTopmost(IntPtr hwnd, int x, int y, int w, int h, bool activate)
        {
            const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
            SetWindowPos(hwnd, new IntPtr(-1), x, y, w, h, SWP_SHOWWINDOW | (activate ? 0u : SWP_NOACTIVATE));
        }

        public const int WM_HOTKEY = 0x0312;
        public const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_NOREPEAT = 0x4000;
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
