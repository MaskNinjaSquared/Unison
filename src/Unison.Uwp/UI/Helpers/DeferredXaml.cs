// =============================================================================
// DeferredXaml
//
// x:Load leaves the generated x:Name field null until realized. Hosts call
// Ensure after flipping the gate (DP / Bindings.Update) so first open does not
// NullReference. Call Unload (x:Load=false) only on Unsupported tiers when the
// plan asks to reclaim RAM after close.
// =============================================================================
using System;
using Windows.UI.Xaml;

namespace Unison.Uwp.UI.Helpers
{
    internal static class DeferredXaml
    {
        /// <summary>
        /// Realizes a deferred element if needed, caches it, and runs
        /// <paramref name="onFirstRealize"/> once when the instance is new.
        /// </summary>
        public static T Ensure<T>(
            ref T cache,
            bool isMaterialized,
            Action materialize,
            Func<T> generatedField,
            FrameworkElement nameScope,
            string name,
            Action<T> onFirstRealize = null)
            where T : class
        {
            if (cache != null)
            {
                return cache;
            }

            if (!isMaterialized)
            {
                materialize?.Invoke();
            }

            T realized = generatedField?.Invoke();
            if (realized == null && nameScope != null && !string.IsNullOrEmpty(name))
            {
                realized = nameScope.FindName(name) as T;
            }

            if (realized == null)
            {
                return null;
            }

            cache = realized;
            onFirstRealize?.Invoke(realized);
            return cache;
        }

        /// <summary>Clears the cache when the host sets x:Load back to false.</summary>
        public static void Forget<T>(ref T cache)
            where T : class
        {
            cache = null;
        }
    }
}
