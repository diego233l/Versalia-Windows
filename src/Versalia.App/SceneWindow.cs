using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Versalia.App
{
    /// <summary>Elemento que dibuja el trozo de escena que le toca a un monitor, en píxeles físicos.</summary>
    internal sealed class SceneView : FrameworkElement
    {
        private readonly Scene _scene;
        private readonly MonitorInfo _monitor;

        public SceneView(Scene scene, MonitorInfo monitor)
        {
            _scene = scene;
            _monitor = monitor;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            // La escena trabaja en píxeles del escritorio virtual; WPF dibuja en unidades independientes del DPI.
            dc.PushTransform(new ScaleTransform(1.0 / dpi.DpiScaleX, 1.0 / dpi.DpiScaleY));
            dc.PushTransform(new TranslateTransform(-_monitor.X, -_monitor.Y));
            _scene.Render(dc, _monitor);
            dc.Pop();
            dc.Pop();
        }
    }

    /// <summary>Ventana sin bordes que cubre un monitor. En modo superposición es transparente y deja pasar los clics.</summary>
    internal sealed class SceneWindow : Window
    {
        private readonly MonitorInfo _monitor;
        private readonly bool _overlay;
        public SceneView View { get; }
        public event Action? EscapePressed;

        public SceneWindow(Scene scene, MonitorInfo monitor, bool overlay)
        {
            _monitor = monitor;
            _overlay = overlay;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            AllowsTransparency = overlay;
            Background = overlay ? Brushes.Transparent : Brushes.Black;
            ShowActivated = !overlay;
            Width = 200; Height = 200;
            Title = "Versalia";
            View = new SceneView(scene, monitor);
            Content = View;
            if (!overlay) Cursor = Cursors.None;

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | (overlay ? Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE : 0));
                Place();
            };
            Loaded += (_, _) => Place();
            DpiChanged += (_, _) => Place();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) EscapePressed?.Invoke(); };
        }

        private void Place()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) Native.PlaceTopmost(hwnd, _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, !_overlay);
        }
    }
}
