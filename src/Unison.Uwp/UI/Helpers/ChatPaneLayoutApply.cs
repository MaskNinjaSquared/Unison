// =============================================================================
// ChatPaneLayoutApply
//
// Direct column/visibility geometry for NarrowList / NarrowDetail / WideBoth.
// Avoids VisualStateManager.GoToState on list→detail (Mobile hitch): same
// setters Legacy applies via GridLength + Visibility, without a VSM pass.
// =============================================================================
using System;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Helpers
{
    internal static class ChatPaneLayoutApply
    {
        /// <summary>
        /// Applies pane geometry when <paramref name="pane"/> differs from
        /// <paramref name="appliedPane"/>. Returns true when layout ran.
        /// </summary>
        public static bool ApplyIfChanged(
            ref string appliedPane,
            string pane,
            ColumnDefinition listColumn,
            ColumnDefinition detailColumn,
            FrameworkElement listPart,
            FrameworkElement detailPart,
            FrameworkElement splitter)
        {
            if (string.Equals(appliedPane, pane, StringComparison.Ordinal))
            {
                return false;
            }

            appliedPane = pane;
            Apply(pane, listColumn, detailColumn, listPart, detailPart, splitter);
            return true;
        }

        public static void Apply(
            string pane,
            ColumnDefinition listColumn,
            ColumnDefinition detailColumn,
            FrameworkElement listPart,
            FrameworkElement detailPart,
            FrameworkElement splitter)
        {
            if (listColumn == null || detailColumn == null)
            {
                return;
            }

            if (string.Equals(pane, ShellViewModel.PaneNarrowDetail, StringComparison.Ordinal))
            {
                listColumn.Width = new GridLength(0);
                listColumn.MinWidth = 0;
                listColumn.MaxWidth = 10000;
                detailColumn.Width = new GridLength(1, GridUnitType.Star);
                detailColumn.MinWidth = 0;
                SetVisible(listPart, false);
                SetVisible(detailPart, true);
                SetVisible(splitter, false);
                return;
            }

            if (string.Equals(pane, ShellViewModel.PaneNarrowList, StringComparison.Ordinal))
            {
                listColumn.Width = new GridLength(1, GridUnitType.Star);
                listColumn.MinWidth = 0;
                listColumn.MaxWidth = 10000;
                detailColumn.Width = new GridLength(0);
                detailColumn.MinWidth = 0;
                SetVisible(listPart, true);
                SetVisible(detailPart, false);
                SetVisible(splitter, false);
                return;
            }

            // WideBoth (default)
            listColumn.MinWidth = 280;
            listColumn.MaxWidth = 720;
            detailColumn.Width = new GridLength(1, GridUnitType.Star);
            detailColumn.MinWidth = 280;
            SetVisible(listPart, true);
            SetVisible(detailPart, true);
            SetVisible(splitter, true);
        }

        private static void SetVisible(FrameworkElement element, bool visible)
        {
            if (element == null)
            {
                return;
            }

            element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
