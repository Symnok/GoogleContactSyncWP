// Services/ContactStoreService.cs
// Uses Windows.Phone.PersonalInformation.ContactStore for silent batch writes.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Phone.PersonalInformation;
using GoogleContactSyncWP.Models;
using GoogleContactSyncWP.Helpers;

namespace GoogleContactSyncWP.Services
{
    public class ContactStoreService
    {
        private ContactStore _store;

        private async Task<ContactStore> GetStoreAsync()
        {
            if (_store == null)
                _store = await ContactStore.CreateOrOpenAsync(
                    ContactStoreSystemAccessMode.ReadWrite,
                    ContactStoreApplicationAccessMode.ReadOnly);
            return _store;
        }

        // ================================================================
        // SYNC ALL — clear and rewrite all contacts silently
        // ================================================================
        public async Task<int> SyncAllAsync(
            List<GoogleContact> contacts,
            Action<string> progress = null,
            bool skipClear = false)
        {
            var store = await GetStoreAsync();
            if (progress != null) progress("Store opened.");

            // Clear only if skipClear is false
            if (!skipClear)
            {
                if (progress != null) progress("Clearing old contacts...");
                try
                {
                    var query    = store.CreateContactQuery();
                    var existing = await query.GetContactsAsync();
                    if (progress != null)
                        progress("Found " + existing.Count + " existing.");
                    int del = 0;
                    foreach (var c in existing)
                    {
                        await store.DeleteContactAsync(c.Id);
                        del++;
                    }
                    if (progress != null) progress("Cleared " + del + " contacts.");
                }
                catch (Exception ex)
                {
                    if (progress != null) progress("Clear error: " + ex.Message);
                }
            }
            else
            {
                if (progress != null) progress("Skipping clear (first install).");
            }

            // Save new contacts
            if (progress != null)
                progress("Saving " + contacts.Count + " contacts...");

            int saved    = 0;
            int failed   = 0;
            var newEtags = new Dictionary<string, string>();

            foreach (var gc in contacts)
            {
                try
                {
                    await SaveContactAsync(store, gc);
                    if (!string.IsNullOrEmpty(gc.Id))
                        newEtags[gc.Id] = gc.ETag;
                    saved++;
                    if (saved % 25 == 0 && progress != null)
                        progress("Saved " + saved + " / " + contacts.Count + "...");
                }
                catch (Exception ex)
                {
                    failed++;
                    if (failed <= 3 && progress != null)
                        progress("Save error: " + ex.Message);
                }
            }

            ETagStorage.SaveAll(newEtags);

            if (progress != null)
                progress("Done. " + saved + " saved, " + failed + " failed.");

            return saved;
        }

        // ================================================================
        // UPSERT — update or insert single contact
        // ================================================================
        public async Task UpsertContactAsync(GoogleContact gc)
        {
            var store = await GetStoreAsync();

            if (!string.IsNullOrEmpty(gc.Id))
            {
                try
                {
                    var existing = await store.FindContactByRemoteIdAsync(gc.Id);
                    if (existing != null)
                        await store.DeleteContactAsync(existing.Id);
                }
                catch { }
            }

            await SaveContactAsync(store, gc);
        }

        // ================================================================
        // DELETE by remote ID
        // ================================================================
        public async Task DeleteContactAsync(string remoteId)
        {
            if (string.IsNullOrEmpty(remoteId)) return;
            try
            {
                var store    = await GetStoreAsync();
                var existing = await store.FindContactByRemoteIdAsync(remoteId);
                if (existing != null)
                    await store.DeleteContactAsync(existing.Id);
            }
            catch { }
        }

        // ================================================================
        // READ ALL contacts from store
        // ================================================================
        public async Task<List<StoredContact>> ReadAllContactsAsync(
            Action<string> progress = null)
        {
            var store  = await GetStoreAsync();
            var query  = store.CreateContactQuery();
            var result = await query.GetContactsAsync();
            if (progress != null)
                progress("Read " + result.Count + " contacts from phone.");
            return new List<StoredContact>(result);
        }

        // ================================================================
        // FIND contact by RemoteId
        // ================================================================
        public async Task<StoredContact> FindContactByRemoteIdAsync(string remoteId)
        {
            if (string.IsNullOrEmpty(remoteId)) return null;
            try
            {
                var store = await GetStoreAsync();
                return await store.FindContactByRemoteIdAsync(remoteId);
            }
            catch { return null; }
        }

        // ================================================================
        // PRIVATE — write one contact to store
        // ================================================================
        private async Task SaveContactAsync(ContactStore store, GoogleContact gc)
        {
            var contact = new StoredContact(store);

            if (!string.IsNullOrEmpty(gc.Id))
                contact.RemoteId = gc.Id;

            // Display name
            string displayName = ((gc.FirstName ?? "") + " " +
                                  (gc.LastName  ?? "")).Trim();
            if (string.IsNullOrEmpty(displayName)) displayName = gc.Nickname ?? "";
            if (string.IsNullOrEmpty(displayName) && gc.Phones.Count > 0)
                displayName = gc.Phones[0].Number;
            if (string.IsNullOrEmpty(displayName)) displayName = "(no name)";
            contact.DisplayName = displayName;

            var props = await contact.GetPropertiesAsync();

            if (!string.IsNullOrEmpty(gc.FirstName))
                props[KnownContactProperties.GivenName]  = gc.FirstName;
            if (!string.IsNullOrEmpty(gc.LastName))
                props[KnownContactProperties.FamilyName] = gc.LastName;
            if (!string.IsNullOrEmpty(gc.Nickname))
                props[KnownContactProperties.Nickname]   = gc.Nickname;
            if (!string.IsNullOrEmpty(gc.Notes))
                props[KnownContactProperties.Notes]      = gc.Notes;

            // Phones — WP8.1 has 6 phone slots
            // Primary: MobileTelephone, Telephone, WorkTelephone
            // Alternate: AlternateMobileTelephone, AlternateTelephone, AlternateWorkTelephone
            var phoneKeys = new[]
            {
                KnownContactProperties.MobileTelephone,
                KnownContactProperties.Telephone,
                KnownContactProperties.WorkTelephone,
                KnownContactProperties.AlternateMobileTelephone,
                KnownContactProperties.AlternateTelephone,
                KnownContactProperties.AlternateWorkTelephone
            };
            int phoneSlot = 0;
            foreach (var p in gc.Phones)
            {
                if (string.IsNullOrEmpty(p.Number)) continue;
                if (phoneSlot >= phoneKeys.Length) break;

                string t = (p.Type ?? "").ToLower();
                // Try to match type to slot, otherwise use next available
                string key;
                if (t.Contains("work"))
                {
                    if (!props.ContainsKey(KnownContactProperties.WorkTelephone))
                        key = KnownContactProperties.WorkTelephone;
                    else if (!props.ContainsKey(KnownContactProperties.AlternateWorkTelephone))
                        key = KnownContactProperties.AlternateWorkTelephone;
                    else
                        key = phoneKeys[phoneSlot];
                }
                else if (t.Contains("home"))
                {
                    if (!props.ContainsKey(KnownContactProperties.Telephone))
                        key = KnownContactProperties.Telephone;
                    else if (!props.ContainsKey(KnownContactProperties.AlternateTelephone))
                        key = KnownContactProperties.AlternateTelephone;
                    else
                        key = phoneKeys[phoneSlot];
                }
                else
                {
                    if (!props.ContainsKey(KnownContactProperties.MobileTelephone))
                        key = KnownContactProperties.MobileTelephone;
                    else if (!props.ContainsKey(KnownContactProperties.AlternateMobileTelephone))
                        key = KnownContactProperties.AlternateMobileTelephone;
                    else
                        key = phoneKeys[phoneSlot];
                }

                if (!props.ContainsKey(key))
                {
                    props[key] = p.Number;
                    phoneSlot++;
                }
            }

            // Emails — WP8.1 has: Email, WorkEmail, OtherEmail
            bool emailSet      = false;
            bool workEmailSet  = false;
            bool otherEmailSet = false;
            foreach (var em in gc.Emails)
            {
                if (string.IsNullOrEmpty(em.Address)) continue;
                string t = (em.Type ?? "").ToLower();
                if (t.Contains("work") && !workEmailSet)
                { props[KnownContactProperties.WorkEmail] = em.Address; workEmailSet = true; }
                else if (!emailSet)
                { props[KnownContactProperties.Email] = em.Address; emailSet = true; }
                else if (!otherEmailSet)
                { props[KnownContactProperties.OtherEmail] = em.Address; otherEmailSet = true; }
            }

            // Organization
            if (gc.Organizations.Count > 0)
            {
                if (!string.IsNullOrEmpty(gc.Organizations[0].Name))
                    props[KnownContactProperties.CompanyName] = gc.Organizations[0].Name;
                if (!string.IsNullOrEmpty(gc.Organizations[0].Title))
                    props[KnownContactProperties.JobTitle]    = gc.Organizations[0].Title;
            }

            // Website
            if (gc.Urls.Count > 0 && !string.IsNullOrEmpty(gc.Urls[0].Value))
                props[KnownContactProperties.Url] = gc.Urls[0].Value;

            // Birthday
            if (gc.Birthday != null && gc.Birthday.Month > 0 && gc.Birthday.Day > 0)
            {
                int year = gc.Birthday.Year ?? 1900;
                try
                {
                    props[KnownContactProperties.Birthdate] =
                        new DateTimeOffset(new DateTime(
                            year, gc.Birthday.Month, gc.Birthday.Day));
                }
                catch { }
            }

            await contact.SaveAsync();
        }
    }
}
