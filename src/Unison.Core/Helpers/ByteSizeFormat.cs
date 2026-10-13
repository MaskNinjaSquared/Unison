using System.Globalization;

namespace Unison.Core.Helpers
{
    /// <summary>Short size label for the disk-usage row.</summary>
    public static class ByteSizeFormat
    {
        public static string Format(long bytes)
        {
            if (bytes < 0)
            {
                bytes = 0;
            }

            const long kb = 1024;
            const long mb = kb * 1024;
            const long gb = mb * 1024;
            if (bytes >= gb)
            {
                return ((double)bytes / gb).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            }

            if (bytes >= mb)
            {
                return ((double)bytes / mb).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            }

            if (bytes >= kb)
            {
                return ((double)bytes / kb).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            }

            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }
    }
}
