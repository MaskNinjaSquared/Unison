using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Windows.System;

namespace Unison.Uwp.Services
{
    public sealed class UriLauncherService : IUriLauncher
    {
        public async Task<bool> LaunchAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            try
            {
                return await Launcher.LaunchUriAsync(new Uri(url));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[UriLauncherService] " + ex.Message);
                return false;
            }
        }
    }
}
