// SyncAgent/ScheduledAgent.cs
// WP8.1 Silverlight background scheduled task agent.
// Runs bidirectional sync every 30 minutes (WP8.1 minimum interval).
// Shares IsolatedStorageSettings with the main app — same keys.
// Self-contained — duplicates HTTP and storage logic to avoid
// cross-project DLL dependency issues on WP8.1.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Phone.Scheduler;
using Microsoft.Phone.Shell;
using Windows.Phone.PersonalInformation;

namespace SyncAgent
{
    public class ScheduledAgent : ScheduledTaskAgent
    {
        private static readonly IsolatedStorageSettings _settings =
            IsolatedStorageSettings.ApplicationSettings;

        private const string TokenUrl  = "https://oauth2.googleapis.com/token";
        private const string PeopleUrl = "https://people.googleapis.com/v1";

        protected override void OnInvoke(ScheduledTask task)
        {
            // Run async sync, then notify completion
            RunSyncAsync().ContinueWith(t =>
            {
                _settings["LastBgSync"] =
                    DateTime.Now.ToString("dd MMM yyyy HH:mm");
                _settings.Save();
                NotifyComplete();
            });
        }

        // ================================================================
        // MAIN SYNC — bidirectional, same logic as SyncManager
        // ================================================================
        private async Task RunSyncAsync()
        {
            try
            {
                string refreshToken = GetSetting("RefreshToken");
                string clientId     = GetSetting("ClientId");
                string clientSecret = GetSetting("ClientSecret");
                if (string.IsNullOrEmpty(refreshToken) ||
                    string.IsNullOrEmpty(clientId)) return;

                // Get access token
                string accessToken = await GetAccessTokenAsync(
                    clientId, clientSecret, refreshToken);
                if (string.IsNullOrEmpty(accessToken)) return;

                // Load sync state
                var state = LoadSyncState();

                // Fetch Google contacts
                var googleContacts = await FetchContactsAsync(accessToken);
                if (googleContacts == null) return;

                var googleById = new Dictionary<string, ContactData>();
                foreach (var gc in googleContacts)
                    if (!string.IsNullOrEmpty(gc.Id))
                        googleById[gc.Id] = gc;

                // Open contact store
                var store = await ContactStore.CreateOrOpenAsync(
                    ContactStoreSystemAccessMode.ReadWrite,
                    ContactStoreApplicationAccessMode.ReadOnly);

                // Read phone contacts
                var query   = store.CreateContactQuery();
                var phoneCs = await query.GetContactsAsync();
                var phoneById = new Dictionary<string, StoredContact>();
                foreach (var c in phoneCs)
                    if (!string.IsNullOrEmpty(c.RemoteId))
                        phoneById[c.RemoteId] = c;

                var newState = new Dictionary<string, SyncState>();
                int downloaded = 0, uploaded = 0, deleted = 0;

                // --------------------------------------------------------
                // PHASE 1: Process Google contacts
                // --------------------------------------------------------
                foreach (var gc in googleContacts)
                {
                    if (string.IsNullOrEmpty(gc.Id)) continue;

                    bool hasState    = state.ContainsKey(gc.Id);
                    var  savedState  = hasState ? state[gc.Id] : null;
                    bool gcChanged   = !hasState || savedState.ETag != gc.ETag;

                    StoredContact local = null;
                    phoneById.TryGetValue(gc.Id, out local);
                    bool existsLocally = local != null;

                    if (!existsLocally)
                    {
                        await SaveContactToStoreAsync(store, gc);
                        downloaded++;
                    }
                    else if (gcChanged)
                    {
                        var localProps = await local.GetPropertiesAsync();
                        string currentHash = ComputeHash(localProps);
                        bool   localChanged = hasState &&
                            !string.IsNullOrEmpty(savedState.LocalHash) &&
                            savedState.LocalHash != currentHash;

                        if (!localChanged)
                        {
                            // Only Google changed — download
                            await UpsertContactAsync(store, gc);
                            downloaded++;
                        }
                        else
                        {
                            // Both changed — Google wins
                            bool googleNewer = string.Compare(
                                gc.UpdateTime,
                                savedState != null ? savedState.UpdateTime : "",
                                StringComparison.Ordinal) > 0;

                            if (googleNewer)
                            {
                                await UpsertContactAsync(store, gc);
                                downloaded++;
                            }
                            else
                            {
                                // Local wins — upload
                                string etag = savedState != null
                                    ? savedState.ETag : "*";
                                await UploadContactAsync(
                                    local, gc.Id, etag,
                                    clientId, clientSecret, accessToken);
                                uploaded++;
                            }
                        }
                    }
                    else if (existsLocally)
                    {
                        // Google unchanged — check local
                        var localProps = await local.GetPropertiesAsync();
                        string currentHash = ComputeHash(localProps);
                        bool   localChanged = hasState &&
                            !string.IsNullOrEmpty(savedState.LocalHash) &&
                            savedState.LocalHash != currentHash;

                        if (localChanged)
                        {
                            string etag = savedState != null
                                ? savedState.ETag : "*";
                            await UploadContactAsync(
                                local, gc.Id, etag,
                                clientId, clientSecret, accessToken);
                            uploaded++;
                        }
                    }

                    // Compute updated local hash
                    string localHash = "";
                    var updatedPhone = await store.FindContactByRemoteIdAsync(gc.Id);
                    if (updatedPhone != null)
                    {
                        var p = await updatedPhone.GetPropertiesAsync();
                        localHash = ComputeHash(p);
                    }

                    newState[gc.Id] = new SyncState
                    {
                        ETag       = gc.ETag       ?? "",
                        UpdateTime = gc.UpdateTime ?? "",
                        LocalHash  = localHash
                    };
                }

                // --------------------------------------------------------
                // PHASE 2: Local contacts not in Google
                // --------------------------------------------------------
                foreach (var c in phoneCs)
                {
                    if (string.IsNullOrEmpty(c.RemoteId))
                    {
                        // New local — upload
                        string newId = await CreateContactOnGoogleAsync(
                            c, clientId, clientSecret, accessToken);
                        if (!string.IsNullOrEmpty(newId))
                        {
                            c.RemoteId = newId;
                            await c.SaveAsync();
                            uploaded++;
                            var up = await c.GetPropertiesAsync();
                            newState[newId] = new SyncState
                            {
                                ETag = "", UpdateTime = "",
                                LocalHash = ComputeHash(up)
                            };
                        }
                        continue;
                    }

                    if (!googleById.ContainsKey(c.RemoteId) &&
                        state.ContainsKey(c.RemoteId))
                    {
                        // Deleted from Google — delete locally
                        await store.DeleteContactAsync(
                            (await store.FindContactByRemoteIdAsync(
                                c.RemoteId))?.Id ?? "");
                        deleted++;
                    }
                }

                // --------------------------------------------------------
                // PHASE 3: Save state
                // --------------------------------------------------------
                SaveSyncState(newState);

                // Update tile
                if (downloaded + uploaded + deleted > 0)
                    UpdateTile(string.Format(
                        "↓{0} ↑{1} ✗{2}", downloaded, uploaded, deleted));
            }
            catch { }
        }

        // ================================================================
        // CONTACT STORE OPERATIONS
        // ================================================================
        private async Task SaveContactToStoreAsync(
            ContactStore store, ContactData gc)
        {
            var contact = new StoredContact(store);
            contact.RemoteId    = gc.Id;
            contact.DisplayName = BuildDisplayName(gc);
            var props = await contact.GetPropertiesAsync();
            SetProps(props, gc);
            await contact.SaveAsync();
        }

        private async Task UpsertContactAsync(
            ContactStore store, ContactData gc)
        {
            // Delete existing
            try
            {
                var existing = await store.FindContactByRemoteIdAsync(gc.Id);
                if (existing != null)
                    await store.DeleteContactAsync(existing.Id);
            }
            catch { }
            await SaveContactToStoreAsync(store, gc);
        }

        private void SetProps(IDictionary<string, object> props, ContactData gc)
        {
            Set(props, KnownContactProperties.GivenName,  gc.FirstName);
            Set(props, KnownContactProperties.FamilyName, gc.LastName);
            Set(props, KnownContactProperties.Nickname,   gc.Nickname);
            Set(props, KnownContactProperties.Notes,      gc.Notes);

            // Phones — up to 6 slots
            var phoneKeys = new[]
            {
                KnownContactProperties.MobileTelephone,
                KnownContactProperties.Telephone,
                KnownContactProperties.WorkTelephone,
                KnownContactProperties.AlternateMobileTelephone,
                KnownContactProperties.AlternateTelephone,
                KnownContactProperties.AlternateWorkTelephone
            };
            int slot = 0;
            foreach (var p in gc.Phones)
            {
                if (slot >= phoneKeys.Length) break;
                string t = (p.Type ?? "mobile").ToLower();
                string key;
                if (t.Contains("work"))
                    key = !props.ContainsKey(KnownContactProperties.WorkTelephone)
                        ? KnownContactProperties.WorkTelephone
                        : KnownContactProperties.AlternateWorkTelephone;
                else if (t.Contains("home"))
                    key = !props.ContainsKey(KnownContactProperties.Telephone)
                        ? KnownContactProperties.Telephone
                        : KnownContactProperties.AlternateTelephone;
                else
                    key = !props.ContainsKey(KnownContactProperties.MobileTelephone)
                        ? KnownContactProperties.MobileTelephone
                        : KnownContactProperties.AlternateMobileTelephone;
                if (!props.ContainsKey(key)) { props[key] = p.Number; slot++; }
            }

            // Emails
            bool emailSet = false, workSet = false, otherSet = false;
            foreach (var e in gc.Emails)
            {
                string t = (e.Type ?? "home").ToLower();
                if (t.Contains("work") && !workSet)
                { props[KnownContactProperties.WorkEmail] = e.Address; workSet = true; }
                else if (!emailSet)
                { props[KnownContactProperties.Email] = e.Address; emailSet = true; }
                else if (!otherSet)
                { props[KnownContactProperties.OtherEmail] = e.Address; otherSet = true; }
            }

            if (gc.Orgs.Count > 0)
            {
                Set(props, KnownContactProperties.CompanyName, gc.Orgs[0].Name);
                Set(props, KnownContactProperties.JobTitle,    gc.Orgs[0].Title);
            }
            if (gc.Urls.Count > 0)
                Set(props, KnownContactProperties.Url, gc.Urls[0].Value);
        }

        private void Set(IDictionary<string, object> props, string key, string val)
        {
            if (!string.IsNullOrEmpty(val)) props[key] = val;
        }

        private string BuildDisplayName(ContactData gc)
        {
            string dn = ((gc.FirstName ?? "") + " " + (gc.LastName ?? "")).Trim();
            if (string.IsNullOrEmpty(dn)) dn = gc.Nickname ?? "";
            if (string.IsNullOrEmpty(dn) && gc.Phones.Count > 0)
                dn = gc.Phones[0].Number;
            return string.IsNullOrEmpty(dn) ? "(no name)" : dn;
        }

        // ================================================================
        // UPLOAD / CREATE on Google
        // ================================================================
        private async Task UploadContactAsync(
            StoredContact c, string remoteId, string etag,
            string clientId, string clientSecret, string accessToken)
        {
            try
            {
                var props = await c.GetPropertiesAsync();
                string body = BuildPersonJson(props, c.DisplayName, etag);
                string id   = remoteId.StartsWith("people/")
                    ? remoteId : "people/" + remoteId;
                string url  = PeopleUrl + "/" + id +
                    ":updateContact?updatePersonFields=" +
                    "names,phoneNumbers,emailAddresses," +
                    "organizations,urls,biographies,nicknames";
                await PatchAsync(url, body, accessToken);
            }
            catch { }
        }

        private async Task<string> CreateContactOnGoogleAsync(
            StoredContact c, string clientId, string clientSecret,
            string accessToken)
        {
            try
            {
                var    props = await c.GetPropertiesAsync();
                string body  = BuildPersonJson(props, c.DisplayName, null);
                string json  = await PostJsonAsync(
                    PeopleUrl + "/people:createContact", body, accessToken);
                return GetJsonValue(json, "resourceName");
            }
            catch { return null; }
        }

        // ================================================================
        // JSON BUILDER — minimal for contact upload
        // ================================================================
        private string BuildPersonJson(
            IDictionary<string, object> props, string displayName, string etag)
        {
            var sb = new StringBuilder();
            sb.Append("{");

            if (etag != null)
            {
                sb.Append("\"etag\":"); AppendStr(sb, etag); sb.Append(",");
            }

            sb.Append("\"names\":[{\"givenName\":");
            AppendStr(sb, Prop(props, KnownContactProperties.GivenName));
            sb.Append(",\"familyName\":");
            AppendStr(sb, Prop(props, KnownContactProperties.FamilyName));
            sb.Append("}]");

            // Phones
            var phoneSlots = new[]
            {
                new { Key = KnownContactProperties.MobileTelephone,         Type = "mobile" },
                new { Key = KnownContactProperties.Telephone,               Type = "home" },
                new { Key = KnownContactProperties.WorkTelephone,           Type = "work" },
                new { Key = KnownContactProperties.AlternateMobileTelephone,Type = "mobile" },
                new { Key = KnownContactProperties.AlternateTelephone,      Type = "home" },
                new { Key = KnownContactProperties.AlternateWorkTelephone,  Type = "work" },
            };
            var phoneList = new List<string[]>();
            foreach (var slot in phoneSlots)
            {
                string num = Prop(props, slot.Key);
                if (!string.IsNullOrEmpty(num))
                    phoneList.Add(new[] { num, slot.Type });
            }
            if (phoneList.Count > 0)
            {
                sb.Append(",\"phoneNumbers\":[");
                for (int i = 0; i < phoneList.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"value\":"); AppendStr(sb, phoneList[i][0]);
                    sb.Append(",\"type\":"); AppendStr(sb, phoneList[i][1]);
                    sb.Append("}");
                }
                sb.Append("]");
            }

            // Emails
            var emailSlots = new[]
            {
                new { Key = KnownContactProperties.Email,      Type = "home" },
                new { Key = KnownContactProperties.WorkEmail,  Type = "work" },
                new { Key = KnownContactProperties.OtherEmail, Type = "other" },
            };
            var emailList = new List<string[]>();
            foreach (var slot in emailSlots)
            {
                string addr = Prop(props, slot.Key);
                if (!string.IsNullOrEmpty(addr))
                    emailList.Add(new[] { addr, slot.Type });
            }
            if (emailList.Count > 0)
            {
                sb.Append(",\"emailAddresses\":[");
                for (int i = 0; i < emailList.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"value\":"); AppendStr(sb, emailList[i][0]);
                    sb.Append(",\"type\":"); AppendStr(sb, emailList[i][1]);
                    sb.Append("}");
                }
                sb.Append("]");
            }

            // Org
            string company = Prop(props, KnownContactProperties.CompanyName);
            string title   = Prop(props, KnownContactProperties.JobTitle);
            if (!string.IsNullOrEmpty(company) || !string.IsNullOrEmpty(title))
            {
                sb.Append(",\"organizations\":[{\"name\":"); AppendStr(sb, company);
                sb.Append(",\"title\":"); AppendStr(sb, title); sb.Append("}]");
            }

            // Notes
            string notes = Prop(props, KnownContactProperties.Notes);
            if (!string.IsNullOrEmpty(notes))
            {
                sb.Append(",\"biographies\":[{\"value\":"); AppendStr(sb, notes);
                sb.Append(",\"contentType\":\"TEXT_PLAIN\"}]");
            }

            // URL
            string url = Prop(props, KnownContactProperties.Url);
            if (!string.IsNullOrEmpty(url))
            {
                sb.Append(",\"urls\":[{\"value\":"); AppendStr(sb, url);
                sb.Append(",\"type\":\"other\"}]");
            }

            sb.Append("}");
            return sb.ToString();
        }

        private string Prop(IDictionary<string, object> props, string key)
        {
            return props.ContainsKey(key) ? props[key] as string ?? "" : "";
        }

        private void AppendStr(StringBuilder sb, string s)
        {
            sb.Append("\"");
            if (!string.IsNullOrEmpty(s))
                sb.Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                           .Replace("\n", "\\n").Replace("\r", ""));
            sb.Append("\"");
        }

        // ================================================================
        // LOCAL HASH
        // ================================================================
        private string ComputeHash(IDictionary<string, object> props)
        {
            var sb = new StringBuilder();
            string[] keys = {
                KnownContactProperties.GivenName,
                KnownContactProperties.FamilyName,
                KnownContactProperties.Nickname,
                KnownContactProperties.Notes,
                KnownContactProperties.MobileTelephone,
                KnownContactProperties.Telephone,
                KnownContactProperties.WorkTelephone,
                KnownContactProperties.AlternateMobileTelephone,
                KnownContactProperties.AlternateTelephone,
                KnownContactProperties.AlternateWorkTelephone,
                KnownContactProperties.Email,
                KnownContactProperties.WorkEmail,
                KnownContactProperties.OtherEmail,
                KnownContactProperties.CompanyName,
                KnownContactProperties.JobTitle,
                KnownContactProperties.Url
            };
            foreach (var k in keys) { sb.Append(Prop(props, k)); sb.Append("|"); }
            string raw = sb.ToString();
            int hash = 17;
            foreach (char c in raw) hash = hash * 31 + c;
            return hash.ToString();
        }

        // ================================================================
        // HTTP
        // ================================================================
        private Task<string> PostAsync(string url, string body)
        {
            var tcs   = new TaskCompletionSource<string>();
            byte[] b  = Encoding.UTF8.GetBytes(body);
            var req   = (HttpWebRequest)WebRequest.Create(url);
            req.Method      = "POST";
            req.ContentType = "application/x-www-form-urlencoded";
            req.BeginGetRequestStream(ar =>
            {
                try
                {
                    using (var s = req.EndGetRequestStream(ar))
                        s.Write(b, 0, b.Length);
                    req.BeginGetResponse(ar2 =>
                    {
                        try
                        {
                            var resp = (HttpWebResponse)req.EndGetResponse(ar2);
                            using (var r = new StreamReader(resp.GetResponseStream()))
                                tcs.TrySetResult(r.ReadToEnd());
                        }
                        catch (WebException ex)
                        {
                            if (ex.Response != null)
                                try { using (var r = new StreamReader(ex.Response.GetResponseStream())) tcs.TrySetResult(r.ReadToEnd()); }
                                catch { tcs.TrySetResult(""); }
                            else tcs.TrySetResult("");
                        }
                        catch { tcs.TrySetResult(""); }
                    }, null);
                }
                catch { tcs.TrySetResult(""); }
            }, null);
            return tcs.Task;
        }

        private Task<string> PostJsonAsync(string url, string body, string token)
        {
            var tcs  = new TaskCompletionSource<string>();
            byte[] b = Encoding.UTF8.GetBytes(body);
            var req  = (HttpWebRequest)WebRequest.Create(url);
            req.Method      = "POST";
            req.ContentType = "application/json";
            req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            req.BeginGetRequestStream(ar =>
            {
                try
                {
                    using (var s = req.EndGetRequestStream(ar))
                        s.Write(b, 0, b.Length);
                    req.BeginGetResponse(ar2 =>
                    {
                        try
                        {
                            var resp = (HttpWebResponse)req.EndGetResponse(ar2);
                            using (var r = new StreamReader(resp.GetResponseStream()))
                                tcs.TrySetResult(r.ReadToEnd());
                        }
                        catch (WebException ex)
                        {
                            if (ex.Response != null)
                                try { using (var r = new StreamReader(ex.Response.GetResponseStream())) tcs.TrySetResult(r.ReadToEnd()); }
                                catch { tcs.TrySetResult(""); }
                            else tcs.TrySetResult("");
                        }
                        catch { tcs.TrySetResult(""); }
                    }, null);
                }
                catch { tcs.TrySetResult(""); }
            }, null);
            return tcs.Task;
        }

        private Task<string> PatchAsync(string url, string body, string token)
        {
            var tcs  = new TaskCompletionSource<string>();
            byte[] b = Encoding.UTF8.GetBytes(body);
            var req  = (HttpWebRequest)WebRequest.Create(url);
            req.Method      = "PATCH";
            req.ContentType = "application/json";
            req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            req.BeginGetRequestStream(ar =>
            {
                try
                {
                    using (var s = req.EndGetRequestStream(ar))
                        s.Write(b, 0, b.Length);
                    req.BeginGetResponse(ar2 =>
                    {
                        try
                        {
                            var resp = (HttpWebResponse)req.EndGetResponse(ar2);
                            using (var r = new StreamReader(resp.GetResponseStream()))
                                tcs.TrySetResult(r.ReadToEnd());
                        }
                        catch { tcs.TrySetResult(""); }
                    }, null);
                }
                catch { tcs.TrySetResult(""); }
            }, null);
            return tcs.Task;
        }

        private Task<string> GetAsync(string url, string token)
        {
            var tcs = new TaskCompletionSource<string>();
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Headers[HttpRequestHeader.Authorization] = "Bearer " + token;
            req.BeginGetResponse(ar =>
            {
                try
                {
                    var resp = (HttpWebResponse)req.EndGetResponse(ar);
                    using (var r = new StreamReader(resp.GetResponseStream()))
                        tcs.TrySetResult(r.ReadToEnd());
                }
                catch (WebException ex)
                {
                    if (ex.Response != null)
                        try { using (var r = new StreamReader(ex.Response.GetResponseStream())) tcs.TrySetResult(r.ReadToEnd()); }
                        catch { tcs.TrySetResult(""); }
                    else tcs.TrySetResult("");
                }
                catch { tcs.TrySetResult(""); }
            }, null);
            return tcs.Task;
        }

        // ================================================================
        // GOOGLE API
        // ================================================================
        private async Task<string> GetAccessTokenAsync(
            string clientId, string clientSecret, string refreshToken)
        {
            string body =
                "client_id="     + Uri.EscapeDataString(clientId) +
                "&client_secret="+ Uri.EscapeDataString(clientSecret) +
                "&refresh_token="+ Uri.EscapeDataString(refreshToken) +
                "&grant_type=refresh_token";
            string json = await PostAsync(TokenUrl, body);
            return GetJsonValue(json, "access_token");
        }

        private async Task<List<ContactData>> FetchContactsAsync(string accessToken)
        {
            var list = new List<ContactData>();
            string nextToken = "";
            while (true)
            {
                string url = PeopleUrl +
                    "/people/me/connections" +
                    "?personFields=names,nicknames,phoneNumbers,emailAddresses," +
                    "addresses,urls,birthdays,organizations,biographies,metadata" +
                    "&pageSize=100";
                if (!string.IsNullOrEmpty(nextToken))
                    url += "&pageToken=" + Uri.EscapeDataString(nextToken);

                string json = await GetAsync(url, accessToken);
                if (string.IsNullOrEmpty(json)) break;

                ParseContacts(json, list);
                nextToken = GetJsonValue(json, "nextPageToken");
                if (string.IsNullOrEmpty(nextToken)) break;
            }
            return list;
        }

        // ================================================================
        // MINIMAL JSON PARSER
        // ================================================================
        private string GetJsonValue(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return "";
            string search = "\"" + key + "\"";
            int ki = json.IndexOf(search);
            if (ki < 0) return "";
            int ci = json.IndexOf(':', ki + search.Length);
            if (ci < 0) return "";
            int start = json.IndexOf('"', ci + 1);
            if (start < 0) return "";
            int end = start + 1;
            while (end < json.Length)
            {
                if (json[end] == '"' && json[end - 1] != '\\') break;
                end++;
            }
            return json.Substring(start + 1, end - start - 1);
        }

        private string GetArray(string json, string key)
        {
            string search = "\"" + key + "\"";
            int ki = json.IndexOf(search);
            if (ki < 0) return null;
            int ci = json.IndexOf('[', ki + search.Length);
            if (ci < 0) return null;
            int depth = 0, i = ci;
            while (i < json.Length)
            {
                if (json[i] == '[') depth++;
                else if (json[i] == ']') { depth--; if (depth == 0) return json.Substring(ci, i - ci + 1); }
                i++;
            }
            return null;
        }

        private List<string> SplitObjects(string arr)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(arr)) return result;
            int i = 0;
            while (i < arr.Length)
            {
                if (arr[i] == '{')
                {
                    int depth = 0, start = i;
                    while (i < arr.Length)
                    {
                        if (arr[i] == '{') depth++;
                        else if (arr[i] == '}') { depth--; if (depth == 0) { result.Add(arr.Substring(start, i - start + 1)); break; } }
                        i++;
                    }
                }
                i++;
            }
            return result;
        }

        private void ParseContacts(string json, List<ContactData> list)
        {
            string arr = GetArray(json, "connections");
            if (arr == null) return;
            foreach (var obj in SplitObjects(arr))
            {
                var gc = ParsePerson(obj);
                if (gc != null) list.Add(gc);
            }
        }

        private ContactData ParsePerson(string json)
        {
            var gc  = new ContactData();
            gc.Id   = GetJsonValue(json, "resourceName");
            gc.ETag = GetJsonValue(json, "etag");

            // UpdateTime
            string srcArr = GetArray(json, "sources");
            if (srcArr != null)
                foreach (var s in SplitObjects(srcArr))
                {
                    string t = GetJsonValue(s, "updateTime");
                    if (string.Compare(t, gc.UpdateTime, StringComparison.Ordinal) > 0)
                        gc.UpdateTime = t;
                }

            // Names
            string namesArr = GetArray(json, "names");
            if (namesArr != null)
            {
                var objs = SplitObjects(namesArr);
                if (objs.Count > 0)
                {
                    gc.FirstName = GetJsonValue(objs[0], "givenName");
                    gc.LastName  = GetJsonValue(objs[0], "familyName");
                }
            }

            gc.Nickname = "";
            string nicksArr = GetArray(json, "nicknames");
            if (nicksArr != null)
            {
                var objs = SplitObjects(nicksArr);
                if (objs.Count > 0) gc.Nickname = GetJsonValue(objs[0], "value");
            }

            string biosArr = GetArray(json, "biographies");
            if (biosArr != null)
            {
                var objs = SplitObjects(biosArr);
                if (objs.Count > 0) gc.Notes = GetJsonValue(objs[0], "value");
            }

            // Phones
            string phonesArr = GetArray(json, "phoneNumbers");
            if (phonesArr != null)
                foreach (var o in SplitObjects(phonesArr))
                {
                    string num  = GetJsonValue(o, "value");
                    string type = GetJsonValue(o, "type");
                    if (string.IsNullOrEmpty(type)) type = "mobile";
                    if (!string.IsNullOrEmpty(num))
                        gc.Phones.Add(new PhoneData { Number = num, Type = type });
                }

            // Emails
            string emailsArr = GetArray(json, "emailAddresses");
            if (emailsArr != null)
                foreach (var o in SplitObjects(emailsArr))
                {
                    string addr = GetJsonValue(o, "value");
                    string type = GetJsonValue(o, "type");
                    if (string.IsNullOrEmpty(type)) type = "home";
                    if (!string.IsNullOrEmpty(addr))
                        gc.Emails.Add(new EmailData { Address = addr, Type = type });
                }

            // Orgs
            string orgsArr = GetArray(json, "organizations");
            if (orgsArr != null)
                foreach (var o in SplitObjects(orgsArr))
                    gc.Orgs.Add(new OrgData
                    {
                        Name  = GetJsonValue(o, "name"),
                        Title = GetJsonValue(o, "title")
                    });

            // URLs
            string urlsArr = GetArray(json, "urls");
            if (urlsArr != null)
                foreach (var o in SplitObjects(urlsArr))
                {
                    string v = GetJsonValue(o, "value");
                    if (!string.IsNullOrEmpty(v))
                        gc.Urls.Add(new UrlData { Value = v });
                }

            if (string.IsNullOrEmpty(gc.Id)) return null;
            return gc;
        }

        // ================================================================
        // SYNC STATE STORAGE — same format as SyncStateStorage.cs
        // ================================================================
        private const string StateKey = "SyncStateV2";

        private Dictionary<string, SyncState> LoadSyncState()
        {
            var result = new Dictionary<string, SyncState>();
            try
            {
                if (!_settings.Contains(StateKey)) return result;
                string raw = _settings[StateKey] as string;
                if (string.IsNullOrEmpty(raw)) return result;
                foreach (string line in raw.Split('\n'))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 4) continue;
                    string id = Unescape(parts[0]);
                    if (string.IsNullOrEmpty(id)) continue;
                    result[id] = new SyncState
                    {
                        ETag       = Unescape(parts[1]),
                        UpdateTime = Unescape(parts[2]),
                        LocalHash  = Unescape(parts[3])
                    };
                }
            }
            catch { }
            return result;
        }

        private void SaveSyncState(Dictionary<string, SyncState> state)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var kv in state)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    sb.Append(Escape(kv.Key));         sb.Append('\t');
                    sb.Append(Escape(kv.Value.ETag));  sb.Append('\t');
                    sb.Append(Escape(kv.Value.UpdateTime)); sb.Append('\t');
                    sb.Append(Escape(kv.Value.LocalHash));
                    sb.Append('\n');
                }
                _settings[StateKey] = sb.ToString();
                _settings.Save();
            }
            catch { }
        }

        private string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n");
        }

        private string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\t", "\t").Replace("\\n", "\n").Replace("\\\\", "\\");
        }

        // ================================================================
        // SETTINGS
        // ================================================================
        private string GetSetting(string key)
        {
            return _settings.Contains(key) ? _settings[key] as string : null;
        }

        // ================================================================
        // TILE UPDATE
        // ================================================================
        private void UpdateTile(string message)
        {
            try
            {
                var e = ShellTile.ActiveTiles.GetEnumerator();
                if (e.MoveNext())
                    e.Current.Update(new FlipTileData
                    {
                        BackContent = message,
                        BackTitle   = "Synced " +
                            DateTime.Now.ToString("HH:mm")
                    });
            }
            catch { }
        }
    }

    // ================================================================
    // SIMPLE DATA MODELS — local to agent, no dependency on main project
    // ================================================================
    public class ContactData
    {
        public string Id         { get; set; }
        public string ETag       { get; set; }
        public string UpdateTime { get; set; }
        public string FirstName  { get; set; }
        public string LastName   { get; set; }
        public string Nickname   { get; set; }
        public string Notes      { get; set; }
        public List<PhoneData> Phones { get; set; }
        public List<EmailData> Emails { get; set; }
        public List<OrgData>   Orgs   { get; set; }
        public List<UrlData>   Urls   { get; set; }
        public ContactData()
        {
            Id = ""; ETag = ""; UpdateTime = "";
            FirstName = ""; LastName = ""; Nickname = ""; Notes = "";
            Phones = new List<PhoneData>();
            Emails = new List<EmailData>();
            Orgs   = new List<OrgData>();
            Urls   = new List<UrlData>();
        }
    }

    public class PhoneData { public string Number { get; set; } public string Type { get; set; } }
    public class EmailData { public string Address { get; set; } public string Type { get; set; } }
    public class OrgData   { public string Name   { get; set; } public string Title { get; set; } }
    public class UrlData   { public string Value  { get; set; } }

    public class SyncState
    {
        public string ETag       { get; set; }
        public string UpdateTime { get; set; }
        public string LocalHash  { get; set; }
    }
}
