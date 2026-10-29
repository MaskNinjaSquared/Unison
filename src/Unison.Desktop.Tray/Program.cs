using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Windows.ApplicationModel;

namespace Unison.Desktop.Tray
{
    /// <summary>
    /// Full-trust stub (Unigram <c>Telegram.Stub</c> shape). Launched by
    /// <c>FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync("SystemTrayGroup")</c>.
    /// </summary>
    internal static class Program
    {
        private const string SingleInstanceMutex = "Local\\Unison.Desktop.Tray.V1";

        [STAThread]
        private static void Main(string[] args)
        {
            // Always refresh loopback exemption when the stub starts (Unigram does this first).
            TryAddLoopbackExemption();

            if (args != null && args.Any(a =>
                    string.Equals(a, TrayProtocol.ArgLoopbackExempt, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            bool createdNew;
            using (var mutex = new Mutex(true, SingleInstanceMutex, out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApplicationContext());
            }
        }

        private static void TryAddLoopbackExemption()
        {
            try
            {
                string familyName = Package.Current.Id.FamilyName;
                var info = new ProcessStartInfo
                {
                    FileName = "CheckNetIsolation.exe",
                    Arguments = "LoopbackExempt -a -n=" + familyName,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process process = Process.Start(info))
                {
                    process?.WaitForExit(5000);
                }
            }
            catch
            {
                // Best-effort; tray still works without it.
            }
        }
    }
}
