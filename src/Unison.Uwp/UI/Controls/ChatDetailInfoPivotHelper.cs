using System;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    internal static class ChatDetailInfoPivotHelper
    {
        public static string Upper(string value)
        {
            return string.IsNullOrEmpty(value) ? string.Empty : value.ToUpperInvariant();
        }

        /// <summary>Always open chat-info on the first pivot (Profile / Group info).</summary>
        public static void ResetToRoot(Pivot pivot)
        {
            if (pivot == null || pivot.Items == null || pivot.Items.Count == 0)
            {
                return;
            }

            if (pivot.SelectedIndex != 0)
            {
                pivot.SelectedIndex = 0;
            }
        }

        /// <summary>
        /// Media and Files share one SQLite index; start it when either pivot is selected.
        /// </summary>
        public static void RequestMediaIndexIfSelected(
            Pivot pivot,
            ChatDetailInfoViewModel vm,
            PivotItem mediaItem,
            PivotItem filesItem)
        {
            if (vm == null || pivot == null)
            {
                return;
            }

            object selected = pivot.SelectedItem;
            if (selected == mediaItem || selected == filesItem)
            {
                vm.EnsureMediaIndex();
            }
        }

        public static bool IsMediaPaneProperty(string propertyName)
        {
            return propertyName == nameof(ChatDetailInfoViewModel.IsMediaIndexLoading) ||
                   propertyName == nameof(ChatDetailInfoViewModel.HasMedia) ||
                   propertyName == nameof(ChatDetailInfoViewModel.HasFiles) ||
                   propertyName == nameof(ChatDetailInfoViewModel.MediaItems) ||
                   propertyName == nameof(ChatDetailInfoViewModel.FileItems) ||
                   propertyName == nameof(ChatDetailInfoViewModel.CanLoadMoreMedia) ||
                   propertyName == nameof(ChatDetailInfoViewModel.CanLoadMoreFiles);
        }

        public static void ApplyNotificationsToggle(
            ToggleSwitch toggle,
            ChatDetailInfoViewModel vm,
            ref bool quiet)
        {
            if (toggle == null || vm == null)
            {
                return;
            }

            toggle.OnContent = vm.NotificationsOnText ?? "On";
            toggle.OffContent = vm.NotificationsOffText ?? "Off";

            bool on = vm.NotificationsEnabled;
            if (toggle.IsOn == on)
            {
                return;
            }

            quiet = true;
            try
            {
                toggle.IsOn = on;
            }
            finally
            {
                quiet = false;
            }
        }

        public static void ExecuteNotificationsToggle(
            ToggleSwitch toggle,
            ChatDetailInfoViewModel vm,
            bool quiet)
        {
            if (quiet || vm == null || toggle == null)
            {
                return;
            }

            var cmd = vm.SetNotificationsCommand;
            bool enabled = toggle.IsOn;
            if (cmd?.CanExecute(enabled) == true)
            {
                cmd.Execute(enabled);
            }
        }

        public static void HandleMediaItemClick(DependencyObject start, object clickedItem)
        {
            var vm = clickedItem as ChatMessageViewModel;
            var host = FindChatDetail(start);
            if (vm == null || host == null)
            {
                return;
            }

            if (vm.IsAudio)
            {
                host.PlayOrPauseAudioFromInfo(vm);
                return;
            }

            if (vm.IsVideo)
            {
                host.OpenInfoVideo(vm);
                return;
            }

            host.OpenInfoImage(vm);
        }

        public static ChatDetailView FindChatDetail(DependencyObject start)
        {
            var current = start;
            while (current != null)
            {
                var view = current as ChatDetailView;
                if (view != null)
                {
                    return view;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
