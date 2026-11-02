using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Desktop.Tray;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.ApplicationModel.Background;
using Windows.Foundation.Collections;
using Windows.Foundation.Metadata;
using Windows.System.Profile;

namespace Unison.Uwp.Services.Desktop
{
    /// <summary>
    /// Unigram-shaped bridge host (<c>BridgeApplicationContext</c> on the UWP side):
    /// launch <c>SystemTrayGroup</c>, take the in-process AppService, push ProcessId / menu
    /// labels / unread, react to Exit. Store/Mobile package has no full-trust process — launch
    /// fails quietly.
    /// </summary>
    public sealed class DesktopTrayHost : IDesktopTrayHost
    {
        private const string DesktopFamily = "Windows.Desktop";

        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Lazy<bool> _isAvailable;
        private readonly object _gate = new object();
        private AppServiceConnection _connection;
        private BackgroundTaskDeferral _deferral;
        private int _lastUnread = -1;
        private int _started;

        public DesktopTrayHost(IRuntimeDiagnostics diagnostics)
        {
            _diagnostics = diagnostics;
            _isAvailable = new Lazy<bool>(DetectAvailability);
        }

        public event EventHandler ExitRequested;

        public bool IsAvailable => _isAvailable.Value;

        public async Task<bool> StartAsync()
        {
            if (!IsAvailable)
            {
                _diagnostics?.Write("desktop-tray", "launch-skipped", "reason=not-available");
                return false;
            }

            if (Interlocked.Exchange(ref _started, 1) == 1)
            {
                return false;
            }

            try
            {
                // Unigram: LaunchFullTrustProcessForCurrentAppAsync("SystemTrayGroup")
                await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync(
                    TrayProtocol.SystemTrayGroup);
                _diagnostics?.Write("desktop-tray", "launched", "group=" + TrayProtocol.SystemTrayGroup);
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _started, 0);
                _diagnostics?.Write(
                    "desktop-tray",
                    "launch-skipped",
                    "reason=" + ex.GetType().Name +
                    "; hr=0x" + ex.HResult.ToString("X8") +
                    "; msg=" + Sanitize(ex.Message));
                return false;
            }
        }

        /// <summary>Optional: Unigram's LoopbackExemptGroup (CheckNetIsolation in the stub).</summary>
        public async Task AddLoopbackExemptionAsync()
        {
            if (!IsAvailable)
            {
                return;
            }

            try
            {
                await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync(
                    TrayProtocol.LoopbackExemptGroup);
            }
            catch (Exception ex)
            {
                _diagnostics?.Write("desktop-tray", "loopback-skipped", "reason=" + ex.GetType().Name);
            }
        }

        public void PublishUnread(int totalUnread)
        {
            int value = Math.Max(0, totalUnread);
            AppServiceConnection connection;
            lock (_gate)
            {
                if (_lastUnread == value)
                {
                    return;
                }

                _lastUnread = value;
                connection = _connection;
            }

            if (connection != null)
            {
                _ = SendAsync(connection, new ValueSet
                {
                    { TrayProtocol.KeyUnreadCount, value },
                    { TrayProtocol.KeyUnreadUnmutedCount, value }
                });
            }
        }

        /// <summary>Tell the stub to hide the frame window (close-to-tray); process stays.</summary>
        public void NotifyCloseRequested(long windowId)
        {
            AppServiceConnection connection;
            lock (_gate)
            {
                connection = _connection;
            }

            if (connection == null)
            {
                return;
            }

            var message = new ValueSet { { TrayProtocol.KeyCloseRequested, true } };
            if (windowId != 0)
            {
                message[TrayProtocol.KeyWindowId] = windowId;
            }

            _ = SendAsync(connection, message);
        }

        public bool TryAttach(IBackgroundTaskInstance taskInstance)
        {
            var details = taskInstance?.TriggerDetails as AppServiceTriggerDetails;
            if (details == null ||
                !string.Equals(details.Name, TrayProtocol.ServiceName, StringComparison.Ordinal))
            {
                return false;
            }

            AppServiceConnection connection = details.AppServiceConnection;
            int unread;
            lock (_gate)
            {
                ReleaseConnection();
                _deferral = taskInstance.GetDeferral();
                _connection = connection;
                unread = _lastUnread;
            }

            taskInstance.Canceled += (s, reason) =>
            {
                _diagnostics?.Write("desktop-tray", "disconnected", "reason=" + reason);
                lock (_gate)
                {
                    if (ReferenceEquals(_connection, connection))
                    {
                        ReleaseConnection();
                    }
                }

                Interlocked.Exchange(ref _started, 0);
            };
            connection.RequestReceived += Connection_RequestReceived;
            _diagnostics?.Write("desktop-tray", "connected");

            // Unigram handshake: ProcessId + localized menu labels, then unread.
            _ = SendHandshakeAsync(connection, unread);
            return true;
        }

        private async Task SendHandshakeAsync(AppServiceConnection connection, int unread)
        {
            var hello = new ValueSet
            {
                { TrayProtocol.KeyProcessId, Process.GetCurrentProcess().Id },
                { TrayProtocol.KeyOpenText, "Abrir Unison" },
                { TrayProtocol.KeyExitText, "Sair" }
            };
            long windowId = TryGetCoreWindowHandle();
            if (windowId != 0)
            {
                hello[TrayProtocol.KeyWindowId] = windowId;
            }

            await SendAsync(connection, hello);
            if (unread >= 0)
            {
                await SendAsync(connection, new ValueSet
                {
                    { TrayProtocol.KeyUnreadCount, unread },
                    { TrayProtocol.KeyUnreadUnmutedCount, unread }
                });
            }
        }

        private void Connection_RequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            if (args.Request.Message != null &&
                args.Request.Message.ContainsKey(TrayProtocol.KeyExit))
            {
                _diagnostics?.Write("desktop-tray", "exit-requested");
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task SendAsync(AppServiceConnection connection, ValueSet message)
        {
            try
            {
                await connection.SendMessageAsync(message);
            }
            catch (Exception ex)
            {
                _diagnostics?.RecordException("desktop-tray", "send-failed", ex);
            }
        }

        private void ReleaseConnection()
        {
            if (_connection != null)
            {
                _connection.RequestReceived -= Connection_RequestReceived;
                _connection = null;
            }

            _deferral?.Complete();
            _deferral = null;
        }

        private static bool DetectAvailability()
        {
            try
            {
                // Unigram: HasPackageIdentity && IsTypePresent(FullTrustProcessLauncher)
                return string.Equals(
                           AnalyticsInfo.VersionInfo.DeviceFamily,
                           DesktopFamily,
                           StringComparison.OrdinalIgnoreCase) &&
                       ApiInformation.IsTypePresent("Windows.ApplicationModel.FullTrustProcessLauncher");
            }
            catch
            {
                return false;
            }
        }

        private static long TryGetCoreWindowHandle()
        {
            try
            {
                var core = Windows.UI.Xaml.Window.Current?.CoreWindow;
                if (core == null)
                {
                    return 0;
                }

                var interop = (ICoreWindowInterop)(object)core;
                return interop.WindowHandle.ToInt64();
            }
            catch
            {
                return 0;
            }
        }

        private static string Sanitize(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return string.Empty;
            }

            return message.Replace('\r', ' ').Replace('\n', ' ');
        }

        [ComImport]
        [System.Runtime.InteropServices.Guid("45D64A29-A63E-4CB6-B498-5781D298CB4F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICoreWindowInterop
        {
            IntPtr WindowHandle { get; }

            bool MessageHandled { set; }
        }
    }
}
