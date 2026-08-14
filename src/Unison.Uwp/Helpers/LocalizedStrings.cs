using System;
using Windows.ApplicationModel.Resources;

namespace Unison.Uwp.Helpers
{
    /// <summary>
    /// Thin wrapper around ResourceLoader for code-behind (Giuraffe style).
    /// Resw dotted property keys (Foo.Text) are requested as Foo/Text.
    /// </summary>
    public static class LocalizedStrings
    {
        private static ResourceLoader _loader;

        private static ResourceLoader Loader
        {
            get
            {
                if (_loader == null)
                {
                    try { _loader = ResourceLoader.GetForCurrentView(); }
                    catch { _loader = ResourceLoader.GetForViewIndependentUse(); }
                }
                return _loader;
            }
        }

        /// <summary>
        /// Drop cached loader after <c>PrimaryLanguageOverride</c> changes so the next
        /// lookup uses the new language list.
        /// </summary>
        public static void Reset()
        {
            _loader = null;
        }

        /// <param name="fallback">Used when the key is missing; when null, the key (or empty) is returned.</param>
        public static string Get(string key, string fallback = null)
        {
            if (string.IsNullOrEmpty(key)) return fallback ?? string.Empty;
            try
            {
                string value = Loader.GetString(ToResourcePath(key));
                return string.IsNullOrEmpty(value) ? (fallback ?? key) : value;
            }
            catch
            {
                return fallback ?? key;
            }
        }

        public static string Format(string key, params object[] args)
        {
            string format = Get(key);
            try { return string.Format(format, args); }
            catch { return format; }
        }

        private static string ToResourcePath(string key)
        {
            return key.Replace('.', '/');
        }
    }
}
