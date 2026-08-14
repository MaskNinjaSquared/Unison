using System;
using System.Globalization;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Converters
{
    /// <summary>
    /// Formats a byte count (<see cref="long"/> / <see cref="ulong"/> / <see cref="int"/>)
    /// as a short size string (B / KB / MB / GB).
    /// </summary>
    public sealed class ByteSizeConverter : IValueConverter
    {
        private const double Kb = 1024d;
        private const double Mb = Kb * 1024d;
        private const double Gb = Mb * 1024d;

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (!TryGetBytes(value, out long bytes) || bytes <= 0)
            {
                return string.Empty;
            }

            return Format(bytes, ResolveCulture(language));
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }

        public static string Format(long bytes, CultureInfo culture = null)
        {
            if (bytes <= 0)
            {
                return string.Empty;
            }

            culture = culture ?? CultureInfo.CurrentCulture;

            if (bytes >= Gb)
            {
                return (bytes / Gb).ToString("0.##", culture) + " GB";
            }

            if (bytes >= Mb)
            {
                return (bytes / Mb).ToString("0.##", culture) + " MB";
            }

            if (bytes >= Kb)
            {
                return (bytes / Kb).ToString("0.##", culture) + " KB";
            }

            return bytes.ToString(culture) + " B";
        }

        private static CultureInfo ResolveCulture(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return CultureInfo.CurrentCulture;
            }

            try
            {
                return CultureInfo.GetCultureInfo(language);
            }
            catch (CultureNotFoundException)
            {
                return CultureInfo.CurrentCulture;
            }
        }

        private static bool TryGetBytes(object value, out long bytes)
        {
            switch (value)
            {
                case long l:
                    bytes = l;
                    return true;
                case ulong ul:
                    bytes = ul > long.MaxValue ? long.MaxValue : (long)ul;
                    return true;
                case int i:
                    bytes = i;
                    return true;
                case uint ui:
                    bytes = ui;
                    return true;
                default:
                    bytes = 0;
                    return false;
            }
        }
    }
}
