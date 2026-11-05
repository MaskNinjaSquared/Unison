using System;
using System.Collections.Generic;
using System.ComponentModel;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Status contact picker plumbing shared by the Unison / WhatsApp and Legacy overlays:
    /// A–Z zoom binding, re-checking the saved rows on open, and mirroring checks to the VM.
    /// </summary>
    internal sealed class StatusContactPickerHost
    {
        private readonly ListView _list;
        private readonly AlphabetSemanticZoomHost _zoom;
        private StatusComposerViewModel _vm;
        private bool _syncingSelection;

        public StatusContactPickerHost(SemanticZoom zoom, ListView list, GridView jumpList)
        {
            _list = list ?? throw new ArgumentNullException(nameof(list));
            _zoom = new AlphabetSemanticZoomHost(zoom, list, jumpList);
            _list.SelectionChanged += List_SelectionChanged;
        }

        public void Attach(StatusComposerViewModel vm)
        {
            Detach();
            _vm = vm;
            if (_vm != null)
            {
                _vm.PropertyChanged += Vm_PropertyChanged;
            }

            Rebind();
        }

        public void Detach()
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= Vm_PropertyChanged;
                _vm = null;
            }

            _zoom.Clear();
        }

        public bool TryHandleZoomBack() => _zoom.TryHandleBackToZoomedIn();

        private void Vm_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (string.Equals(e.PropertyName, nameof(StatusComposerViewModel.IsContactPickerOpen), StringComparison.Ordinal) ||
                string.Equals(e.PropertyName, nameof(StatusComposerViewModel.PickerGroupedContacts), StringComparison.Ordinal))
            {
                Rebind();
            }
        }

        private void Rebind()
        {
            if (_vm == null || !_vm.IsContactPickerOpen)
            {
                return;
            }

            _syncingSelection = true;
            try
            {
                _zoom.Bind(_vm.PickerGroupedContacts, _vm.PickerJumpTiles);
                _zoom.ResetZoomedIn();
                _list.SelectedItems.Clear();
                foreach (AlphabetChatGroup group in _vm.PickerGroupedContacts)
                {
                    foreach (ChatItem contact in group)
                    {
                        if (contact != null && _vm.IsPickerContactSelected(contact.JID))
                        {
                            _list.SelectedItems.Add(contact);
                        }
                    }
                }
            }
            finally
            {
                _syncingSelection = false;
            }
        }

        private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingSelection || _vm == null || !_vm.IsContactPickerOpen)
            {
                return;
            }

            var jids = new List<string>();
            foreach (object item in _list.SelectedItems)
            {
                if (item is ChatItem contact && !string.IsNullOrWhiteSpace(contact.JID))
                {
                    jids.Add(contact.JID);
                }
            }

            _vm.SetPickerSelection(jids);
        }
    }
}
