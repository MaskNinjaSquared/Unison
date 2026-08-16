using System.Threading.Tasks;
using Unison.Core.Contracts;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Shared dialog loop for <see cref="IBackgroundAccessPrompt"/>. Used from Boot
    /// (<see cref="ViewModels.ShellViewModel.InitializeAsync"/>) so Login and Shell
    /// both pass through one check.
    /// </summary>
    public sealed class BackgroundAccessPrompt : IBackgroundAccessPrompt
    {
        private readonly IBackgroundAccessService _access;
        private readonly IDialogService _dialogs;
        private readonly IAppLifecycle _lifecycle;
        private readonly IStringResources _strings;
        private readonly ISessionLogger _logger;
        private bool _promptActive;

        public BackgroundAccessPrompt(
            IBackgroundAccessService access,
            IDialogService dialogs,
            IAppLifecycle lifecycle,
            IStringResources strings,
            ISessionLogger logger)
        {
            _access = access;
            _dialogs = dialogs;
            _lifecycle = lifecycle;
            _strings = strings;
            _logger = logger;
        }

        public async Task<bool> EnsureOrExitAsync()
        {
            if (_access == null)
            {
                return true;
            }

            if (await _access.RefreshAllowedAsync())
            {
                return true;
            }

            if (_promptActive)
            {
                return false;
            }

            _promptActive = true;
            try
            {
                while (true)
                {
                    WriteLog("background access denied — prompting");
                    bool ok = await _dialogs.ShowConfirmAsync(
                        Get("Login_BackgroundAccessTitle", "Background apps"),
                        Get(
                            "Login_BackgroundAccessBody",
                            "Unison needs permission to run in the background to connect to WhatsApp. Turn Unison on under Settings → Privacy → Background apps, then return here."),
                        Get("Common_OK", "OK"),
                        Get("Common_Cancel", "Cancel"));

                    if (!ok)
                    {
                        WriteLog("background access prompt cancelled — exit");
                        _lifecycle?.Exit();
                        return false;
                    }

                    if (await _access.RefreshAllowedAsync())
                    {
                        WriteLog("background access allowed after OK");
                        return true;
                    }

                    WriteLog("opening system background-apps settings");
                    bool launched = await _access.OpenSettingsAsync();
                    if (launched && _lifecycle != null)
                    {
                        await _lifecycle.WaitUntilForegroundAsync();
                    }

                    if (await _access.RefreshAllowedAsync())
                    {
                        WriteLog("background access allowed after Settings");
                        return true;
                    }
                }
            }
            finally
            {
                _promptActive = false;
            }
        }

        private string Get(string key, string fallback)
        {
            return _strings != null ? _strings.Get(key, fallback) : fallback;
        }

        private void WriteLog(string message)
        {
            try
            {
                _logger?.WriteAlways("[Boot] " + message);
            }
            catch
            {
            }
        }
    }
}
