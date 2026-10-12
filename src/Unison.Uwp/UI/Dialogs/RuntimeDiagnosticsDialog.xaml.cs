using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Dialogs
{
    /// <summary>
    /// Mobile-friendly snapshot of runtime health + recent journal, with save to LocalState.
    /// </summary>
    public sealed partial class RuntimeDiagnosticsDialog : ContentDialog
    {
        private string _reportText = string.Empty;
        private bool _saving;
        private bool _requestingHistory;

        public RuntimeDiagnosticsDialog()
        {
            this.InitializeComponent();
            this.Opened += RuntimeDiagnosticsDialog_Opened;
        }

        private async void RuntimeDiagnosticsDialog_Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            await RefreshReportAsync();
        }

        private async Task RefreshReportAsync()
        {
            try
            {
                var diagnostics = App.Services?.GetService<IRuntimeDiagnostics>();
                if (diagnostics == null)
                {
                    ReportBox.Text = "Runtime diagnostics service is not registered.";
                    return;
                }

                await diagnostics.FlushAsync("dialog-open");
                RuntimeDiagnosticsSnapshot snapshot = diagnostics.CaptureSnapshot();
                string recent = diagnostics.GetRecentText();
                _reportText =
                    (snapshot != null ? snapshot.ToDisplayText() : "<no snapshot>") +
                    Environment.NewLine +
                    "=== RECENT IN-MEMORY EVENTS ===" +
                    Environment.NewLine +
                    (string.IsNullOrWhiteSpace(recent) ? "<empty>" : recent);
                ReportBox.Text = _reportText;
            }
            catch (Exception ex)
            {
                ReportBox.Text = "Failed to build report: " + ex.Message;
            }
        }

        private void CopyReportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string text = string.IsNullOrWhiteSpace(_reportText) ? ReportBox.Text : _reportText;
                if (string.IsNullOrWhiteSpace(text))
                {
                    SaveStatusText.Text = "Nothing to copy.";
                    return;
                }

                var package = new DataPackage();
                package.SetText(text);
                Clipboard.SetContent(package);
                SaveStatusText.Text = "Copied to clipboard.";
            }
            catch (Exception ex)
            {
                SaveStatusText.Text = "Copy failed: " + ex.Message;
            }
        }

        private async void RequestFullHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_requestingHistory)
            {
                return;
            }

            _requestingHistory = true;
            RequestFullHistoryButton.IsEnabled = false;
            try
            {
                SaveStatusText.Text = "Requesting full history…";
                var history = App.Services?.GetService<IHistoryService>();
                if (history == null)
                {
                    SaveStatusText.Text = "History service unavailable.";
                    return;
                }

                bool ok = await history.RequestFullHistoryOnDemandAsync(
                    "diagnostics-dialog",
                    force: true).ConfigureAwait(true);
                SaveStatusText.Text = ok
                    ? "Full history on-demand requested."
                    : "Full history request was not sent (socket not ready or skipped).";
                await RefreshReportAsync();
            }
            catch (Exception ex)
            {
                SaveStatusText.Text = "Full history request failed: " + ex.Message;
            }
            finally
            {
                _requestingHistory = false;
                RequestFullHistoryButton.IsEnabled = true;
            }
        }

        private async void ContentDialog_PrimaryButtonClick(
            ContentDialog sender,
            ContentDialogButtonClickEventArgs args)
        {
            if (_saving)
            {
                args.Cancel = true;
                return;
            }

            var deferral = args.GetDeferral();
            _saving = true;
            try
            {
                SaveStatusText.Text = "Saving…";
                var diagnostics = App.Services?.GetService<IRuntimeDiagnostics>();
                if (diagnostics == null)
                {
                    SaveStatusText.Text = "Save failed: diagnostics unavailable.";
                    args.Cancel = true;
                    return;
                }

                string path = await diagnostics.SaveReportToLocalFolderAsync();
                SaveStatusText.Text = string.IsNullOrWhiteSpace(path)
                    ? "Save failed."
                    : "Saved: " + path;
                args.Cancel = true; // keep dialog open so the path is readable on Mobile
                await RefreshReportAsync();
            }
            catch (Exception ex)
            {
                SaveStatusText.Text = "Save failed: " + ex.Message;
                args.Cancel = true;
            }
            finally
            {
                _saving = false;
                deferral.Complete();
            }
        }
    }
}
