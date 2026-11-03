using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Windows.UI.Core;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Wires A–Z <see cref="SemanticZoom"/> + grouped <see cref="ListView"/> the same way as
    /// New Conversation contacts (jump tiles ↔ scroll into letter).
    /// </summary>
    internal sealed class AlphabetSemanticZoomHost
    {
        private readonly SemanticZoom _zoom;
        private readonly ListView _list;
        private readonly GridView _jumpList;
        private readonly CollectionViewSource _cvs = new CollectionViewSource { IsSourceGrouped = true };
        private AlphabetChatGroup _pendingJumpGroup;

        public AlphabetSemanticZoomHost(SemanticZoom zoom, ListView list, GridView jumpList)
        {
            _zoom = zoom ?? throw new ArgumentNullException(nameof(zoom));
            _list = list ?? throw new ArgumentNullException(nameof(list));
            _jumpList = jumpList ?? throw new ArgumentNullException(nameof(jumpList));

            _zoom.ViewChangeStarted += OnViewChangeStarted;
            _zoom.ViewChangeCompleted += OnViewChangeCompleted;
            _jumpList.ItemClick += OnJumpItemClick;
        }

        public void Bind(object groupedSource, object jumpTiles)
        {
            _cvs.Source = groupedSource;
            _list.ItemsSource = _cvs.View;
            _jumpList.ItemsSource = jumpTiles;
        }

        public void Clear()
        {
            _pendingJumpGroup = null;
            _cvs.Source = null;
            _list.ItemsSource = null;
            _jumpList.ItemsSource = null;
        }

        public void ResetZoomedIn()
        {
            _zoom.IsZoomedInViewActive = true;
        }

        public bool TryHandleBackToZoomedIn()
        {
            if (_zoom.IsZoomedInViewActive)
            {
                return false;
            }

            _zoom.IsZoomedInViewActive = true;
            return true;
        }

        private void OnJumpItemClick(object sender, ItemClickEventArgs e)
        {
            var tile = e.ClickedItem as AlphabetJumpTile;
            if (tile == null || !tile.IsEnabled || tile.Group == null || tile.Group.Count == 0)
            {
                return;
            }

            _pendingJumpGroup = tile.Group;
            if (!_zoom.IsZoomedInViewActive)
            {
                _zoom.IsZoomedInViewActive = true;
                return;
            }

            ScheduleScrollToPendingGroup();
        }

        private void OnViewChangeStarted(object sender, SemanticZoomViewChangedEventArgs e)
        {
            if (e == null || e.IsSourceZoomedInView)
            {
                return;
            }

            AlphabetChatGroup pending = _pendingJumpGroup;
            if (pending != null && pending.Count > 0)
            {
                e.DestinationItem.Item = pending[0];
                return;
            }

            var tile = e.SourceItem?.Item as AlphabetJumpTile;
            if (tile?.Group != null && tile.Group.Count > 0)
            {
                e.DestinationItem.Item = tile.Group[0];
                _pendingJumpGroup = tile.Group;
            }
        }

        private void OnViewChangeCompleted(object sender, SemanticZoomViewChangedEventArgs e)
        {
            if (e == null || e.IsSourceZoomedInView || _pendingJumpGroup == null)
            {
                return;
            }

            ScheduleScrollToPendingGroup();
        }

        private void ScheduleScrollToPendingGroup()
        {
            AlphabetChatGroup group = _pendingJumpGroup;
            if (group == null || group.Count == 0)
            {
                return;
            }

            ChatItem first = group[0];
            _ = _list.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    _list.ScrollIntoView(first, ScrollIntoViewAlignment.Leading);
                }
                catch
                {
                }

                _pendingJumpGroup = null;
            });
        }
    }
}
