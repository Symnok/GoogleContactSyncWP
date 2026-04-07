// Services/SyncManager.cs
// Bidirectional sync for WP8.1 Silverlight.
// Ported from W10M GoogleContactSync SyncManager.
// Uses Windows.Phone.PersonalInformation instead of Windows.ApplicationModel.Contacts.

using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Phone.PersonalInformation;
using GoogleContactSyncWP.Helpers;
using GoogleContactSyncWP.Models;

namespace GoogleContactSyncWP.Services
{
    public class SyncResult
    {
        public int Downloaded         { get; set; }
        public int Uploaded           { get; set; }
        public int Deleted            { get; set; }
        public int ConflictsGoogleWon { get; set; }
        public int ConflictsLocalWon  { get; set; }

        public override string ToString()
        {
            return string.Format(
                "Downloaded: {0}, Uploaded: {1}, Deleted: {2}",
                Downloaded, Uploaded, Deleted);
        }
    }

    public class SyncManager
    {
        private readonly GoogleApiService   _api;
        private readonly ContactStoreService _store;
        private readonly string _clientId;
        private readonly string _clientSecret;

        public SyncManager(string clientId, string clientSecret)
        {
            _clientId     = clientId;
            _clientSecret = clientSecret;
            _api          = new GoogleApiService(clientId, clientSecret);
            _store        = new ContactStoreService();
        }

        // ================================================================
        // MAIN SYNC — bidirectional with conflict resolution
        // ================================================================
        public async Task<SyncResult> SyncAsync(Action<string> progress = null)
        {
            var result = new SyncResult();

            if (progress != null) progress("Loading sync state...");
            var state = SyncStateStorage.Load();

            if (progress != null) progress("Fetching contacts from Google...");
            var googleContacts = await _api.FetchAllContactsAsync(progress);
            var googleById     = new Dictionary<string, GoogleContact>();
            foreach (var gc in googleContacts)
                if (!string.IsNullOrEmpty(gc.Id))
                    googleById[gc.Id] = gc;

            if (progress != null) progress("Reading contacts from phone...");
            var phoneContacts = await _store.ReadAllContactsAsync(progress);
            var phoneById     = new Dictionary<string, StoredContact>();
            foreach (var c in phoneContacts)
                if (!string.IsNullOrEmpty(c.RemoteId))
                    phoneById[c.RemoteId] = c;

            var newState = new Dictionary<string, ContactSyncState>();

            // ============================================================
            // PHASE 1: Process all Google contacts
            // ============================================================
            if (progress != null)
                progress("Processing " + googleContacts.Count + " Google contacts...");

            foreach (var gc in googleContacts)
            {
                if (string.IsNullOrEmpty(gc.Id)) continue;

                bool hasState   = state.ContainsKey(gc.Id);
                var  savedState = hasState ? state[gc.Id] : null;

                bool googleChanged = !hasState ||
                                     savedState.ETag != gc.ETag;

                StoredContact localContact = null;
                phoneById.TryGetValue(gc.Id, out localContact);
                bool existsLocally = localContact != null;

                if (!existsLocally)
                {
                    // New from Google — download
                    if (progress != null)
                        progress("Downloading new: " + gc.FirstName + " " + gc.LastName);
                    await _store.UpsertContactAsync(gc);
                    result.Downloaded++;
                }
                else if (googleChanged)
                {
                    // Google changed — check local too
                    var localProps = await localContact.GetPropertiesAsync();
                    string currentLocalHash = ComputeLocalHash(localProps);
                    bool   localChanged     = hasState &&
                                             !string.IsNullOrEmpty(savedState.LocalHash) &&
                                             savedState.LocalHash != currentLocalHash;

                    if (!localChanged)
                    {
                        // Only Google changed → download
                        if (progress != null)
                            progress("Updating from Google: " +
                                gc.FirstName + " " + gc.LastName);
                        await _store.UpsertContactAsync(gc);
                        result.Downloaded++;
                    }
                    else
                    {
                        // Both changed — compare timestamps, Google wins
                        bool googleNewer = string.Compare(
                            gc.UpdateTime,
                            savedState != null ? savedState.UpdateTime : "",
                            StringComparison.Ordinal) > 0;

                        if (googleNewer)
                        {
                            await _store.UpsertContactAsync(gc);
                            result.Downloaded++;
                            result.ConflictsGoogleWon++;
                        }
                        else
                        {
                            // Local is newer — upload to Google
                            string etag = savedState != null ? savedState.ETag : "*";
                            bool ok = await UploadContactAsync(localContact, gc.Id, etag);
                            if (ok)
                            {
                                result.Uploaded++;
                                result.ConflictsLocalWon++;
                            }
                        }
                    }
                }
                else
                {
                    // Google unchanged — check if local changed
                    var localProps = await localContact.GetPropertiesAsync();
                    string currentLocalHash = ComputeLocalHash(localProps);
                    bool   localChanged     = hasState &&
                                             !string.IsNullOrEmpty(savedState.LocalHash) &&
                                             savedState.LocalHash != currentLocalHash;

                    if (localChanged)
                    {
                        // Local changed, Google didn't — upload
                        if (progress != null)
                            progress("Uploading change: " +
                                gc.FirstName + " " + gc.LastName);
                        string etag = savedState != null ? savedState.ETag : "*";
                        bool ok = await UploadContactAsync(localContact, gc.Id, etag);
                        if (ok) result.Uploaded++;
                    }
                }

                // Refresh local contact after possible upsert
                var updatedPhone = await _store.FindContactByRemoteIdAsync(gc.Id);
                string localHash = "";
                if (updatedPhone != null)
                {
                    var props = await updatedPhone.GetPropertiesAsync();
                    localHash = ComputeLocalHash(props);
                }

                newState[gc.Id] = new ContactSyncState
                {
                    ETag       = gc.ETag       ?? "",
                    UpdateTime = gc.UpdateTime ?? "",
                    LocalHash  = localHash
                };
            }

            // ============================================================
            // PHASE 2: Local contacts not in Google
            // ============================================================
            foreach (var c in phoneContacts)
            {
                if (string.IsNullOrEmpty(c.RemoteId))
                {
                    // New local contact — upload to Google
                    if (progress != null)
                        progress("Creating on Google: " + c.DisplayName);
                    var props = await c.GetPropertiesAsync();
                    string newId = await CreateContactOnGoogleAsync(props, c.DisplayName);
                    if (newId != null)
                    {
                        c.RemoteId = newId;
                        await c.SaveAsync();
                        result.Uploaded++;

                        var updated = await _store.FindContactByRemoteIdAsync(newId);
                        string lh = "";
                        if (updated != null)
                        {
                            var up = await updated.GetPropertiesAsync();
                            lh = ComputeLocalHash(up);
                        }
                        newState[newId] = new ContactSyncState
                        {
                            ETag       = "",
                            UpdateTime = "",
                            LocalHash  = lh
                        };
                    }
                    continue;
                }

                if (!googleById.ContainsKey(c.RemoteId) &&
                    state.ContainsKey(c.RemoteId))
                {
                    // Was synced before but deleted from Google — delete locally
                    if (progress != null)
                        progress("Deleting local: " + c.DisplayName);
                    await _store.DeleteContactAsync(c.RemoteId);
                    result.Deleted++;
                }
            }

            // ============================================================
            // PHASE 3: Save state
            // ============================================================
            SyncStateStorage.Save(newState);

            var s = System.IO.IsolatedStorage.IsolatedStorageSettings.ApplicationSettings;
            s["LastBgSync"] = DateTime.Now.ToString("dd MMM yyyy HH:mm");
            s.Save();

            if (progress != null)
                progress(string.Format(
                    "Done. ↓{0} ↑{1} ✗{2}",
                    result.Downloaded, result.Uploaded, result.Deleted));

            return result;
        }

        // ================================================================
        // UPLOAD contact to Google (update existing)
        // ================================================================
        private async Task<bool> UploadContactAsync(
            StoredContact c, string remoteId, string etag)
        {
            try
            {
                var props = await c.GetPropertiesAsync();
                string firstName = GetProp(props, KnownContactProperties.GivenName);
                string lastName  = GetProp(props, KnownContactProperties.FamilyName);
                string mobile    = GetProp(props, KnownContactProperties.MobileTelephone);
                string home      = GetProp(props, KnownContactProperties.Telephone);
                string work      = GetProp(props, KnownContactProperties.WorkTelephone);
                string email     = GetProp(props, KnownContactProperties.Email);
                string company   = GetProp(props, KnownContactProperties.CompanyName);
                string title     = GetProp(props, KnownContactProperties.JobTitle);
                string notes     = GetProp(props, KnownContactProperties.Notes);
                string url       = GetProp(props, KnownContactProperties.Url);

                var phones = BuildPhones(props);
                var emails = BuildEmails(props);
                var orgs   = BuildOrgs(props);
                var urls   = BuildUrls(props);

                string refreshToken = CredentialStorage.LoadToken();
                string accessToken  = await _api.GetAccessTokenAsync(refreshToken);

                return await _api.UpdateContactAsync(
                    remoteId, etag,
                    firstName, lastName, c.DisplayName,
                    notes, phones, emails, null, urls, orgs,
                    null, accessToken);
            }
            catch { return false; }
        }

        // ================================================================
        // CREATE contact on Google
        // ================================================================
        private async Task<string> CreateContactOnGoogleAsync(
            IDictionary<string, object> props, string displayName)
        {
            try
            {
                string firstName = GetProp(props, KnownContactProperties.GivenName);
                string lastName  = GetProp(props, KnownContactProperties.FamilyName);
                string mobile    = GetProp(props, KnownContactProperties.MobileTelephone);
                string home      = GetProp(props, KnownContactProperties.Telephone);
                string work      = GetProp(props, KnownContactProperties.WorkTelephone);
                string email     = GetProp(props, KnownContactProperties.Email);
                string company   = GetProp(props, KnownContactProperties.CompanyName);
                string title     = GetProp(props, KnownContactProperties.JobTitle);
                string notes     = GetProp(props, KnownContactProperties.Notes);
                string url       = GetProp(props, KnownContactProperties.Url);

                var phones = BuildPhones(props);
                var emails = BuildEmails(props);
                var orgs   = BuildOrgs(props);
                var urls   = BuildUrls(props);

                string refreshToken = CredentialStorage.LoadToken();
                string accessToken  = await _api.GetAccessTokenAsync(refreshToken);

                return await _api.CreateContactAsync(
                    firstName, lastName, displayName,
                    notes, phones, emails, null, urls, orgs,
                    null, accessToken);
            }
            catch { return null; }
        }

        // ================================================================
        // LOCAL HASH — same logic as W10M but using WP8.1 property keys
        // ================================================================
        public static string ComputeLocalHash(IDictionary<string, object> props)
        {
            var sb = new StringBuilder();
            sb.Append(Norm(GetProp(props, KnownContactProperties.GivenName)));   sb.Append("|");
            sb.Append(Norm(GetProp(props, KnownContactProperties.FamilyName)));  sb.Append("|");
            sb.Append(Norm(GetProp(props, KnownContactProperties.Nickname)));    sb.Append("|");
            sb.Append(Norm(GetProp(props, KnownContactProperties.Notes)));       sb.Append("|");
            // All phone slots
            sb.Append(NormPhone(GetProp(props, KnownContactProperties.MobileTelephone)));        sb.Append("|");
            sb.Append(NormPhone(GetProp(props, KnownContactProperties.Telephone)));              sb.Append("|");
            sb.Append(NormPhone(GetProp(props, KnownContactProperties.WorkTelephone)));          sb.Append("|");
            sb.Append(NormPhone(GetProp(props, KnownContactProperties.AlternateMobileTelephone)));sb.Append("|");
            sb.Append(NormPhone(GetProp(props, KnownContactProperties.AlternateTelephone)));     sb.Append("|");
            sb.Append(NormPhone(GetProp(props, KnownContactProperties.AlternateWorkTelephone))); sb.Append("|");
            // All email slots
            sb.Append(Norm(GetProp(props, KnownContactProperties.Email)));       sb.Append("|");
            sb.Append(Norm(GetProp(props, KnownContactProperties.WorkEmail)));   sb.Append("|");
            sb.Append(Norm(GetProp(props, KnownContactProperties.OtherEmail)));  sb.Append("|");
            // Org, URL
            sb.Append(Norm(GetProp(props, KnownContactProperties.CompanyName))); sb.Append("|");
            sb.Append(Norm(GetProp(props, KnownContactProperties.JobTitle)));    sb.Append("|");
            sb.Append(Norm(GetProp(props, KnownContactProperties.Url)));

            string raw = sb.ToString();
            int hash = 17;
            foreach (char ch in raw) hash = hash * 31 + ch;
            return hash.ToString();
        }

        // ================================================================
        // HELPERS
        // ================================================================
        private static string GetProp(IDictionary<string, object> props, string key)
        {
            return props.ContainsKey(key) ? props[key] as string ?? "" : "";
        }

        private static string Norm(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Trim().ToLower();
        }

        private static string NormPhone(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
                if (char.IsDigit(c) || c == '+') sb.Append(c);
            return sb.ToString();
        }

        private List<GPhone> BuildPhones(IDictionary<string, object> props)
        {
            var list = new List<GPhone>();
            AddPhone(list, GetProp(props, KnownContactProperties.MobileTelephone),        "mobile");
            AddPhone(list, GetProp(props, KnownContactProperties.Telephone),              "home");
            AddPhone(list, GetProp(props, KnownContactProperties.WorkTelephone),          "work");
            AddPhone(list, GetProp(props, KnownContactProperties.AlternateMobileTelephone),"mobile");
            AddPhone(list, GetProp(props, KnownContactProperties.AlternateTelephone),     "home");
            AddPhone(list, GetProp(props, KnownContactProperties.AlternateWorkTelephone), "work");
            return list;
        }

        private void AddPhone(List<GPhone> list, string number, string type)
        {
            if (!string.IsNullOrEmpty(number))
                list.Add(new GPhone { Number = number, Type = type });
        }

        private List<GEmail> BuildEmails(IDictionary<string, object> props)
        {
            var list = new List<GEmail>();
            string e  = GetProp(props, KnownContactProperties.Email);
            string ew = GetProp(props, KnownContactProperties.WorkEmail);
            string eo = GetProp(props, KnownContactProperties.OtherEmail);
            if (!string.IsNullOrEmpty(e))  list.Add(new GEmail { Address = e,  Type = "home" });
            if (!string.IsNullOrEmpty(ew)) list.Add(new GEmail { Address = ew, Type = "work" });
            if (!string.IsNullOrEmpty(eo)) list.Add(new GEmail { Address = eo, Type = "other" });
            return list;
        }

        private List<GOrg> BuildOrgs(IDictionary<string, object> props)
        {
            var list = new List<GOrg>();
            string c = GetProp(props, KnownContactProperties.CompanyName);
            string t = GetProp(props, KnownContactProperties.JobTitle);
            if (!string.IsNullOrEmpty(c) || !string.IsNullOrEmpty(t))
                list.Add(new GOrg { Name = c, Title = t });
            return list;
        }

        private List<GUrl> BuildUrls(IDictionary<string, object> props)
        {
            var list = new List<GUrl>();
            string u = GetProp(props, KnownContactProperties.Url);
            if (!string.IsNullOrEmpty(u)) list.Add(new GUrl { Value = u, Type = "other" });
            return list;
        }
    }
}
