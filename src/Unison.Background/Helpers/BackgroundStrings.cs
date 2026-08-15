using System;
using Windows.ApplicationModel.Resources;

namespace Unison.Background
{
    /// <summary>
    /// Toast / preview copy for background task. Uses the app package
    /// <see cref="ResourceLoader"/> (same PRI as Unison.Uwp Strings/*.resw).
    /// Prefer <see cref="ResourceLoader.GetForViewIndependentUse"/> — no UI thread in BG.
    /// </summary>
    internal static class BackgroundStrings
    {
        private static ResourceLoader _loader;

        private static ResourceLoader Loader
        {
            get
            {
                if (_loader == null)
                {
                    try
                    {
                        _loader = ResourceLoader.GetForViewIndependentUse();
                    }
                    catch
                    {
                        try
                        {
                            _loader = ResourceLoader.GetForCurrentView();
                        }
                        catch
                        {
                            return null;
                        }
                    }
                }

                return _loader;
            }
        }

        public static string Get(string key, string fallback = null)
        {
            if (string.IsNullOrEmpty(key))
            {
                return fallback ?? string.Empty;
            }

            try
            {
                ResourceLoader loader = Loader;
                if (loader == null)
                {
                    return fallback ?? key;
                }

                // resw name Toast_Foo → GetString("Toast_Foo"); dotted Uid keys → slash.
                string path = key.IndexOf('.') >= 0 ? key.Replace('.', '/') : key;
                string value = loader.GetString(path);
                return string.IsNullOrEmpty(value) ? (fallback ?? key) : value;
            }
            catch
            {
                return fallback ?? key;
            }
        }
    }
}
