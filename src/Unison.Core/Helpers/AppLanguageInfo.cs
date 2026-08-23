using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Tags (BCP-47) and display names for <see cref="AppLanguage"/>.
    /// Priority when System: OS preferred tags → first shipped match → English.
    /// </summary>
    public static class AppLanguageInfo
    {
        private static readonly AppLanguage[] AllLanguages =
        {
            AppLanguage.System,
            AppLanguage.English,
            AppLanguage.PortugueseBrazil,
            AppLanguage.Spanish,
            AppLanguage.Italian,
            AppLanguage.Dutch,
            AppLanguage.Indonesian,
            AppLanguage.Polish,
            AppLanguage.Ukrainian,
            AppLanguage.Russian,
            AppLanguage.German
        };

        private static readonly AppLanguage[] ShippedLanguages =
        {
            AppLanguage.English,
            AppLanguage.PortugueseBrazil,
            AppLanguage.Spanish,
            AppLanguage.Italian,
            AppLanguage.Dutch,
            AppLanguage.Indonesian,
            AppLanguage.Polish,
            AppLanguage.Ukrainian,
            AppLanguage.Russian,
            AppLanguage.German
        };

        /// <summary>ComboBox order (System first, then shipped locales).</summary>
        public static IReadOnlyList<AppLanguage> All => AllLanguages;

        /// <summary>Locales that have Strings/{tag}/Resources.resw (excludes System).</summary>
        public static IReadOnlyList<AppLanguage> Shipped => ShippedLanguages;

        /// <summary>Canonical English label for <see cref="AppLanguage.System"/> (UI may localize).</summary>
        public const string SystemDisplayName = "System";

        /// <summary>
        /// Localized ComboBox labels in <see cref="All"/> order.
        /// System uses <paramref name="strings"/> key <c>Settings_LanguageSystem</c>.
        /// </summary>
        public static IReadOnlyList<string> GetDisplayNames(Contracts.IStringResources strings)
        {
            if (strings == null)
            {
                throw new ArgumentNullException(nameof(strings));
            }

            IReadOnlyList<AppLanguage> all = All;
            var names = new string[all.Count];
            for (int i = 0; i < all.Count; i++)
            {
                names[i] = all[i] == AppLanguage.System
                    ? strings.Get("Settings_LanguageSystem", SystemDisplayName)
                    : GetDisplayName(all[i]);
            }

            return names;
        }

        /// <summary>Label for ComboBox (System uses <see cref="SystemDisplayName"/>; UI may replace via resources).</summary>
        public static string GetDisplayName(AppLanguage language)
        {
            switch (language)
            {
                case AppLanguage.System:
                    return SystemDisplayName;
                case AppLanguage.English:
                    return "English";
                case AppLanguage.PortugueseBrazil:
                    return "Português (Brasil)";
                case AppLanguage.Spanish:
                    return "Español";
                case AppLanguage.Italian:
                    return "Italiano";
                case AppLanguage.Dutch:
                    return "Nederlands";
                case AppLanguage.Indonesian:
                    return "Bahasa Indonesia";
                case AppLanguage.Polish:
                    return "Polski";
                case AppLanguage.Ukrainian:
                    return "Українська";
                case AppLanguage.Russian:
                    return "Русский";
                case AppLanguage.German:
                    return "Deutsch";
                default:
                    return "English";
            }
        }

        /// <summary>
        /// True when any OS preference tag maps to a shipped locale (not only English fallback).
        /// </summary>
        public static bool OsListContainsShipped(IEnumerable<string> osLanguageTags)
        {
            if (osLanguageTags == null)
            {
                return false;
            }

            foreach (string tag in osLanguageTags)
            {
                if (TryMapShipped(tag, out _))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Effective concrete language: user override, or OS match, or English.
        /// </summary>
        public static AppLanguage ResolveEffective(
            AppLanguage selected,
            IEnumerable<string> osLanguageTags)
        {
            if (!IsSystem(selected))
            {
                return selected;
            }

            return ResolveFromOsPreferences(osLanguageTags);
        }

        /// <summary>
        /// Resource qualifier for a concrete language.
        /// For <see cref="AppLanguage.System"/> returns empty (clear PrimaryLanguageOverride).
        /// </summary>
        public static string GetTag(AppLanguage language)
        {
            switch (language)
            {
                case AppLanguage.System:
                    return string.Empty;
                case AppLanguage.English:
                    return "en-US";
                case AppLanguage.PortugueseBrazil:
                    return "pt-BR";
                case AppLanguage.Spanish:
                    return "es-ES";
                case AppLanguage.Italian:
                    return "it-IT";
                case AppLanguage.Dutch:
                    return "nl-NL";
                case AppLanguage.Indonesian:
                    return "id-ID";
                case AppLanguage.Polish:
                    return "pl-PL";
                case AppLanguage.Ukrainian:
                    return "uk-UA";
                case AppLanguage.Russian:
                    return "ru-RU";
                case AppLanguage.German:
                    return "de-DE";
                default:
                    return "en-US";
            }
        }

        public static bool IsSystem(AppLanguage language) => language == AppLanguage.System;

        /// <summary>
        /// Maps an OS/BCP-47 tag onto a shipped language, or English if unsupported.
        /// </summary>
        public static AppLanguage FromTag(string tag)
        {
            AppLanguage mapped;
            if (TryMapShipped(tag, out mapped))
            {
                return mapped;
            }

            return AppLanguage.English;
        }

        /// <summary>
        /// Picks the first OS-preferred language we ship; otherwise English.
        /// </summary>
        public static AppLanguage ResolveFromOsPreferences(IEnumerable<string> osLanguageTags)
        {
            if (osLanguageTags != null)
            {
                foreach (string tag in osLanguageTags)
                {
                    AppLanguage mapped;
                    if (TryMapShipped(tag, out mapped))
                    {
                        return mapped;
                    }
                }
            }

            return AppLanguage.English;
        }

        public static AppLanguage FromStored(int raw)
        {
            return Enum.IsDefined(typeof(AppLanguage), raw)
                ? (AppLanguage)raw
                : AppLanguage.System;
        }

        private static bool TryMapShipped(string tag, out AppLanguage language)
        {
            language = AppLanguage.English;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return false;
            }

            string normalized = tag.Trim().Replace('_', '-');
            for (int i = 0; i < ShippedLanguages.Length; i++)
            {
                if (string.Equals(GetTag(ShippedLanguages[i]), normalized, StringComparison.OrdinalIgnoreCase))
                {
                    language = ShippedLanguages[i];
                    return true;
                }
            }

            int dash = normalized.IndexOf('-');
            string primary = dash > 0 ? normalized.Substring(0, dash) : normalized;
            for (int i = 0; i < ShippedLanguages.Length; i++)
            {
                string known = GetTag(ShippedLanguages[i]);
                if (known.StartsWith(primary + "-", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(known, primary, StringComparison.OrdinalIgnoreCase))
                {
                    language = ShippedLanguages[i];
                    return true;
                }
            }

            return false;
        }
    }
}
