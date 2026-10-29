using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

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

        private readonly Action<int>? _onOffset;
        private readonly Func<int>? _getOffset;
        private readonly Border? _bar;
        private readonly TextBlock? _offsetLabel;
        private Point _lastMouse = new Point(-100, -100);
        private readonly DispatcherTimer _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };

        /// <param name="onOffset">Cambia la sincronía en ms (0 = restablecer). Null en la superposición transparente.</param>
        /// <param name="showControls">Muestra la barra de sincronía al mover el ratón (pantalla de la letra).</param>
        public SceneWindow(Scene scene, MonitorInfo monitor, bool overlay, Action<int>? onOffset = null,
            Func<int>? getOffset = null, bool showControls = false)
        {
            _onOffset = onOffset;
            _getOffset = getOffset;
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
            var root = new Grid();
            root.Children.Add(View);
            if (!overlay && showControls && onOffset != null)
            {
                _offsetLabel = new TextBlock
                {
                    Foreground = Brushes.White, FontSize = 16, VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(14, 0, 14, 0), MinWidth = 150, TextAlignment = TextAlignment.Center,
                };
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(MakeButton("Retrasar 0,5 s", -500));
                row.Children.Add(MakeButton("−0,1 s", -100));
                row.Children.Add(_offsetLabel);
                row.Children.Add(MakeButton("+0,1 s", 100));
                row.Children.Add(MakeButton("Adelantar 0,5 s", 500));
                row.Children.Add(MakeButton("Restablecer", 0));
                _bar = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(200, 12, 12, 18)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(14), Padding = new Thickness(10),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 90), Child = row, Visibility = Visibility.Collapsed,
                };
                root.Children.Add(_bar);
                UpdateLabel();
            }
            Content = root;
            if (!overlay)
            {
                Cursor = Cursors.None;
                _hideTimer.Tick += (_, _) =>
                {
                    if (_bar != null && _bar.IsMouseOver) return;
                    _hideTimer.Stop();
                    Cursor = Cursors.None;
                    if (_bar != null) _bar.Visibility = Visibility.Collapsed;
                };
                MouseMove += (_, e) =>
                {
                    // WPF también lanza MouseMove sin que se mueva el ratón: solo cuenta un movimiento real.
                    var p = e.GetPosition(this);
                    if (Math.Abs(p.X - _lastMouse.X) + Math.Abs(p.Y - _lastMouse.Y) < 3) return;
                    _lastMouse = p;
                    ShowControls();
                };
                MouseWheel += (_, e) => Offset(e.Delta > 0 ? 50 : -50);
                Closed += (_, _) => _hideTimer.Stop();
            }

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | (overlay ? Native.WS_EX_TRANSPARENT | Native.WS_EX_NOACTIVATE : 0));
                Place();
            };
            Loaded += (_, _) => Place();
            DpiChanged += (_, _) => Place();
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape) EscapePressed?.Invoke();
                else if (e.Key == Key.Right) Offset(100);
                else if (e.Key == Key.Left) Offset(-100);
                else if (e.Key == Key.Back || e.Key == Key.D0 || e.Key == Key.NumPad0) Offset(0);
            };
        }

        private Button MakeButton(string text, int delta)
        {
            var button = new Button
            {
                Content = text, FontSize = 15, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(4, 0, 4, 0),
                Foreground = Brushes.White, Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Focusable = false,
            };
            button.Click += (_, _) => Offset(delta);
            return button;
        }

        private void Offset(int delta)
        {
            if (_onOffset == null) return;
            _onOffset(delta);
            UpdateLabel();
            ShowControls();
        }

        private void UpdateLabel()
        {
            if (_offsetLabel == null || _getOffset == null) return;
            int ms = _getOffset();
            string sign = ms > 0 ? "+" : ms < 0 ? "−" : "";
            _offsetLabel.Text = ms == 0 ? "Sincronía: 0 ms" : $"Sincronía: {sign}{Math.Abs(ms)} ms";
        }

        private void ShowControls()
        {
            Cursor = Cursors.Arrow;
            if (_bar != null) { UpdateLabel(); _bar.Visibility = Visibility.Visible; }
            _hideTimer.Stop();
            _hideTimer.Start();
        }

        private void Place()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero) Native.PlaceTopmost(hwnd, _monitor.X, _monitor.Y, _monitor.Width, _monitor.Height, !_overlay);
        }
    }
}
