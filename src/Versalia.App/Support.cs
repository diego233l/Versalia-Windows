using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Versalia.App
{
    internal static class Monotonic
    {
        public static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    }

    internal static class Paths
    {
        public static readonly string Root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Versalia");
        public static string Logs => Path.Combine(Root, "logs");
        public static string Cache => Path.Combine(Root, "cache");
        public static string SettingsFile => Path.Combine(Root, "settings.json");
    }

    internal static class Log
    {
        private static readonly object Gate = new object();
        private static string File_ => Path.Combine(Paths.Logs, "versalia.log");

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Paths.Logs);
                    var info = new FileInfo(File_);
                    if (info.Exists && info.Length > 1_000_000)
                        File.Move(File_, Path.Combine(Paths.Logs, "versalia.old.log"), true);
                    File.AppendAllText(File_, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    internal sealed class OffsetSample
    {
        public string Player { get; set; } = "";
        public string Key { get; set; } = "";
        public int Ms { get; set; }
    }

    /// <summary>Token de Apple Music del usuario, cifrado con su cuenta de Windows (DPAPI). Nunca va a settings.json ni al registro.</summary>
    internal static class AppleToken
    {
        private static string FilePath => Path.Combine(Paths.Root, "apple-music.token");
        private static string? _cached;
        private static bool _loaded;

        public static string? Get()
        {
            if (_loaded) return _cached;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return _cached = null;
                var bytes = System.Security.Cryptography.ProtectedData.Unprotect(File.ReadAllBytes(FilePath), null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                _cached = System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch (Exception e) { Log.Write("No se pudo leer el token de Apple Music: " + e.Message); _cached = null; }
            return _cached;
        }

        public static bool IsSet => !string.IsNullOrEmpty(Get());

        public static void Set(string? token)
        {
            try
            {
                Directory.CreateDirectory(Paths.Root);
                if (string.IsNullOrEmpty(token)) { if (File.Exists(FilePath)) File.Delete(FilePath); }
                else
                {
                    var bytes = System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(token), null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(FilePath, bytes);
                }
                _cached = string.IsNullOrEmpty(token) ? null : token;
                _loaded = true;
            }
            catch (Exception e) { Log.Write("No se pudo guardar el token de Apple Music: " + e.Message); }
        }
    }

    internal sealed class Settings
    {
        /// <summary>off, stage (escenario multipantalla) u overlay (solo letra, transparente).</summary>
        public string Mode { get; set; } = "off";
        public int OffsetMs { get; set; } = 0;
        public string Palette { get; set; } = "auto";
        /// <summary>duo, aurora, neon, poster u orbit.</summary>
        public string Style { get; set; } = "duo";
        public int OverlayMonitor { get; set; } = -1;
        public bool ShownWelcome { get; set; } = false;
        /// <summary>Ajuste de sincronía guardado por canción (clave: título|artista normalizados), en ms.</summary>
        public System.Collections.Generic.Dictionary<string, int> TrackOffsets { get; set; } = new System.Collections.Generic.Dictionary<string, int>();

        /// <summary>Ajustes recientes por reproductor: su mediana es el punto de partida de las canciones sin ajuste propio.</summary>
        public System.Collections.Generic.List<OffsetSample> OffsetSamples { get; set; } = new System.Collections.Generic.List<OffsetSample>();

        public static Settings Load()
        {
            try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(Paths.SettingsFile)) ?? new Settings(); }
            catch { return new Settings(); }
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Paths.Root);
                File.WriteAllText(Paths.SettingsFile, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e) { Log.Write("No se pudieron guardar los ajustes: " + e.Message); }
        }
    }
}
