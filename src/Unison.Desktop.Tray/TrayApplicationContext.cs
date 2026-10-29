using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Windows.ApplicationModel;
using Windows.ApplicationModel.AppService;
using Windows.Foundation.Collections;

namespace Unison.Desktop.Tray
{
    /// <summary>
    /// Notification-area icon + AppService client — Unigram Stub <c>BridgeApplicationContext</c>
    /// reduced to Open / Exit / unread. Reconnects when the UWP side closes the service with
    /// <c>CloseRequested</c> (window closed to tray); exits when the user picks Exit.
    /// </summary>
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private const int MaxTooltipLength = 63;

        private readonly NotifyIcon _icon;
        private readonly ToolStripMenuItem _openItem;
        private readonly ToolStripMenuItem _exitItem;
        private readonly Control _uiInvoker;
        private AppServiceConnection _connection;
        private int _uwpProcessId;
        private long _uwpWindowId;
        private bool _closeRequested = true;

        public TrayApplicationContext()
        {
            _uiInvoker = new Control();
            _uiInvoker.CreateControl();

            bool portuguese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt";
            _openItem = new ToolStripMenuItem(portuguese ? "Abrir Unison" : "Open Unison");
            _openItem.Click += (s, e) => OpenApp();
            _exitItem = new ToolStripMenuItem(portuguese ? "Sair" : "Exit");
            _exitItem.Click += (s, e) => _ = ExitAsync();

            var menu = new ContextMenuStrip();
            menu.Items.Add(_openItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_exitItem);

            _icon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "Unison",
                ContextMenuStrip = menu,
                Visible = true
            };
            _icon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left)
                {
                    OpenApp();
                }
            };

            Connect();
        }

        private async void Connect()
        {
            if (_connection != null)
            {
                return;
            }

            try
            {
                var connection = new AppServiceConnection
                {
                    AppServiceName = TrayProtocol.ServiceName,
                    PackageFamilyName = Package.Current.Id.FamilyName
                };
                connection.RequestReceived += OnRequestReceived;
                connection.ServiceClosed += OnServiceClosed;

                AppServiceConnectionStatus status = await connection.OpenAsync();
                if (status != AppServiceConnectionStatus.Success)
                {
                    Debug.WriteLine("[Tray] AppService open failed: " + status);
                    connection.Dispose();
                    OnUi(ExitThread);
                    return;
                }

                _connection = connection;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Tray] Connect failed: " + ex.Message);
                OnUi(ExitThread);
            }
        }

        private async void OnRequestReceived(AppServiceConnection sender, AppServiceRequestReceivedEventArgs args)
        {
            var deferral = args.GetDeferral();
            var response = new ValueSet();
            ValueSet message = args.Request.Message;
            try
            {
                object raw;
                if (message.TryGetValue(TrayProtocol.KeyProcessId, out raw) && raw is int processId)
                {
                    _uwpProcessId = processId;
                    response[TrayProtocol.KeyProcessId] = Process.GetCurrentProcess().Id;
                }

                if (TryReadInt64(message, TrayProtocol.KeyWindowId, out long windowId) && windowId != 0)
                {
                    _uwpWindowId = windowId;
                }

                if (message.TryGetValue(TrayProtocol.KeyOpenText, out raw) && raw is string openText)
                {
                    OnUi(() => _openItem.Text = openText);
                }

                if (message.TryGetValue(TrayProtocol.KeyExitText, out raw) && raw is string exitText)
                {
                    OnUi(() => _exitItem.Text = exitText);
                }

                if (message.TryGetValue(TrayProtocol.KeyUnreadCount, out raw) && raw is int unread)
                {
                    OnUi(() => ApplyUnread(unread));
                }

                if (message.ContainsKey(TrayProtocol.KeyCloseRequested))
                {
                    _closeRequested = true;
                    long hideWindowId = _uwpWindowId;
                    int hideProcessId = _uwpProcessId;
                    OnUi(() => NativeWindow.HideAppWindow(hideWindowId, hideProcessId));
                }

                if (message.ContainsKey(TrayProtocol.KeyExit))
                {
                    _closeRequested = false;
                }

                await args.Request.SendResponseAsync(response);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Tray] Request failed: " + ex.Message);
            }
            finally
            {
                deferral.Complete();
            }

            if (message.ContainsKey(TrayProtocol.KeyExit))
            {
                OnUi(() =>
                {
                    DisposeConnection();
                    ExitThread();
                });
            }
        }

        private void OnServiceClosed(AppServiceConnection sender, AppServiceClosedEventArgs args)
        {
            DisposeConnection();
            if (_closeRequested)
            {
                // UWP closed the window but wants the tray to stay (confirmAppClose path).
                Connect();
            }
            else
            {
                OnUi(ExitThread);
            }
        }

        private void ApplyUnread(int unread)
        {
            string text = unread <= 0
                ? "Unison"
                : "Unison — " + (unread > 99 ? "99+" : unread.ToString(CultureInfo.CurrentCulture));
            _icon.Text = text.Length > MaxTooltipLength ? text.Substring(0, MaxTooltipLength) : text;
        }

        private async void OpenApp()
        {
            OnUi(() => NativeWindow.ShowAppWindow(_uwpWindowId, _uwpProcessId));

            try
            {
                // Unigram: Package.Current.GetAppListEntriesAsync().First().LaunchAsync()
                var entries = await Package.Current.GetAppListEntriesAsync();
                var entry = entries?.FirstOrDefault();
                if (entry != null)
                {
                    await entry.LaunchAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Tray] Open failed: " + ex.Message);
            }

            OnUi(() => NativeWindow.ShowAppWindow(_uwpWindowId, _uwpProcessId));
            Connect();
        }

        private static bool TryReadInt64(ValueSet message, string key, out long value)
        {
            value = 0;
            if (!message.TryGetValue(key, out object raw) || raw == null)
            {
                return false;
            }

            if (raw is long asLong)
            {
                value = asLong;
                return true;
            }

            if (raw is int asInt)
            {
                value = asInt;
                return true;
            }

            return long.TryParse(raw.ToString(), out value);
        }

        private async Task ExitAsync()
        {
            _closeRequested = false;
            AppServiceConnection connection = _connection;
            if (connection != null)
            {
                try
                {
                    await connection.SendMessageAsync(new ValueSet { { TrayProtocol.KeyExit, string.Empty } });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[Tray] Exit message failed: " + ex.Message);
                }
            }

            // If UWP did not exit, kill the process we were told about (Unigram SessionEnded path).
            if (_uwpProcessId > 0)
            {
                try
                {
                    Process.GetProcessById(_uwpProcessId)?.Kill();
                }
                catch
                {
                }
            }

            OnUi(ExitThread);
        }

        private void DisposeConnection()
        {
            if (_connection == null)
            {
                return;
            }

            _connection.RequestReceived -= OnRequestReceived;
            _connection.ServiceClosed -= OnServiceClosed;
            _connection.Dispose();
            _connection = null;
        }

        private void OnUi(Action action)
        {
            if (_uiInvoker.IsDisposed)
            {
                return;
            }

            if (_uiInvoker.InvokeRequired)
            {
                _uiInvoker.BeginInvoke(action);
            }
            else
            {
                action();
            }
        }

        private static Icon LoadIcon()
        {
            using (Stream png = typeof(TrayApplicationContext).Assembly
                       .GetManifestResourceStream("Unison.Desktop.Tray.TrayIcon.png"))
            {
                if (png == null)
                {
                    return SystemIcons.Application;
                }

                using (var bitmap = new Bitmap(png))
                {
                    return Icon.FromHandle(bitmap.GetHicon());
                }
            }
        }

        protected override void ExitThreadCore()
        {
            _icon.Visible = false;
            _icon.Dispose();
            DisposeConnection();
            _uiInvoker.Dispose();
            base.ExitThreadCore();
        }
    }
}
