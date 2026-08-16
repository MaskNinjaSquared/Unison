namespace Unison.Core.Models
{
    /// <summary>
    /// UI languages shipped under Strings/{tag}/Resources.resw.
    /// <see cref="System"/> follows the OS (PrimaryLanguageOverride empty);
    /// otherwise a concrete override. Persisted as
    /// <see cref="Constants.LocalSettingsConstants.SelectedLanguage"/>.
    /// </summary>
    public enum AppLanguage
    {
        /// <summary>Follow OS preferred languages; fall back to English resources.</summary>
        System = -1,

        English = 0,
        PortugueseBrazil = 1,
        Spanish = 2,
        Italian = 3,
        Dutch = 4,
        Indonesian = 5,
        Polish = 6,
        Ukrainian = 7
    }
}
