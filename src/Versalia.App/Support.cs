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

    internal sealed class Settings
    {
        /// <summary>off, stage (escenario multipantalla) u overlay (solo letra, transparente).</summary>
        public string Mode { get; set; } = "off";
        public int OffsetMs { get; set; } = 0;
        public string Palette { get; set; } = "auto";
        public int OverlayMonitor { get; set; } = -1;
        public bool ShownWelcome { get; set; } = false;

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
