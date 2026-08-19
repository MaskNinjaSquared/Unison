using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Contacts;
using Windows.ApplicationModel.UserDataAccounts;
using Windows.Foundation.Metadata;
using Windows.Storage;

namespace Unison.Uwp.Services
{
    /// <summary>
    /// Unigram-shaped People export: a <see cref="UserDataAccount"/>, a contact list on that
    /// account, and annotations so the hub shows the names. Not the user agenda.
    /// </summary>
    internal static class WindowsContactListPublisher
    {
        private const string ListDisplayName = "Unison";

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        public static async Task PublishAsync(ILocalSettings settings, IReadOnlyList<AppContactExport> contacts)
        {
            if (settings == null)
            {
                return;
            }

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                ContactStore store = await OpenWriteStoreAsync().ConfigureAwait(false);
                if (store == null)
                {
                    return;
                }

                UserDataAccount account = await GetOrCreateAccountAsync(settings).ConfigureAwait(false);
                if (account == null)
                {
                    return;
                }

                ContactList list = await GetOrCreateContactListAsync(settings, store, account).ConfigureAwait(false);
                if (list == null)
                {
                    return;
                }

                await TryDeleteOrphanListsAsync(store, list.Id).ConfigureAwait(false);

                ContactAnnotationList annotations =
                    await GetOrCreateAnnotationListAsync(settings, account).ConfigureAwait(false);

                var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int count = contacts != null ? contacts.Count : 0;
                for (int i = 0; i < count; i++)
                {
                    AppContactExport item = contacts[i];
                    if (item == null || string.IsNullOrWhiteSpace(item.RemoteId))
                    {
                        continue;
                    }

                    string remoteId = ToRemoteId(item.RemoteId);
                    if (string.IsNullOrEmpty(remoteId) || !wanted.Add(remoteId))
                    {
                        continue;
                    }

                    try
                    {
                        await UpsertAsync(list, annotations, item, remoteId).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("[WindowsContactList] Upsert skipped " + remoteId + ": " + ex.Message);
                    }
                }

                Debug.WriteLine("[WindowsContactList] Published " + wanted.Count + " contacts");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WindowsContactList] Publish failed: " + ex.Message);
            }
            finally
            {
                Gate.Release();
            }
        }

        public static async Task ClearAsync(ILocalSettings settings)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                UserDataAccount account = await TryGetStoredAccountAsync(settings).ConfigureAwait(false);
                if (account != null)
                {
                    await account.DeleteAsync();
                    Debug.WriteLine("[WindowsContactList] Deleted Unison UserDataAccount");
                }

                ContactStore store = await OpenWriteStoreAsync().ConfigureAwait(false);
                if (store != null)
                {
                    await TryDeleteOrphanListsAsync(store, keepListId: null).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WindowsContactList] Clear failed: " + ex.Message);
            }
            finally
            {
                ClearStoredIds(settings);
                Gate.Release();
            }
        }

        private static async Task UpsertAsync(
            ContactList list,
            ContactAnnotationList annotations,
            AppContactExport item,
            string remoteId)
        {
            Contact contact = null;
            try
            {
                contact = await list.GetContactFromRemoteIdAsync(remoteId);
            }
            catch
            {
            }

            if (contact == null)
            {
                contact = new Contact();
            }

            string name = (item.DisplayName ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(name) || name.IndexOf('@') >= 0)
            {
                string digits = PhoneNumberHelper.NormalizePhoneDigits(item.PhoneDigits);
                name = string.IsNullOrEmpty(digits) ? remoteId : "+" + digits;
            }

            contact.FirstName = name;
            contact.LastName = string.Empty;
            contact.RemoteId = remoteId;

            string phone = PhoneNumberHelper.NormalizePhoneDigits(item.PhoneDigits);
            if (!string.IsNullOrEmpty(phone))
            {
                string number = phone.StartsWith("+", StringComparison.Ordinal) ? phone : "+" + phone;
                ContactPhone existing = contact.Phones.Count > 0 ? contact.Phones[0] : null;
                if (existing == null)
                {
                    contact.Phones.Add(new ContactPhone
                    {
                        Kind = ContactPhoneKind.Mobile,
                        Number = number
                    });
                }
                else
                {
                    existing.Kind = ContactPhoneKind.Mobile;
                    existing.Number = number;
                }
            }

            await TryAttachPictureAsync(contact, item.PhotoUri).ConfigureAwait(false);
            await list.SaveContactAsync(contact);

            if (annotations != null)
            {
                await TryAnnotateAsync(annotations, contact).ConfigureAwait(false);
            }
        }

        private static async Task TryAnnotateAsync(ContactAnnotationList annotations, Contact contact)
        {
            ContactAnnotation annotation = null;
            try
            {
                IReadOnlyList<ContactAnnotation> found =
                    await annotations.FindAnnotationsByRemoteIdAsync(contact.RemoteId);
                if (found != null && found.Count > 0)
                {
                    annotation = found[0];
                }
            }
            catch
            {
            }

            if (annotation == null)
            {
                annotation = new ContactAnnotation();
            }

            annotation.ContactId = contact.Id;
            annotation.RemoteId = contact.RemoteId;
            annotation.SupportedOperations =
                ContactAnnotationOperations.ContactProfile |
                ContactAnnotationOperations.Message |
                ContactAnnotationOperations.AudioCall;

            if (ApiInformation.IsApiContractPresent("Windows.Foundation.UniversalApiContract", 5))
            {
                annotation.SupportedOperations |= ContactAnnotationOperations.Share;
            }

            if (annotation.ProviderProperties.Count == 0)
            {
                string appId = Package.Current.Id.FamilyName + "!App";
                annotation.ProviderProperties.Add("ContactPanelAppID", appId);
                annotation.ProviderProperties.Add("ContactShareAppID", appId);
            }

            try
            {
                await annotations.TrySaveAnnotationAsync(annotation);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WindowsContactList] Annotation skipped: " + ex.Message);
            }
        }

        private static async Task<UserDataAccount> GetOrCreateAccountAsync(ILocalSettings settings)
        {
            UserDataAccountStore accountStore = await OpenAccountStoreAsync().ConfigureAwait(false);
            if (accountStore == null)
            {
                return null;
            }

            UserDataAccount account = await TryGetStoredAccountAsync(settings, accountStore).ConfigureAwait(false);
            if (account != null)
            {
                return account;
            }

            ClearStoredIds(settings);
            try
            {
                account = await accountStore.CreateAccountAsync(ListDisplayName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WindowsContactList] CreateAccount failed: " + ex.Message);
                return null;
            }

            if (account != null)
            {
                settings.Set(LocalSettingsConstants.PublishWindowsUserDataAccountId, account.Id);
            }

            return account;
        }

        private static Task<UserDataAccount> TryGetStoredAccountAsync(ILocalSettings settings)
        {
            return TryGetStoredAccountAsync(settings, accountStore: null);
        }

        private static async Task<UserDataAccount> TryGetStoredAccountAsync(
            ILocalSettings settings,
            UserDataAccountStore accountStore)
        {
            string id = ReadId(settings, LocalSettingsConstants.PublishWindowsUserDataAccountId);
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            if (accountStore == null)
            {
                accountStore = await OpenAccountStoreAsync().ConfigureAwait(false);
                if (accountStore == null)
                {
                    return null;
                }
            }

            try
            {
                return await accountStore.GetAccountAsync(id);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WindowsContactList] Stored account missing: " + ex.Message);
                return null;
            }
        }

        private static async Task<ContactList> GetOrCreateContactListAsync(
            ILocalSettings settings,
            ContactStore store,
            UserDataAccount account)
        {
            string id = ReadId(settings, LocalSettingsConstants.PublishWindowsContactListId);
            ContactList list = null;
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    list = await store.GetContactListAsync(id);
                }
                catch
                {
                }
            }

            if (list == null)
            {
                try
                {
                    list = await store.CreateContactListAsync(ListDisplayName, account.Id);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WindowsContactList] CreateContactList failed: " + ex.Message);
                    return null;
                }

                if (list != null)
                {
                    settings.Set(LocalSettingsConstants.PublishWindowsContactListId, list.Id);
                }
            }

            if (list == null)
            {
                return null;
            }

            list.DisplayName = ListDisplayName;
            list.OtherAppReadAccess = ContactListOtherAppReadAccess.SystemOnly;
            list.OtherAppWriteAccess = ContactListOtherAppWriteAccess.None;
            await list.SaveAsync();
            return list;
        }

        private static async Task<ContactAnnotationList> GetOrCreateAnnotationListAsync(
            ILocalSettings settings,
            UserDataAccount account)
        {
            ContactAnnotationStore store;
            try
            {
                store = await ContactManager.RequestAnnotationStoreAsync(
                    ContactAnnotationStoreAccessType.AppAnnotationsReadWrite);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WindowsContactList] Annotation store unavailable: " + ex.Message);
                return null;
            }

            if (store == null)
            {
                return null;
            }

            string id = ReadId(settings, LocalSettingsConstants.PublishWindowsAnnotationListId);
            ContactAnnotationList list = null;
            if (!string.IsNullOrEmpty(id))
            {
                try
                {
                    list = await store.GetAnnotationListAsync(id);
                }
                catch
                {
                }
            }

            if (list == null)
            {
                try
                {
                    list = await store.CreateAnnotationListAsync(account.Id);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WindowsContactList] CreateAnnotationList(account) failed: " + ex.Message);
                    try
                    {
                        list = await store.CreateAnnotationListAsync();
                    }
                    catch (Exception fallbackEx)
                    {
                        Debug.WriteLine("[WindowsContactList] CreateAnnotationList failed: " + fallbackEx.Message);
                        return null;
                    }
                }

                if (list != null)
                {
                    settings.Set(LocalSettingsConstants.PublishWindowsAnnotationListId, list.Id);
                }
            }

            return list;
        }

        private static async Task TryDeleteOrphanListsAsync(ContactStore store, string keepListId)
        {
            IReadOnlyList<ContactList> lists;
            try
            {
                lists = await store.FindContactListsAsync();
            }
            catch
            {
                return;
            }

            if (lists == null)
            {
                return;
            }

            foreach (ContactList list in lists)
            {
                if (list == null ||
                    !string.Equals(list.DisplayName, ListDisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(keepListId) &&
                    string.Equals(list.Id, keepListId, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    await list.DeleteAsync();
                    Debug.WriteLine("[WindowsContactList] Deleted orphan People list " + list.Id);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WindowsContactList] Orphan list delete skipped: " + ex.Message);
                }
            }
        }

        private static async Task<UserDataAccountStore> OpenAccountStoreAsync()
        {
            try
            {
                return await UserDataAccountManager.RequestStoreAsync(
                    UserDataAccountStoreAccessType.AppAccountsReadWrite);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WindowsContactList] UserDataAccount store unavailable: " + ex.Message);
                return null;
            }
        }

        private static async Task<ContactStore> OpenWriteStoreAsync()
        {
            try
            {
                return await ContactManager.RequestStoreAsync(ContactStoreAccessType.AppContactsReadWrite);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WindowsContactList] App contact store unavailable: " + ex.Message);
                return null;
            }
        }

        private static async Task TryAttachPictureAsync(Contact contact, string photoUri)
        {
            if (contact == null || string.IsNullOrWhiteSpace(photoUri))
            {
                return;
            }

            try
            {
                StorageFile file = await OpenPhotoFileAsync(photoUri.Trim()).ConfigureAwait(false);
                if (file == null)
                {
                    return;
                }

                // Unigram: SourceDisplayPicture = StorageFile from a local path, not Thumbnail.
                contact.SourceDisplayPicture = file;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WindowsContactList] Picture skipped: " + ex.Message);
            }
        }

        private static async Task<StorageFile> OpenPhotoFileAsync(string trimmed)
        {
            if (trimmed.StartsWith("ms-appdata:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase))
            {
                return await StorageFile.GetFileFromApplicationUriAsync(new Uri(trimmed));
            }

            if (trimmed.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                return await StorageFile.GetFileFromApplicationUriAsync(new Uri(trimmed));
            }

            if (trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return await StorageFile.GetFileFromPathAsync(trimmed);
        }

        /// <summary>
        /// People <c>SaveContactAsync</c> rejects a RemoteId with <c>@</c>. Unigram uses
        /// <c>u</c> + numeric id; we keep the JID without the at-sign.
        /// </summary>
        internal static string ToRemoteId(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            return "w" + jid.Trim().Replace('@', '.');
        }

        internal static bool IsUnisonRemoteId(string remoteId)
        {
            if (string.IsNullOrWhiteSpace(remoteId) ||
                remoteId.Length < 2 ||
                (remoteId[0] != 'w' && remoteId[0] != 'W'))
            {
                return false;
            }

            return remoteId.IndexOf(".s.whatsapp.net", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   remoteId.EndsWith(".lid", StringComparison.OrdinalIgnoreCase);
        }

        internal static async Task<HashSet<string>> GetOwnedListIdsAsync(
            ContactStore store,
            ILocalSettings settings)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (store == null)
            {
                return ids;
            }

            string ourList = ReadId(settings, LocalSettingsConstants.PublishWindowsContactListId);
            if (!string.IsNullOrEmpty(ourList))
            {
                ids.Add(ourList);
            }

            string ourAccount = ReadId(settings, LocalSettingsConstants.PublishWindowsUserDataAccountId);
            IReadOnlyList<ContactList> lists;
            try
            {
                lists = await store.FindContactListsAsync();
            }
            catch
            {
                return ids;
            }

            if (lists == null)
            {
                return ids;
            }

            foreach (ContactList list in lists)
            {
                if (list == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(ourList) &&
                    string.Equals(list.Id, ourList, StringComparison.Ordinal))
                {
                    ids.Add(list.Id);
                    continue;
                }

                if (!string.IsNullOrEmpty(ourAccount) &&
                    string.Equals(list.UserDataAccountId, ourAccount, StringComparison.Ordinal))
                {
                    ids.Add(list.Id);
                    continue;
                }

                if (string.Equals(list.DisplayName, ListDisplayName, StringComparison.OrdinalIgnoreCase) &&
                    list.OtherAppWriteAccess == ContactListOtherAppWriteAccess.None)
                {
                    ids.Add(list.Id);
                }
            }

            return ids;
        }

        private static string ReadId(ILocalSettings settings, string key)
        {
            if (settings == null)
            {
                return null;
            }

            string value = settings.Get<string>(key);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static void ClearStoredIds(ILocalSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            settings.Remove(LocalSettingsConstants.PublishWindowsUserDataAccountId);
            settings.Remove(LocalSettingsConstants.PublishWindowsContactListId);
            settings.Remove(LocalSettingsConstants.PublishWindowsAnnotationListId);
        }
    }
}
