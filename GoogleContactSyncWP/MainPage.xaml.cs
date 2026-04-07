using System;
using System.Collections.Generic;
using System.IO;
using System.IO.IsolatedStorage;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Tasks;
using GoogleContactSyncWP.Helpers;
using GoogleContactSyncWP.Services;
using GoogleContactSyncWP.Models;
using Microsoft.Phone.Scheduler;

namespace GoogleContactSyncWP
{
    public partial class MainPage : PhoneApplicationPage
    {
        private GoogleApiService _api;
        private StringBuilder    _log = new StringBuilder();

        public MainPage()
        {
            InitializeComponent();
            Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Show previous crash if any
            var settings = IsolatedStorageSettings.ApplicationSettings;
            if (settings.Contains("LastCrash"))
            {
                string crash = settings["LastCrash"] as string;
                settings.Remove("LastCrash");
                settings.Save();
                Log("=== PREVIOUS CRASH ===");
                Log(crash);
                Log("=== END CRASH ===");
            }

            string savedId     = CredentialStorage.LoadClientId();
            string savedSecret = CredentialStorage.LoadClientSecret();
            TxtClientId.Text         = savedId     ?? "";
            TxtClientSecret.Password = savedSecret ?? "";

            LoadSavedInterval();

            if (CredentialStorage.HasToken())
                ShowSignedInState();
            else
                ShowSignedOutState();
        }

        // ================================================================
        // SHOW CRASH — called from App.xaml.cs
        // ================================================================
        public void ShowCrash(string msg)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Log("=== CRASH ===");
                Log(msg);
            });
        }

        // ================================================================
        // LOAD client_secret.json
        // ================================================================
        private void BtnLoadJson_Click(object sender, RoutedEventArgs e)
        {
            // Try IsolatedStorage first
            try
            {
                using (var store = IsolatedStorageFile.GetUserStoreForApplication())
                {
                    string[] files = store.GetFileNames("*.json");
                    if (files.Length > 0)
                    {
                        using (var stream = store.OpenFile(files[0],
                            FileMode.Open, FileAccess.Read))
                        using (var reader = new StreamReader(stream))
                        {
                            ParseCredentials(reader.ReadToEnd());
                            return;
                        }
                    }
                }
            }
            catch { }

            // Try common external locations
            ScanExternalStorageAsync();
        }

        private void ScanExternalStorageAsync()
        {
            var log = new System.Text.StringBuilder();
            log.AppendLine("Scanning...");

            string[] searchDirs =
            {
                "D:\\",
                "D:\\Documents",
                "D:\\Downloads",
                "E:\\",
                "E:\\Documents",
                "E:\\Downloads",
                "D:\\Documents",
                "C:\\Data\\Users\\Public",
                "C:\\Data\\Users\\Public\\Documents",
                "C:\\Data\\Users\\Public\\Downloads",
            };

            foreach (string dir in searchDirs)
            {
                try
                {
                    bool exists = System.IO.Directory.Exists(dir);
                    log.AppendLine("Dir " + dir + ": " + (exists ? "found" : "not found"));
                    if (!exists) continue;

                    string specific = System.IO.Path.Combine(dir, "client_secret.json");
                    bool fileExists = System.IO.File.Exists(specific);
                    log.AppendLine("  client_secret.json: " + (fileExists ? "found!" : "no"));

                    if (fileExists)
                    {
                        using (var sr = new System.IO.StreamReader(specific))
                        {
                            string text = sr.ReadToEnd();
                            ParseCredentials(text);
                            TxtLoginStatus.Text = "Loaded: " + specific;
                            return;
                        }
                    }

                    // Check all json files
                    string[] files = System.IO.Directory.GetFiles(dir, "*.json");
                    log.AppendLine("  *.json files: " + files.Length);
                    foreach (string f in files)
                    {
                        try
                        {
                            using (var sr = new System.IO.StreamReader(f))
                            {
                                string text = sr.ReadToEnd();
                                ParseCredentials(text);
                                TxtLoginStatus.Text = "Loaded: " + f;
                                return;
                            }
                        }
                        catch (Exception ex2)
                        {
                            log.AppendLine("  Read error: " + ex2.Message);
                        }
                    }
                }
                catch (Exception ex)
                {
                    log.AppendLine("  Error: " + ex.Message);
                }
            }

            TxtLoginStatus.Text = log.ToString();
        }


        private void ParseCredentials(string json)
        {
            int idx = json.IndexOf("\"installed\"");
            if (idx < 0) idx = json.IndexOf("\"web\"");
            if (idx < 0) { TxtLoginStatus.Text = "Unknown JSON format."; return; }
            int brace = json.IndexOf('{', idx);
            int end = -1, depth = 0;
            for (int i = brace; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}') { depth--; if (depth == 0) { end = i; break; } }
            }
            if (end < 0) return;
            string section  = json.Substring(brace, end - brace + 1);
            string clientId = JsonHelper.GetString(section, "client_id");
            string secret   = JsonHelper.GetString(section, "client_secret");
            if (!string.IsNullOrEmpty(clientId))     TxtClientId.Text         = clientId;
            if (!string.IsNullOrEmpty(secret))       TxtClientSecret.Password = secret;
            TxtLoginStatus.Text = "Credentials loaded.";
        }

        // ================================================================
        // SIGN IN — Step 1
        // ================================================================
        private async void BtnSignIn_Click(object sender, RoutedEventArgs e)
        {
            string clientId     = TxtClientId.Text.Trim();
            string clientSecret = TxtClientSecret.Password.Trim();
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                TxtLoginStatus.Text = "Please enter Client ID and Client Secret.";
                return;
            }
            CredentialStorage.SaveCredentials(clientId, clientSecret);
            _api                = new GoogleApiService(clientId, clientSecret);
            BtnSignIn.IsEnabled = false;
            TxtLoginStatus.Text = "Requesting device code...";
            PanelCode.Visibility = Visibility.Collapsed;
            try
            {
                var result = await _api.RequestDeviceCodeAsync();
                TxtVerificationUrl.Text = result.VerificationUrl;
                TxtUserCode.Text        = result.UserCode;
                PanelCode.Visibility    = Visibility.Visible;
                TxtLoginStatus.Text     = "";
                PollForTokenAsync(result.DeviceCode, result.Interval);
            }
            catch (Exception ex)
            {
                TxtLoginStatus.Text = "Error: " + ex.Message;
                BtnSignIn.IsEnabled = true;
            }
        }

        // ================================================================
        // SIGN IN — Step 2: poll
        // ================================================================
        private void PollForTokenAsync(string deviceCode, int interval)
        {
            // Use DispatcherTimer to poll on UI thread — required for WebClient
            var timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(interval);
            timer.Tick += async (s, e) =>
            {
                timer.Stop();
                try
                {
                    string refreshToken = await _api.PollForTokenAsync(deviceCode);
                    if (refreshToken != null)
                    {
                        Log("Token received!");
                        CredentialStorage.SaveToken(refreshToken);
                        PanelCode.Visibility  = Visibility.Collapsed;
                        BtnSignIn.IsEnabled   = true;
                        TxtLoginStatus.Text   = "";
                        ShowSignedInState();
                        // Switch to Sync tab
                        MainPivot.SelectedIndex = 1;
                    }
                    else
                    {
                        Log("Still pending, retrying...");
                        timer.Start();
                    }
                }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    if (msg.Contains("authorization_pending") ||
                        msg.Contains("slow_down"))
                    {
                        Log("Pending... retrying in " + interval + "s");
                        timer.Start();
                    }
                    else
                    {
                        PanelCode.Visibility = Visibility.Collapsed;
                        BtnSignIn.IsEnabled  = true;
                        TxtLoginStatus.Text  = "Auth failed: " + msg +
                            (ex.InnerException != null    ? " Inner: " + ex.InnerException.Message : "");
                        Log("Auth failed: " + msg);
                        Log("Stack: " + ex.StackTrace);
                    }
                }
            };
            timer.Start();
        }

        // ================================================================
        // GOOGLE → PHONE (one-direction, ETag-based)
        // ================================================================
        private void BtnGoogleToPhone_Click(object sender, RoutedEventArgs e)
        {
            SetUiBusy(true);
            _log.Clear();
            Dispatcher.BeginInvoke(() => TxtLog.Text = "");
            Log("=== Google→Phone: " + DateTime.Now.ToString("HH:mm:ss") + " ===");

            string clientId     = CredentialStorage.LoadClientId();
            string clientSecret = CredentialStorage.LoadClientSecret();
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                Log("Error: credentials not set. Go to Sign in tab.");
                Dispatcher.BeginInvoke(() => SetUiBusy(false));
                return;
            }

            Action<string> progress = msg => Log(msg);

            Task.Run(async () =>
            {
                try
                {
                    var api   = new GoogleApiService(clientId, clientSecret);
                    var store = new ContactStoreService();

                    // Load state
                    var state   = SyncStateStorage.Load();
                    bool isFirst = state.Count == 0;
                    Log("isFirst=" + isFirst + " state=" + state.Count);

                    Log("Fetching contacts from Google...");
                    var contacts = await api.FetchAllContactsAsync(progress);
                    Log("Fetched: " + contacts.Count);

                    if (contacts.Count == 0)
                    {
                        Log("No contacts found.");
                        return;
                    }

                    var newState = new Dictionary<string, ContactSyncState>();
                    int updated  = 0;
                    int skipped  = 0;

                    foreach (var gc in contacts)
                    {
                        if (string.IsNullOrEmpty(gc.Id)) continue;

                        bool hasState    = state.ContainsKey(gc.Id);
                        string savedETag = hasState ? state[gc.Id].ETag : null;
                        bool changed     = !hasState ||
                                          savedETag != gc.ETag;

                        if (isFirst || changed)
                        {
                            Log((isFirst ? "Saving" : "Updating") + ": " +
                                gc.FirstName + " " + gc.LastName +
                                " (ETag: " + (gc.ETag ?? "null").Substring(0,
                                    Math.Min(10, (gc.ETag ?? "").Length)) + ")");
                            await store.UpsertContactAsync(gc);
                            updated++;
                        }
                        else
                        {
                            skipped++;
                        }

                        // Get local hash for this contact
                        string localHash = "";
                        var phone = await store.FindContactByRemoteIdAsync(gc.Id);
                        if (phone != null)
                        {
                            var props = await phone.GetPropertiesAsync();
                            localHash = SyncManager.ComputeLocalHash(props);
                        }

                        newState[gc.Id] = new ContactSyncState
                        {
                            ETag       = gc.ETag       ?? "",
                            UpdateTime = gc.UpdateTime ?? "",
                            LocalHash  = localHash
                        };
                    }

                    // Handle deletions
                    int deleted = 0;
                    foreach (var id in state.Keys)
                    {
                        bool stillExists = false;
                        foreach (var gc in contacts)
                            if (gc.Id == id) { stillExists = true; break; }
                        if (!stillExists)
                        {
                            await store.DeleteContactAsync(id);
                            deleted++;
                        }
                    }

                    SyncStateStorage.Save(newState);
                    Log("Updated=" + updated + " Skipped=" + skipped +
                        " Deleted=" + deleted);
                    Log("=== Done: " + DateTime.Now.ToString("HH:mm:ss") + " ===");

                    Dispatcher.BeginInvoke(() =>
                    {
                        TxtLastSync.Text       = "Last sync: " +
                            DateTime.Now.ToString("dd MMM yyyy HH:mm");
                        TxtLastSync.Visibility = Visibility.Visible;
                    });
                }
                catch (Exception ex)
                {
                    Log("EXCEPTION: " + ex.GetType().Name);
                    Log("MSG: " + ex.Message);
                    if (ex.InnerException != null)
                        Log("INNER: " + ex.InnerException.Message);
                }
                finally
                {
                    Dispatcher.BeginInvoke(() => SetUiBusy(false));
                }
            });
        }

        // ================================================================
        // PHONE → GOOGLE (one-direction, hash-based change detection)
        // ================================================================
        private void BtnPhoneToGoogle_Click(object sender, RoutedEventArgs e)
        {
            SetUiBusy(true);
            _log.Clear();
            Dispatcher.BeginInvoke(() => TxtLog.Text = "");
            Log("=== Phone→Google: " + DateTime.Now.ToString("HH:mm:ss") + " ===");

            string clientId     = CredentialStorage.LoadClientId();
            string clientSecret = CredentialStorage.LoadClientSecret();
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            {
                Log("Error: credentials not set. Go to Sign in tab.");
                Dispatcher.BeginInvoke(() => SetUiBusy(false));
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    var manager = new SyncManager(clientId, clientSecret);

                    // Read phone contacts
                    var store    = new ContactStoreService();
                    var contacts = await store.ReadAllContactsAsync(msg => Log(msg));
                    Log("Found " + contacts.Count + " contacts on phone.");

                    if (contacts.Count == 0)
                    {
                        Log("No contacts. Run Google→Phone first.");
                        return;
                    }

                    // Load state
                    var state = SyncStateStorage.Load();

                    // Get access token once
                    var api = new GoogleApiService(clientId, clientSecret);
                    string refreshToken = CredentialStorage.LoadToken();
                    string accessToken  = await api.GetAccessTokenAsync(refreshToken);
                    Log("Access token OK.");

                    int created = 0, updated = 0, skipped = 0, failed = 0;

                    foreach (var c in contacts)
                    {
                        try
                        {
                            var props = await c.GetPropertiesAsync();
                            string currentHash = SyncManager.ComputeLocalHash(props);

                            if (string.IsNullOrEmpty(c.RemoteId) ||
                                !state.ContainsKey(c.RemoteId))
                            {
                                // New contact — create on Google
                                Log("Creating: " + c.DisplayName);
                                string firstName = GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.GivenName);
                                string lastName  = GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.FamilyName);
                                string notes     = GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.Notes);
                                var phones = BuildPhones(props);
                                var emails = BuildEmails(props);
                                var orgs   = BuildOrgs(props);
                                var urls   = BuildUrls(props);

                                string newId = await api.CreateContactAsync(
                                    firstName, lastName, c.DisplayName,
                                    notes, phones, emails, null, urls, orgs,
                                    null, accessToken);
                                if (!string.IsNullOrEmpty(newId))
                                {
                                    c.RemoteId = newId;
                                    await c.SaveAsync();
                                    // Add to state
                                    state[newId] = new ContactSyncState
                                    {
                                        ETag = "", UpdateTime = "",
                                        LocalHash = currentHash
                                    };
                                    created++;
                                }
                                else failed++;
                            }
                            else
                            {
                                string savedHash = state[c.RemoteId].LocalHash;
                                if (savedHash == currentHash)
                                {
                                    skipped++;
                                }
                                else
                                {
                                    // Changed — update on Google
                                    Log("Updating: " + c.DisplayName);
                                    string etag = state[c.RemoteId].ETag ?? "*";
                                    string firstName = GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.GivenName);
                                    string lastName  = GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.FamilyName);
                                    string notes     = GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.Notes);
                                    var phones = BuildPhones(props);
                                    var emails = BuildEmails(props);
                                    var orgs   = BuildOrgs(props);
                                    var urls   = BuildUrls(props);

                                    bool ok = await api.UpdateContactAsync(
                                        c.RemoteId, etag,
                                        firstName, lastName, c.DisplayName,
                                        notes, phones, emails, null, urls, orgs,
                                        null, accessToken);
                                    if (ok)
                                    {
                                        state[c.RemoteId].LocalHash = currentHash;
                                        updated++;
                                    }
                                    else failed++;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Log("Error: " + c.DisplayName + ": " + ex.Message);
                        }
                    }

                    SyncStateStorage.Save(state);
                    Log("=== Done: created=" + created + " updated=" + updated +
                        " skipped=" + skipped + " failed=" + failed + " ===");

                    Dispatcher.BeginInvoke(() =>
                    {
                        TxtLastSync.Text       = "Last upload: " +
                            DateTime.Now.ToString("dd MMM yyyy HH:mm");
                        TxtLastSync.Visibility = Visibility.Visible;
                    });
                }
                catch (Exception ex)
                {
                    Log("EXCEPTION: " + ex.GetType().Name);
                    Log("MSG: " + ex.Message);
                    if (ex.InnerException != null)
                        Log("INNER: " + ex.InnerException.Message);
                }
                finally
                {
                    Dispatcher.BeginInvoke(() => SetUiBusy(false));
                }
            });
        }

        private string GetProp(IDictionary<string, object> props, string key)
        {
            return props.ContainsKey(key) ? props[key] as string ?? "" : "";
        }

        private List<GPhone> BuildPhones(IDictionary<string, object> props)
        {
            var list = new List<GPhone>();
            AddPhone(list, GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.MobileTelephone),         "mobile");
            AddPhone(list, GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.Telephone),               "home");
            AddPhone(list, GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.WorkTelephone),           "work");
            AddPhone(list, GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.AlternateMobileTelephone),"mobile");
            AddPhone(list, GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.AlternateTelephone),      "home");
            AddPhone(list, GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.AlternateWorkTelephone),  "work");
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
            string e  = GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.Email);
            string ew = GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.WorkEmail);
            string eo = GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.OtherEmail);
            if (!string.IsNullOrEmpty(e))  list.Add(new GEmail { Address = e,  Type = "home" });
            if (!string.IsNullOrEmpty(ew)) list.Add(new GEmail { Address = ew, Type = "work" });
            if (!string.IsNullOrEmpty(eo)) list.Add(new GEmail { Address = eo, Type = "other" });
            return list;
        }

        private List<GOrg> BuildOrgs(IDictionary<string, object> props)
        {
            var list = new List<GOrg>();
            string c = GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.CompanyName);
            string t = GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.JobTitle);
            if (!string.IsNullOrEmpty(c) || !string.IsNullOrEmpty(t))
                list.Add(new GOrg { Name = c, Title = t });
            return list;
        }

        private List<GUrl> BuildUrls(IDictionary<string, object> props)
        {
            var list = new List<GUrl>();
            string u = GetProp(props, Windows.Phone.PersonalInformation.KnownContactProperties.Url);
            if (!string.IsNullOrEmpty(u)) list.Add(new GUrl { Value = u, Type = "other" });
            return list;
        }

        // ================================================================
        // CLEAR LOG
        // ================================================================
        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            _log.Clear();
            Dispatcher.BeginInvoke(() => TxtLog.Text = "");
        }

        // ================================================================
        // BACKGROUND SYNC
        // ================================================================
        private const string AgentName = "GoogleContactSyncAgent";

        private int GetSelectedInterval()
        {
            if (Rb60  != null && Rb60.IsChecked  == true) return 60;
            if (Rb120 != null && Rb120.IsChecked == true) return 120;
            if (Rb240 != null && Rb240.IsChecked == true) return 240;
            return 30; // default
        }

        private void RbInterval_Checked(object sender, RoutedEventArgs e)
        {
            // Save selected interval
            int interval = GetSelectedInterval();
            IsolatedStorageSettings.ApplicationSettings["BgInterval"] = interval;
            IsolatedStorageSettings.ApplicationSettings.Save();

            // Re-register if already running to apply new interval
            bool registered = ScheduledActionService.Find(AgentName) != null;
            if (registered)
            {
                RegisterBackgroundTask();
                Log("Auto sync interval changed to " + interval + " min.");
            }
        }

        private void LoadSavedInterval()
        {
            var s = IsolatedStorageSettings.ApplicationSettings;
            int interval = s.Contains("BgInterval") ? (int)s["BgInterval"] : 30;
            if (interval >= 240 && Rb240 != null) Rb240.IsChecked = true;
            else if (interval >= 120 && Rb120 != null) Rb120.IsChecked = true;
            else if (interval >= 60  && Rb60  != null) Rb60.IsChecked  = true;
            else if (Rb30 != null) Rb30.IsChecked = true;
        }

        private void RegisterBackgroundTask()
        {
            try
            {
                // Remove existing
                var existing = ScheduledActionService.Find(AgentName);
                if (existing != null)
                    ScheduledActionService.Remove(AgentName);

                int interval = GetSelectedInterval();

                var agent = new PeriodicTask(AgentName)
                {
                    Description = "Syncs Google Contacts every " +
                                  interval + " minutes"
                };
                ScheduledActionService.Add(agent);
                Log("Background sync registered (" + interval + " min).");
                UpdateBgSyncStatus();
            }
            catch (Exception ex)
            {
                Log("BG task error: " + ex.Message);
            }
        }

        private void UnregisterBackgroundTask()
        {
            try
            {
                var existing = ScheduledActionService.Find(AgentName);
                if (existing != null)
                    ScheduledActionService.Remove(AgentName);
                Log("Background sync unregistered.");
                UpdateBgSyncStatus();
            }
            catch { }
        }

        private void UpdateBgSyncStatus()
        {
            bool registered = ScheduledActionService.Find(AgentName) != null;
            int  interval   = GetSelectedInterval();
            Dispatcher.BeginInvoke(() =>
            {
                TxtBgStatus.Text = registered
                    ? "Auto sync: ON (every " + interval + " min)"
                    : "Auto sync: OFF";
                BtnToggleBgSync.Content = registered
                    ? "Disable auto sync"
                    : "Enable auto sync";

                // Show last bg sync time if available
                var s = IsolatedStorageSettings.ApplicationSettings;
                if (s.Contains("LastBgSync"))
                    TxtBgStatus.Text += "\nLast: " + s["LastBgSync"];
            });
        }

        private void BtnToggleBgSync_Click(object sender, RoutedEventArgs e)
        {
            bool registered = ScheduledActionService.Find(AgentName) != null;
            if (registered)
                UnregisterBackgroundTask();
            else
                RegisterBackgroundTask();
        }

        // ================================================================
        // SIGN OUT
        // ================================================================
        private void BtnSignOut_Click(object sender, RoutedEventArgs e)
        {
            UnregisterBackgroundTask();
            CredentialStorage.DeleteToken();
            ETagStorage.Clear();
            SyncStateStorage.Clear();
            ShowSignedOutState();
            TxtLastSync.Visibility = Visibility.Collapsed;
        }

        // ================================================================
        // EMAIL LOG
        // ================================================================
        private void BtnEmailLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var task = new EmailComposeTask
                {
                    Subject = "GoogleContactSyncWP Log " +
                              DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    Body    = _log.Length > 0 ? _log.ToString() : "(empty)"
                };
                task.Show();
            }
            catch (Exception ex)
            {
                Log("Email error: " + ex.Message);
            }
        }

        // ================================================================
        // LOGGING — updates screen in real time
        // ================================================================
        private void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + msg;
            _log.AppendLine(line);
            // Update UI on UI thread
            Dispatcher.BeginInvoke(() =>
            {
                TxtLog.Text += line + "\n";
                // Auto-scroll to bottom
                LogScroller.ScrollToVerticalOffset(LogScroller.ScrollableHeight);
            });
        }

        // ================================================================
        // UI STATE
        // ================================================================
        private void ShowSignedInState()
        {
            TxtAccountStatus.Text       = "Google account connected";
            BtnGoogleToPhone.IsEnabled  = true;
            BtnPhoneToGoogle.IsEnabled  = true;
            BtnSignOut.IsEnabled        = true;
            BtnToggleBgSync.IsEnabled   = true;
            UpdateBgSyncStatus();
        }

        private void ShowSignedOutState()
        {
            TxtAccountStatus.Text       = "Not signed in";
            BtnGoogleToPhone.IsEnabled  = false;
            BtnPhoneToGoogle.IsEnabled  = false;
            BtnSignOut.IsEnabled        = false;
            BtnToggleBgSync.IsEnabled   = false;
            PanelCode.Visibility        = Visibility.Collapsed;
            BtnSignIn.IsEnabled         = true;
        }

        private void SetUiBusy(bool busy)
        {
            BtnGoogleToPhone.IsEnabled = !busy;
            BtnPhoneToGoogle.IsEnabled = !busy;
            BtnSignOut.IsEnabled       = !busy;
        }
    }
}
