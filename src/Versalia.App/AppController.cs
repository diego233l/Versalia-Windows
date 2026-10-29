using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Versalia.Core;
using Forms = System.Windows.Forms;

namespace Versalia.App
{
    /// <summary>Bandeja del sistema, atajos, ventanas por monitor y bucle de dibujo.</summary>
    internal sealed class AppController : IDisposable
    {
        private const int HotkeyStage = 1, HotkeyOverlay = 2;
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        private readonly Settings _settings = Settings.Load();
        private readonly Director _director;
        private readonly MediaSessionSource _source = new MediaSessionSource();
        private readonly Scene _scene = new Scene();
        private readonly List<SceneWindow> _windows = new List<SceneWindow>();
        private readonly Forms.NotifyIcon _tray;
        private readonly System.Windows.Window _hotkeyHost;
        private readonly DispatcherTimer _idleTimer;
        private readonly DispatcherTimer _displayDebounce;
        private bool _rendering;
        private int _frameCounter;
        private List<MonitorInfo> _monitors = new List<MonitorInfo>();

        public AppController()
        {
            _director = new Director(_settings);
            _source.Updated += (snapshot, art) =>
                System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() => _director.OnSnapshot(snapshot, art)));

            _tray = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "Versalia", Visible = true };
            _tray.ContextMenuStrip = new Forms.ContextMenuStrip();
            _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
            _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ToggleMode("stage"); };

            // Ventana invisible que recibe los atajos de teclado globales.
            _hotkeyHost = new System.Windows.Window
            {
                Width = 0, Height = 0, WindowStyle = System.Windows.WindowStyle.None, ShowInTaskbar = false,
                ShowActivated = false, Visibility = System.Windows.Visibility.Hidden,
            };
            var handle = new WindowInteropHelper(_hotkeyHost).EnsureHandle();
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
            if (!Native.RegisterHotKey(handle, HotkeyStage, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Forms.Keys.V))
                Log.Write("No se pudo registrar Ctrl+Alt+V (otra app lo usa).");
            if (!Native.RegisterHotKey(handle, HotkeyOverlay, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Forms.Keys.L))
                Log.Write("No se pudo registrar Ctrl+Alt+L (otra app lo usa).");

            _displayDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _displayDebounce.Tick += (_, _) => { _displayDebounce.Stop(); RebuildWindows(); };
            SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

            // Sin ventanas abiertas la letra se sigue precargando con eventos; este temporizador solo mantiene el reloj.
            _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _idleTimer.Tick += (_, _) => { if (!_rendering) _director.Tick(Monotonic.Now); };
            _idleTimer.Start();
        }

        public void Start()
        {
            _monitors = Native.GetMonitors();
            Log.Write("Versalia iniciado. Monitores: " + string.Join(" | ",
                _monitors.Select(m => $"{m.Width}x{m.Height} en ({m.X},{m.Y}) {m.Dpi / 96 * 100:0}%")));
            _source.Start();
            if (_settings.Mode != "off") ApplyMode(_settings.Mode);
            if (!_settings.ShownWelcome)
            {
                _settings.ShownWelcome = true;
                _settings.Save();
                _tray.ShowBalloonTip(6000, "Versalia está en la bandeja",
                    "Pulsa Ctrl+Alt+V para el escenario en todas tus pantallas (Esc para salir) o Ctrl+Alt+L para la letra flotante.",
                    Forms.ToolTipIcon.Info);
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (id == HotkeyStage) ToggleMode("stage");
                else if (id == HotkeyOverlay) ToggleMode("overlay");
                handled = true;
            }
            return IntPtr.Zero;
        }

        private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        {
            Log.Write("Cambió la configuración de pantallas.");
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                _displayDebounce.Stop();
                _displayDebounce.Start();
            }));
        }

        // ───────────────────────── Modos ─────────────────────────

        private void ToggleMode(string mode) => ApplyMode(_settings.Mode == mode ? "off" : mode);

        private void ApplyMode(string mode)
        {
            _settings.Mode = mode;
            _settings.Save();
            RebuildWindows();
        }

        private void RebuildWindows()
        {
            foreach (var w in _windows) { w.EscapePressed -= OnEscape; w.Close(); }
            _windows.Clear();
            StopRendering();
            _monitors = Native.GetMonitors();
            if (_settings.Mode == "off" || _monitors.Count == 0) return;

            bool overlay = _settings.Mode == "overlay";
            var targets = overlay
                ? new List<MonitorInfo> { PickOverlayMonitor() }
                : _monitors;
            foreach (var m in targets)
            {
                var window = new SceneWindow(_scene, m, overlay);
                window.EscapePressed += OnEscape;
                _windows.Add(window);
                window.Show();
            }
            if (!overlay) _windows.FirstOrDefault()?.Activate();
            Log.Write($"Modo {_settings.Mode}: {_windows.Count} ventana(s).");
            StartRendering();
        }

        private MonitorInfo PickOverlayMonitor()
        {
            if (_settings.OverlayMonitor >= 0 && _settings.OverlayMonitor < _monitors.Count) return _monitors[_settings.OverlayMonitor];
            return _monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors[0];
        }

        private void OnEscape() => ApplyMode("off");

        // ───────────────────────── Dibujo ─────────────────────────

        private void StartRendering()
        {
            if (_rendering) return;
            _rendering = true;
            System.Windows.Media.CompositionTarget.Rendering += OnRendering;
        }

        private void StopRendering()
        {
            if (!_rendering) return;
            _rendering = false;
            System.Windows.Media.CompositionTarget.Rendering -= OnRendering;
        }

        private void OnRendering(object? sender, EventArgs e)
        {
            if (_windows.Count == 0) return;
            double now = Monotonic.Now;
            var frame = _director.Tick(now);
            // En pausa no hace falta ir a 60 fps.
            _frameCounter++;
            if (!frame.Playing && _frameCounter % 3 != 0) return;

            var mode = _settings.Mode == "overlay" ? SceneMode.Overlay : SceneMode.Stage;
            int overlayIndex = _settings.Mode == "overlay" ? PickOverlayMonitor().Index : -1;
            _scene.Update(frame, _monitors, mode, overlayIndex, now);
            foreach (var w in _windows) w.View.InvalidateVisual();
        }

        // ───────────────────────── Bandeja ─────────────────────────

        private void BuildMenu()
        {
            var menu = _tray.ContextMenuStrip!;
            menu.Items.Clear();

            Forms.ToolStripMenuItem Item(string text, Action action, bool isChecked = false)
            {
                var item = new Forms.ToolStripMenuItem(text) { Checked = isChecked };
                item.Click += (_, _) => action();
                return item;
            }

            var monitors = Native.GetMonitors();
            var info = new Forms.ToolStripMenuItem(
                $"Pantallas detectadas: {monitors.Count} ({string.Join(" + ", monitors.Select(m => $"{m.Width}×{m.Height}"))})")
            { Enabled = false };
            menu.Items.Add(info);
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Item("Escenario en todas las pantallas   Ctrl+Alt+V", () => ToggleMode("stage"), _settings.Mode == "stage"));
            menu.Items.Add(Item("Letra flotante transparente   Ctrl+Alt+L", () => ToggleMode("overlay"), _settings.Mode == "overlay"));

            var overlayMenu = new Forms.ToolStripMenuItem("Pantalla de la letra flotante");
            overlayMenu.DropDownItems.Add(Item("Pantalla principal", () => { _settings.OverlayMonitor = -1; _settings.Save(); if (_settings.Mode == "overlay") RebuildWindows(); }, _settings.OverlayMonitor < 0));
            for (int i = 0; i < monitors.Count; i++)
            {
                int index = i;
                overlayMenu.DropDownItems.Add(Item($"Pantalla {i + 1} ({monitors[i].Width}×{monitors[i].Height})",
                    () => { _settings.OverlayMonitor = index; _settings.Save(); if (_settings.Mode == "overlay") RebuildWindows(); },
                    _settings.OverlayMonitor == i));
            }
            menu.Items.Add(overlayMenu);
            menu.Items.Add(new Forms.ToolStripSeparator());

            var palettes = new Forms.ToolStripMenuItem("Colores");
            palettes.DropDownItems.Add(Item("Automático (según la portada)", () => SetPalette("auto"), _settings.Palette == "auto"));
            foreach (var id in ThemePresets.All.Keys)
            {
                string captured = id;
                palettes.DropDownItems.Add(Item(char.ToUpper(id[0]) + id.Substring(1), () => SetPalette(captured), _settings.Palette == id));
            }
            menu.Items.Add(palettes);

            var sync = new Forms.ToolStripMenuItem($"Sincronía de la letra ({_settings.OffsetMs:+#;-#;0} ms)");
            sync.DropDownItems.Add(Item("Adelantar la letra  +100 ms", () => _director.ChangeOffset(100)));
            sync.DropDownItems.Add(Item("Retrasar la letra  −100 ms", () => _director.ChangeOffset(-100)));
            sync.DropDownItems.Add(Item("Restablecer (0 ms)", () => _director.ChangeOffset(0)));
            menu.Items.Add(sync);
            menu.Items.Add(new Forms.ToolStripSeparator());

            menu.Items.Add(Item("Iniciar con Windows", ToggleAutostart, IsAutostart()));
            menu.Items.Add(Item("Abrir carpeta de registros", () => Process.Start(new ProcessStartInfo(Paths.Logs) { UseShellExecute = true })));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Item("Salir", () => System.Windows.Application.Current.Shutdown()));
        }

        private void SetPalette(string id) { _settings.Palette = id; _settings.Save(); }

        private static bool IsAutostart()
        {
            try { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("Versalia") != null; }
            catch { return false; }
        }

        private static void ToggleAutostart()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key == null) return;
                if (key.GetValue("Versalia") != null) key.DeleteValue("Versalia");
                else key.SetValue("Versalia", "\"" + Environment.ProcessPath + "\"");
            }
            catch (Exception e) { Log.Write("Inicio con Windows: " + e.Message); }
        }

        private static Icon MakeIcon()
        {
            using var bitmap = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using var brush = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), Color.FromArgb(255, 60, 255, 176), Color.FromArgb(255, 140, 156, 255), 45f);
                g.FillEllipse(brush, 1, 1, 30, 30);
                using var font = new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
                var size = g.MeasureString("V", font);
                g.DrawString("V", font, Brushes.Black, (32 - size.Width) / 2, (32 - size.Height) / 2);
            }
            return Icon.FromHandle(bitmap.GetHicon());
        }

        public void Dispose()
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            StopRendering();
            _idleTimer.Stop();
            foreach (var w in _windows) w.Close();
            var handle = new WindowInteropHelper(_hotkeyHost).Handle;
            Native.UnregisterHotKey(handle, HotkeyStage);
            Native.UnregisterHotKey(handle, HotkeyOverlay);
            _source.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
        }
    }
}
