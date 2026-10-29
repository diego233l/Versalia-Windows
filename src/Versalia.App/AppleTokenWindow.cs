using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Versalia.Core;

namespace Versalia.App
{
    /// <summary>Ventana para pegar el media-user-token de music.apple.com, con los pasos para encontrarlo.</summary>
    internal sealed class AppleTokenWindow : Window
    {
        private readonly PasswordBox _box;
        private readonly TextBlock _error;
        public string Token { get; private set; } = "";

        public AppleTokenWindow()
        {
            Title = "Versalia: letras de Apple Music";
            Width = 640;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Topmost = true;
            var panel = new StackPanel { Margin = new Thickness(22) };

            panel.Children.Add(new TextBlock
            {
                Text = "Conectar Versalia con tu cuenta de Apple Music", FontSize = 20, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
            });
            panel.Children.Add(Paragraph(
                "Así Versalia usa las mismas letras que la app de Apple Music, con los tiempos por sílaba de la versión que suena. " +
                "Hace falta copiar una vez un código de tu sesión de la web de Apple Music:"));
            panel.Children.Add(Paragraph(
                "1. Pulsa «Abrir Apple Music en el navegador» e inicia sesión con tu cuenta (si no lo estás).\n" +
                "2. En esa página pulsa F12. Arriba, entra en la pestaña «Aplicación» (en inglés «Application»; si no la ves, pulsa »).\n" +
                "3. A la izquierda abre «Cookies» y haz clic en «https://music.apple.com».\n" +
                "4. En la lista busca la fila «media-user-token». Haz doble clic en su valor, pulsa Ctrl+C y pégalo abajo con Ctrl+V."));

            var open = new Button { Content = "Abrir Apple Music en el navegador", Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 14) };
            open.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo("https://music.apple.com/") { UseShellExecute = true }); }
                catch { }
            };
            panel.Children.Add(open);

            panel.Children.Add(new TextBlock { Text = "media-user-token:", Margin = new Thickness(0, 0, 0, 4) });
            _box = new PasswordBox { FontSize = 14, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(_box);
            _error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            panel.Children.Add(_error);

            panel.Children.Add(Paragraph(
                "El código se guarda cifrado con tu usuario de Windows y solo se envía a Apple. Da acceso a tu cuenta, " +
                "así que no lo compartas. Caduca al cabo de unos meses o si cierras la sesión en la web: entonces el menú " +
                "dirá «token rechazado» y basta con repetir estos pasos. Apple no ofrece esto oficialmente y podría dejar de funcionar.",
                11.5, 0.65));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0) };
            var save = new Button { Content = "Guardar", IsDefault = true, Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0) };
            var cancel = new Button { Content = "Cancelar", IsCancel = true, Padding = new Thickness(18, 6, 18, 6) };
            save.Click += (_, _) =>
            {
                var token = AppleMusicProvider.CleanToken(_box.Password);
                if (token.Length < 40 || token.Contains(' '))
                {
                    _error.Text = "Eso no parece el valor de media-user-token. Copia solo el valor (un texto largo de letras y números).";
                    return;
                }
                Token = token;
                DialogResult = true;
            };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            Content = panel;
            Loaded += (_, _) => _box.Focus();
        }

        private static TextBlock Paragraph(string text, double size = 13.5, double opacity = 1) => new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = size, Opacity = opacity, Margin = new Thickness(0, 0, 0, 10),
            LineHeight = size * 1.45,
        };
    }
}
