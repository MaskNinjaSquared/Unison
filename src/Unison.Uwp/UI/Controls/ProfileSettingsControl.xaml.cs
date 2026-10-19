using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Unison expanded-profile rows (name, phone, about). The hero owns the photo and the motion.
    /// </summary>
    public sealed partial class ProfileSettingsControl : UserControl
    {
        private const double ProfileFieldsMaxWidth = 800;

        public ProfileSettingsControl()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Each profile row fills the width on hand and stops at 800. Measuring them in a stack
        /// would shrink the row to the text.
        /// </summary>
        private void ProfileSettingsRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (ProfileFieldsHost == null || e.NewSize.Width <= 0)
            {
                return;
            }

            double horizontalPadding = ProfileFieldsHost.Padding.Left + ProfileFieldsHost.Padding.Right;
            double available = Math.Max(0, e.NewSize.Width - horizontalPadding);
            double boxWidth = Math.Min(available, ProfileFieldsMaxWidth);
            double target = boxWidth + horizontalPadding;
            if (double.IsNaN(ProfileFieldsHost.Width) || Math.Abs(ProfileFieldsHost.Width - target) > 0.5)
            {
                ProfileFieldsHost.Width = target;
            }

            bool bounded = !double.IsNaN(Height) && !double.IsPositiveInfinity(Height) && Height > 0;
            ProfileScroll.VerticalScrollMode = bounded ? ScrollMode.Enabled : ScrollMode.Disabled;
        }
    }
}
