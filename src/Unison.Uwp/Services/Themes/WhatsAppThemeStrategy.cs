using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.ViewModels;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Services.Themes
{
    /// <summary>
    /// WhatsApp shell: OS-default title bar background; green caption text + min/max/close glyphs.
    /// Sync feedback always stays in the chat-list header.
    /// Settings: <see cref="IDialogService.ShowWhatsAppSettingsAsync"/> overlay (not a shell page).
    /// </summary>
    public sealed class WhatsAppThemeStrategy : ShellThemeStrategy
    {
        /// <summary>Matches WhatsAppGreenFocusBrush (#1DAA61).</summary>
        private static readonly Color CaptionGreen = Color.FromArgb(0xFF, 0x1D, 0xAA, 0x61);

        /// <summary>Slightly brighter glyph on hover (#25D366).</summary>
        private static readonly Color CaptionGreenHover = Color.FromArgb(0xFF, 0x25, 0xD3, 0x66);

        private readonly IDialogService _dialogs;
        private readonly Func<SettingsViewModel> _settingsVmFactory;
        private bool _settingsOpen;

        public WhatsAppThemeStrategy(IDialogService dialogs, Func<SettingsViewModel> settingsVmFactory)
        {
            _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
            _settingsVmFactory = settingsVmFactory ?? throw new ArgumentNullException(nameof(settingsVmFactory));
        }

        public override bool DisplaySyncInChatList => true;

        public override bool UsesMobileStatusBarProgress => false;

        /// <summary>
        /// Keep the system caption background; only tint title + window buttons green.
        /// Explicit null / Transparent clears any leftover Unison green fill from an in-process shell switch.
        /// </summary>
        public override void SetTitleBar()
        {
            try
            {
                var titleBar = ApplicationView.GetForCurrentView().TitleBar;

                // Bar fill = OS default (no green strip).
                titleBar.BackgroundColor = null;
                titleBar.InactiveBackgroundColor = null;

                titleBar.ForegroundColor = CaptionGreen;
                titleBar.InactiveForegroundColor = CaptionGreen;

                // Glyphs only — transparent button chrome over the default bar.
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonHoverBackgroundColor = Colors.Transparent;
                titleBar.ButtonPressedBackgroundColor = Colors.Transparent;

                titleBar.ButtonForegroundColor = CaptionGreen;
                titleBar.ButtonInactiveForegroundColor = CaptionGreen;
                titleBar.ButtonHoverForegroundColor = CaptionGreenHover;
                titleBar.ButtonPressedForegroundColor = CaptionGreen;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WhatsAppTheme] SetTitleBar: " + ex.Message);
            }
        }

        public override void OpenSettings(Frame shellFrame)
        {
            // Keep the current shell page (usually Chats); settings is an overlay dialog.
            if (_settingsOpen || _dialogs == null)
            {
                return;
            }

            _ = OpenSettingsOverlayAsync();
        }

        public override bool IsSettingsPage(object content)
        {
            // Settings is never a Frame page on WhatsApp shell.
            return false;
        }

        private async Task OpenSettingsOverlayAsync()
        {
            if (_settingsOpen)
            {
                return;
            }

            _settingsOpen = true;
            try
            {
                SettingsViewModel vm = _settingsVmFactory();
                await _dialogs.ShowWhatsAppSettingsAsync(vm);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WhatsAppTheme] OpenSettings: " + ex.Message);
            }
            finally
            {
                _settingsOpen = false;
            }
        }
    }
}
