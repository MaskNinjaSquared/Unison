using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Windows.ApplicationModel.Contacts;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Input;

namespace Unison.Uwp.Services
{
    public class LocalContactsService : ILocalContactsService
    {
        private readonly ILocalSettings _localSettings;

        /// <summary>
        /// Set once the address book turns out to be unreachable on this machine, so the failure
        /// is paid for once. The overlay refresh runs repeatedly, and the store does not become
        /// available later in the session - retrying only reproduces the same throw.
        /// </summary>
        private bool _storeUnavailable;

        private Dictionary<string, string> _lastPhoneLookup =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Full numbers from the user agenda only (not last-10 keys, not the Unison People list).
        /// </summary>
        private HashSet<string> _userAgendaExactPhones =
            new HashSet<string>(StringComparer.Ordinal);

        public LocalContactsService(ILocalSettings localSettings)
        {
            _localSettings = localSettings;
        }

        public async Task<Dictionary<string, string>> LoadPhoneContactNamesAsync()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var exactPhones = new HashSet<string>(StringComparer.Ordinal);

            if (_storeUnavailable)
            {
                return result;
            }

            try
            {
                ContactStore store = await OpenUserAgendaStoreAsync().ConfigureAwait(false);
                if (store == null)
                {
                    _storeUnavailable = true;
                    _lastPhoneLookup = result;
                    _userAgendaExactPhones = exactPhones;
                    Debug.WriteLine("[LocalContactsService] User agenda is not readable; Add contact stays available");
                    return result;
                }

                HashSet<string> unisonListIds =
                    await WindowsContactListPublisher.GetOwnedListIdsAsync(store, _localSettings)
                        .ConfigureAwait(false);

                IReadOnlyList<ContactList> lists;
                try
                {
                    lists = await store.FindContactListsAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[LocalContactsService] FindContactLists failed: " + ex.Message);
                    lists = null;
                }

                if (lists != null)
                {
                    foreach (ContactList list in lists)
                    {
                        if (list == null ||
                            (unisonListIds != null && unisonListIds.Contains(list.Id)))
                        {
                            continue;
                        }

                        await ReadUserListPhonesAsync(list, result, exactPhones).ConfigureAwait(false);
                    }
                }

                _lastPhoneLookup = result;
                _userAgendaExactPhones = exactPhones;
                Debug.WriteLine(
                    "[LocalContactsService] User agenda keys=" + result.Count +
                    " exactPhones=" + exactPhones.Count);
            }
            catch (Exception ex)
            {
                _storeUnavailable = true;
                Debug.WriteLine("[LocalContactsService] Failed to load contacts: " + ex.Message);
            }

            return result;
        }

        public bool IsPhoneInAddressBook(string phoneDigits)
        {
            if (_userAgendaExactPhones == null || _userAgendaExactPhones.Count == 0)
            {
                return false;
            }

            string digits = PhoneNumberHelper.NormalizePhoneDigits(phoneDigits);
            return !string.IsNullOrEmpty(digits) && _userAgendaExactPhones.Contains(digits);
        }

        public async Task<bool> ShowSystemContactCardAsync(
            string displayName,
            string phoneDigits,
            string photoUri)
        {
            string digits = PhoneNumberHelper.NormalizePhoneDigits(phoneDigits);
            if (string.IsNullOrEmpty(digits))
            {
                return false;
            }

            try
            {
                var contact = new Contact();
                string name = (displayName ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(name) && name.IndexOf('@') < 0)
                {
                    contact.Name = name;
                }

                string number = digits.StartsWith("+", StringComparison.Ordinal) ? digits : "+" + digits;
                contact.Phones.Add(new ContactPhone
                {
                    Number = number,
                    Kind = ContactPhoneKind.Mobile
                });

                await TryAttachThumbnailAsync(contact, photoUri).ConfigureAwait(false);

                var dispatcher = CoreApplication.MainView?.CoreWindow?.Dispatcher;
                if (dispatcher != null)
                {
                    // MenuFlyout / overflow close on the same tick as the click. The People
                    // mini-card is light-dismiss, so showing it before the flyout is gone
                    // closes it immediately (Windows 11). Idle waits for that teardown.
                    Exception showError = null;
                    await dispatcher.RunIdleAsync(_ =>
                    {
                        try
                        {
                            ShowCard(contact);
                        }
                        catch (Exception ex)
                        {
                            showError = ex;
                        }
                    });
                    if (showError != null)
                    {
                        throw showError;
                    }
                }
                else
                {
                    ShowCard(contact);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LocalContactsService] ShowSystemContactCard failed: " + ex.Message);
                try
                {
                    string uri = "ms-people:savetocontact?PhoneNumber=" +
                                 Uri.EscapeDataString("+" + digits);
                    if (!string.IsNullOrWhiteSpace(displayName))
                    {
                        uri += "&ContactName=" + Uri.EscapeDataString(displayName.Trim());
                    }

                    return await Launcher.LaunchUriAsync(new Uri(uri));
                }
                catch (Exception launchEx)
                {
                    Debug.WriteLine("[LocalContactsService] People protocol failed: " + launchEx.Message);
                    return false;
                }
            }
        }

        private static void ShowCard(Contact contact)
        {
            // Desktop / Continuum: full People window (not the light-dismiss flyout that
            // Windows 11 closes as soon as the menu or overlay refresh takes focus).
            if (ShouldUseFullContactCard())
            {
                ContactManager.ShowFullContactCard(
                    contact,
                    new FullContactCardOptions
                    {
                        DesiredRemainingView = ViewSizePreference.UseHalf
                    });
                return;
            }

            Rect selection = GetAnchorRect();
            if (ContactManager.IsShowContactCardSupported())
            {
                ContactManager.ShowContactCard(contact, selection, Placement.Default);
                return;
            }

            ContactManager.ShowFullContactCard(contact, new FullContactCardOptions());
        }

        private static bool ShouldUseFullContactCard()
        {
            try
            {
                if (!SystemInfoProvider.DetectIsMobile())
                {
                    return true;
                }

                return UIViewSettings.GetForCurrentView().UserInteractionMode == UserInteractionMode.Mouse;
            }
            catch
            {
                return !SystemInfoProvider.DetectIsMobile();
            }
        }

        /// <summary>
        /// Mini-card anchor. A 1×1 point in the window centre is treated as gone on Windows 11.
        /// Prefer the focused control (the Add-contact button / flyout item).
        /// </summary>
        private static Rect GetAnchorRect()
        {
            try
            {
                var focused = FocusManager.GetFocusedElement() as FrameworkElement;
                if (focused != null && focused.ActualWidth > 0 && focused.ActualHeight > 0)
                {
                    var origin = focused.TransformToVisual(null).TransformPoint(new Point(0, 0));
                    return new Rect(origin.X, origin.Y, focused.ActualWidth, focused.ActualHeight);
                }
            }
            catch
            {
            }

            try
            {
                var bounds = Window.Current?.Bounds ?? new Rect(0, 0, 320, 480);
                double width = Math.Min(280, Math.Max(80, bounds.Width * 0.4));
                double height = 48;
                return new Rect(
                    bounds.X + ((bounds.Width - width) / 2),
                    bounds.Y + (bounds.Height * 0.4),
                    width,
                    height);
            }
            catch
            {
                return new Rect(40, 120, 240, 48);
            }
        }

        private static async Task TryAttachThumbnailAsync(Contact contact, string photoUri)
        {
            if (contact == null || string.IsNullOrWhiteSpace(photoUri))
            {
                return;
            }

            try
            {
                string trimmed = photoUri.Trim();
                if (trimmed.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    contact.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(trimmed));
                    return;
                }

                StorageFile file = await StorageFile.GetFileFromPathAsync(trimmed);
                if (file != null)
                {
                    contact.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LocalContactsService] Thumbnail skipped: " + ex.Message);
            }
        }

        /// <summary>
        /// User agenda only. The app contact store is the Unison People export — using it as
        /// a fallback made every published 1:1 look "already saved".
        /// </summary>
        private static async Task<ContactStore> OpenUserAgendaStoreAsync()
        {
            try
            {
                return await ContactManager.RequestStoreAsync(ContactStoreAccessType.AllContactsReadOnly);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LocalContactsService] The system address book is not readable: " + ex.Message);
                return null;
            }
        }

        private static async Task ReadUserListPhonesAsync(
            ContactList list,
            Dictionary<string, string> overlayKeys,
            HashSet<string> exactPhones)
        {
            ContactReader reader;
            try
            {
                reader = list.GetContactReader();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[LocalContactsService] GetContactReader skipped: " + ex.Message);
                return;
            }

            if (reader == null)
            {
                return;
            }

            while (true)
            {
                ContactBatch batch;
                try
                {
                    batch = await reader.ReadBatchAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[LocalContactsService] ReadBatch skipped: " + ex.Message);
                    return;
                }

                if (batch == null || batch.Contacts == null || batch.Contacts.Count == 0)
                {
                    return;
                }

                foreach (Contact contact in batch.Contacts)
                {
                    AddUserContactPhones(contact, overlayKeys, exactPhones);
                }
            }
        }

        private static void AddUserContactPhones(
            Contact contact,
            Dictionary<string, string> overlayKeys,
            HashSet<string> exactPhones)
        {
            if (contact == null || WindowsContactListPublisher.IsUnisonRemoteId(contact.RemoteId))
            {
                return;
            }

            string displayName = BuildDisplayName(contact);
            if (string.IsNullOrWhiteSpace(displayName) || contact.Phones == null)
            {
                return;
            }

            string name = displayName.Trim();
            foreach (ContactPhone phone in contact.Phones)
            {
                string digits = PhoneNumberHelper.NormalizePhoneDigits(phone?.Number);
                if (string.IsNullOrEmpty(digits))
                {
                    continue;
                }

                exactPhones.Add(digits);

                foreach (string key in PhoneNumberHelper.BuildPhoneKeys(digits))
                {
                    if (!overlayKeys.ContainsKey(key))
                    {
                        overlayKeys[key] = name;
                    }
                }
            }
        }

        /// <summary>Kept for call sites that used the static API before PhoneNumberHelper.</summary>
        public static string NormalizePhoneDigits(string value)
            => PhoneNumberHelper.NormalizePhoneDigits(value);

        private static string BuildDisplayName(Contact contact)
        {
            if (contact == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(contact.DisplayName))
            {
                return contact.DisplayName;
            }

            string first = contact.FirstName?.Trim();
            string last = contact.LastName?.Trim();
            if (!string.IsNullOrEmpty(first) && !string.IsNullOrEmpty(last))
            {
                return first + " " + last;
            }

            return first ?? last;
        }

        public Task PublishAppContactsAsync(IReadOnlyList<AppContactExport> contacts)
        {
            return WindowsContactListPublisher.PublishAsync(_localSettings, contacts);
        }

        public Task ClearPublishedAppContactsAsync()
        {
            return WindowsContactListPublisher.ClearAsync(_localSettings);
        }
    }
}
