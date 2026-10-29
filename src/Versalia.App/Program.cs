using System;
using System.Threading;
using System.Windows;

namespace Versalia.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            using var mutex = new Mutex(true, "Versalia-Windows-SingleInstance", out bool first);
            if (!first) return;

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += (_, e) =>
            {
                Log.Write("Error no controlado: " + e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Error fatal: " + e.ExceptionObject);

            var controller = new AppController();
            app.Startup += (_, _) => controller.Start();
            app.Exit += (_, _) => controller.Dispose();
            app.Run();
        }
    }
}
