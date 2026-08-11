using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.ViewModels;
using Unison.Uwp.Client;
using Unison.Uwp.Helpers;
using Unison.Uwp.Services;
using Unison.Uwp.Services.WhatsApp;
using Windows.ApplicationModel;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Views
{
    public sealed partial class DebugView : UserControl
    {
        public event EventHandler BackRequested;
        private bool _isInitializing;
        private bool _isActive;
        private DebugViewModel _viewModel;
        private readonly object _pendingLogLock = new object();
        private readonly List<string> _pendingLogLines = new List<string>();
        private readonly DispatcherTimer _logFlushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        private readonly DispatcherTimer _runtimeRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        private const int MaxDisplayedLogCharacters = 60000;

        private DebugViewModel ViewModel
        {
            get
            {
                if (_viewModel == null && App.Services != null)
                {
                    _viewModel = App.Services.GetRequiredService<DebugViewModel>();
                    DataContext = _viewModel;
                    _viewModel.BackRequested += (s, e) => BackRequested?.Invoke(this, e);
                }
                return _viewModel;
            }
        }

        public DebugView()
        {
            this.InitializeComponent();
            StripToggleLabels(VerboseLoggingToggle);
            StripToggleLabels(SessionLoggingToggle);
            _logFlushTimer.Tick += LogFlushTimer_Tick;
            _runtimeRefreshTimer.Tick += RuntimeRefreshTimer_Tick;
            this.Loaded += DebugView_Loaded;
            this.Unloaded += DebugView_Unloaded;
        }

        private void DebugView_Loaded(object sender, RoutedEventArgs e)
        {
            if (Visibility == Visibility.Visible)
            {
                Activate();
            }
        }

        private void DebugView_Unloaded(object sender, RoutedEventArgs e)
        {
            Deactivate();
        }

        public void Activate()
        {
            if (_isActive)
            {
                return;
            }

            _isActive = true;
            _isInitializing = true;
            var vm = ViewModel;
            vm?.RefreshFromServices();
            ClearToggleLabels(VerboseLoggingToggle);
            ClearToggleLabels(SessionLoggingToggle);
            SessionLoggingToggle.IsOn = vm?.IsSessionLoggingEnabled ?? SessionLogger.Instance.Enabled;
            VerboseLoggingToggle.IsOn = vm?.IsVerboseLoggingEnabled ?? WhatsAppService.VerboseLogging;
            SessionLogText.Text = TrimDisplayedLog(vm?.LogText ?? SessionLogger.Instance.GetLogText());
            _isInitializing = false;

            SessionLogger.Instance.OnLogUpdated -= Instance_OnLogUpdated;
            SessionLogger.Instance.OnLogUpdated += Instance_OnLogUpdated;
            RefreshRuntimeHealth();
            UpdateBuildInfo();
            _logFlushTimer.Start();
            _runtimeRefreshTimer.Start();
        }

        public void Deactivate()
        {
            if (!_isActive)
            {
                return;
            }

            _isActive = false;
            SessionLogger.Instance.OnLogUpdated -= Instance_OnLogUpdated;
            _logFlushTimer.Stop();
            _runtimeRefreshTimer.Stop();
            lock (_pendingLogLock)
            {
                _pendingLogLines.Clear();
            }
        }

        /// <summary>
        /// UWP ToggleSwitch still reserves On/Off label width for "". Use collapsed
        /// zero-size content so the knob sits flush right like Settings.
        /// </summary>
        private static void StripToggleLabels(ToggleSwitch toggle)
        {
            if (toggle == null)
            {
                return;
            }

            toggle.ClearValue(FrameworkElement.WidthProperty);
            toggle.MinWidth = 0;
            toggle.Margin = new Thickness(0);
            toggle.OnContent = CreateEmptyToggleContent();
            toggle.OffContent = CreateEmptyToggleContent();
        }

        private static UIElement CreateEmptyToggleContent()
        {
            return new Border
            {
                Width = 0,
                Height = 0,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false
            };
        }

        private static void ClearToggleLabels(ToggleSwitch toggle)
        {
            StripToggleLabels(toggle);
        }

        private void Instance_OnLogUpdated(object sender, string line)
        {
            lock (_pendingLogLock)
            {
                _pendingLogLines.Add(line ?? string.Empty);
                if (_pendingLogLines.Count > 100)
                {
                    _pendingLogLines.RemoveRange(0, _pendingLogLines.Count - 100);
                }
            }
        }

        private void LogFlushTimer_Tick(object sender, object e)
        {
            string[] lines;
            lock (_pendingLogLock)
            {
                if (_pendingLogLines.Count == 0)
                {
                    return;
                }
                lines = _pendingLogLines.ToArray();
                _pendingLogLines.Clear();
            }

            var builder = new StringBuilder(SessionLogText.Text ?? string.Empty);
            foreach (var line in lines)
            {
                builder.AppendLine(line);
            }
            SessionLogText.Text = TrimDisplayedLog(builder.ToString());
            SessionLogScroller.ChangeView(null, SessionLogScroller.ScrollableHeight, null, true);
        }

        private static string TrimDisplayedLog(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= MaxDisplayedLogCharacters)
            {
                return text ?? string.Empty;
            }
            return text.Substring(text.Length - MaxDisplayedLogCharacters);
        }

        private void RuntimeRefreshTimer_Tick(object sender, object e)
        {
            RefreshRuntimeHealth();
        }

        private void RefreshRuntimeHealth()
        {
            try
            {
                var snapshot = RuntimeDiagnosticsService.Instance.CaptureSnapshot();
                string recent = RuntimeDiagnosticsService.Instance.GetRecentText();
                RuntimeHealthText.Text = snapshot.ToDisplayText() +
                    Environment.NewLine +
                    "RECENT RUNTIME EVENTS" + Environment.NewLine +
                    (string.IsNullOrWhiteSpace(recent) ? "<none>" : recent);
            }
            catch (Exception ex)
            {
                RuntimeHealthText.Text = "Unable to capture runtime health: " + ex.Message;
            }
        }

        private void UpdateBuildInfo()
        {
            try
            {
                PackageVersion version = Package.Current.Id.Version;
                BuildInfoText.Text = string.Format(
                    "Build: {0}.{1}.{2}.{3} ({4}) - Refactor Phase A",
                    version.Major,
                    version.Minor,
                    version.Build,
                    version.Revision,
                    Package.Current.Id.Architecture);
            }
            catch
            {
                BuildInfoText.Text = "Build: Refactor Phase A";
            }
        }

        private void RefreshRuntimeButton_Click(object sender, RoutedEventArgs e)
        {
            RefreshRuntimeHealth();
        }

        private async void SaveRuntimeReportButton_Click(object sender, RoutedEventArgs e)
        {
            RuntimeExportStatusText.Text = LocalizedStrings.Get("Debug_PreparingReport");
            string result = await RuntimeDiagnosticsService.Instance.ExportReportAsync();
            RuntimeExportStatusText.Text = result;
            RefreshRuntimeHealth();
        }

        private async void ClearRuntimeLogButton_Click(object sender, RoutedEventArgs e)
        {
            await RuntimeDiagnosticsService.Instance.ClearAsync();
            RuntimeExportStatusText.Text = LocalizedStrings.Get("Debug_RuntimeCleared");
            RefreshRuntimeHealth();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke(this, EventArgs.Empty);
        }

        private void VerboseLoggingToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isInitializing) return;
            if (ViewModel != null)
                ViewModel.IsVerboseLoggingEnabled = VerboseLoggingToggle.IsOn;
            else
                WhatsAppService.SetVerboseLogging(VerboseLoggingToggle.IsOn, "DebugView.Toggle");
        }

        private void SessionLoggingToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
                ViewModel.IsSessionLoggingEnabled = SessionLoggingToggle.IsOn;
            else
                SessionLogger.Instance.Enabled = SessionLoggingToggle.IsOn;
        }

        private void SaveSessionLogButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.SaveLogCommand?.CanExecute(null) == true)
                ViewModel.SaveLogCommand.Execute(null);
            else
                _ = SessionLogger.Instance.SaveToFileAsync();
        }

        private void ClearSessionLogButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.ClearLogCommand?.CanExecute(null) == true)
                ViewModel.ClearLogCommand.Execute(null);
            else
                SessionLogger.Instance.Clear();
            SessionLogText.Text = "";
        }

        private void TestDHButton_Click(object sender, RoutedEventArgs e)
        {
            // Placeholder for original TestDH logic if needed
        }

        private async void DeleteSessionButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ViewModel != null)
                {
                    await ViewModel.WipeSessionAsync();
                    // ShellViewModel.OnSessionCleared → Login imediato, QR após wipe de auth.
                    return;
                }

                var dialog = new ContentDialog
                {
                    Title = LocalizedStrings.Get("Debug_WipeTitle"),
                    Content = LocalizedStrings.Get("Debug_WipeBody"),
                    PrimaryButtonText = LocalizedStrings.Get("Debug_WipeDelete"),
                    CloseButtonText = LocalizedStrings.Get("Debug_WipeCancel")
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    await App.GetWhatsAppService().ClearSessionAsync();
                }
            }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80070057) || ex.Message.Contains("single ContentDialog"))
            {
                System.Diagnostics.Debug.WriteLine("[DebugView] Cannot show session wipe dialog - another dialog is open.");
            }
        }
    }
}
