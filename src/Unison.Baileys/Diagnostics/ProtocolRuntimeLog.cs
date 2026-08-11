using System;
using System.Diagnostics;

namespace Unison.Baileys.Diagnostics
{
    /// <summary>
    /// UI-free protocol logging bridge. The foreground may attach its existing
    /// diagnostic sink; the external task keeps payloads and JIDs out of the
    /// durable broker log.
    /// </summary>
    public static class ProtocolRuntimeLog
    {
        public static Action<string> Sink { get; set; }

        [Conditional("DEBUG")]
        public static void Write(string message)
        {
            try
            {
                Action<string> sink = Sink;
                if (sink != null)
                {
                    sink(message);
                    return;
                }
            }
            catch
            {
            }

            Debug.WriteLine(message);
        }
    }
}
