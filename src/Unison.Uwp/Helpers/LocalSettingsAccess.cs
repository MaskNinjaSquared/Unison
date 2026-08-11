using Microsoft.Extensions.DependencyInjection;
using Unison.Core.Contracts;
using Unison.Uwp.Services;

namespace Unison.Uwp.Helpers
{
    /// <summary>
    /// Resolves <see cref="ILocalSettings"/> from DI when available, otherwise a
    /// direct wrapper (needed for early App constructor / static singletons).
    /// </summary>
    internal static class LocalSettingsAccess
    {
        private static readonly LocalSettingsService Fallback = new LocalSettingsService();

        public static ILocalSettings Current
        {
            get
            {
                try
                {
                    if (App.Services != null)
                        return App.Services.GetRequiredService<ILocalSettings>();
                }
                catch
                {
                }

                return Fallback;
            }
        }
    }
}
