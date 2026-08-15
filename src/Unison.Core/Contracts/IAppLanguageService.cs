using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Applies <see cref="AppLanguage"/> via PrimaryLanguageOverride.
    /// Desktop: dialog + process restart. Mobile: toast + Exit; next launch applies settings.
    /// </summary>
    public interface IAppLanguageService
    {
        /// <summary>Reads LocalSettings and sets PrimaryLanguageOverride (call early on launch).</summary>
        void ApplyFromSettings();

        /// <summary>
        /// Persists language and applies override, then restarts (Desktop) or exits (Mobile).
        /// </summary>
        Task ChangeLanguageAndRestartAsync(AppLanguage language);
    }
}
