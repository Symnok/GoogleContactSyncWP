# GoogleContactSyncWP

A Windows Phone 8.1 Silverlight app that synchronizes Google Contacts with the phone's People hub using the Google People API and OAuth2 Device Flow. Supports bidirectional sync with conflict resolution and optional background auto-sync.

## Features

- **Google → Phone sync** — fetches all contacts from Google and writes them silently to the People hub
- **Phone → Google sync** — uploads new and changed contacts back to Google
- **Bidirectional conflict resolution** — if both sides changed, compares Google `updateTime` vs local hash; Google wins by default
- **ETag-based incremental sync** — only changed contacts are downloaded on subsequent syncs
- **Local hash change detection** — only changed contacts are uploaded on Phone → Google
- **Background auto-sync** — configurable interval: 30 / 60 / 120 / 240 minutes
- **OAuth2 Device Flow** — sign in by entering a code on any browser; no redirect URI needed
- **Load credentials from SD card** — scans `D:\` and subfolders for `client_secret.json`
- **Live sync log** — real-time progress on a dedicated Log tab, with email export
- **Tile back content** — shows last sync result count after background sync

## Requirements

### Device
- Windows Phone 8.1 (Silverlight runtime)
- Tested on Nokia Lumia 640
- Interop-unlocked phone recommended (for SD card file access)

### Development
- Visual Studio 2015
- Windows Phone 8.1 SDK
- Two projects in solution: `GoogleContactSyncWP` (main app) + `SyncAgent` (background task)

### Google Account
- Google account with Contacts API enabled
- OAuth2 credentials (installed app type) from Google Cloud Console
- `client_secret.json` downloaded from Cloud Console

## Setup

### Step 1 — Google Cloud Console

1. Go to https://console.cloud.google.com
2. Create a project or select existing
3. Enable **People API**
4. Go to **Credentials → Create Credentials → OAuth Client ID**
5. Application type: **TV/Limited Input Devices** 
6. Download the `client_secret.json` file

### Step 2 — Place credentials on phone

**Option A — Internal Storage or SD card** (interop-unlocked phone):
- Copy `client_secret.json` to the root of your SD card (`D:\` on the phone)
- In the app tap **Load client_secret.json** → it scans `D:\` and `D:\Documents\`

**Option B — Isolated Storage Explorer**:
- Connect phone via USB
- Open **Windows Phone Power Tools** (free)
- Navigate to `GoogleContactSyncWP` app storage
- Drop `client_secret.json` into the root
- Tap **Load client_secret.json** in the app

**Option C — Type manually**:
- Enter Client ID and Client Secret directly in the Sign in tab text fields

### Step 3 — Sign in

1. Open the app → **Sign in** tab
2. Tap **Load client_secret.json** or enter credentials manually
3. Tap **Sign in with Google**
4. On your PC, open the displayed URL and enter the shown code
5. The app polls automatically — switches to Sync tab when authorized

### Step 4 — Sync

- **Google → Phone** — first run downloads all contacts; subsequent runs only download changes
- **Phone → Google** — uploads new or changed phone contacts to Google
- **Sign out** — clears token and sync state

## Background Sync

1. Go to **Settings** tab
2. Select interval: 30 / 60 / 120 / 240 minutes
3. Tap **Enable auto sync**
4. The tile back content updates after each background sync showing counts

> **Note:** WP8.1 enforces a minimum of 30 minutes for background tasks regardless of setting. Actual run frequency depends on battery saver and network conditions.

## Project Structure

```
GoogleContactSyncWP/
├── Models/
│   └── GoogleContact.cs          — contact data model
├── Helpers/
│   ├── CredentialStorage.cs      — saves refresh token + credentials to IsolatedStorageSettings
│   ├── ETagStorage.cs            — legacy ETag storage (superseded by SyncStateStorage)
│   └── SyncStateStorage.cs       — per-contact state: ETag + updateTime + local hash
├── Services/
│   ├── GoogleApiService.cs       — Device Flow, People API fetch/create/update via HttpWebRequest
│   ├── JsonHelper.cs             — manual JSON parser + builder (no external dependencies)
│   ├── ContactStoreService.cs    — reads/writes contacts via Windows.Phone.PersonalInformation
│   └── SyncManager.cs            — bidirectional sync logic with conflict resolution
├── MainPage.xaml / .cs           — 4-tab UI: Sign in, Sync, Settings, Log
├── App.xaml / .cs                — app lifecycle + crash handler
└── Properties/
    └── WMAppManifest.xml         — capabilities, background agent declaration

SyncAgent/
└── ScheduledAgent.cs             — self-contained background task agent
                                    runs SyncManager-equivalent logic every N minutes
```

## How Sync Works

### Google → Phone

```
1. Load sync state (ETag + updateTime + localHash per contact)
2. Fetch all contacts from Google People API
3. For each Google contact:
   - Not on phone → download
   - ETag changed → check if local also changed
     - Only Google changed → download
     - Both changed → compare updateTime → Google wins (default)
4. Contacts deleted from Google → deleted locally
5. Save new sync state
```

### Phone → Google

```
1. Load sync state
2. Read all contacts from ContactStore
3. For each phone contact:
   - No RemoteId → create on Google
   - LocalHash unchanged → skip
   - LocalHash changed → update on Google
4. Save updated hashes
```

## Contact Field Mapping

| Google People API | Windows Phone People hub |
|-------------------|--------------------------|
| `givenName` | Given name |
| `familyName` | Family name |
| `nickname` | Nickname |
| `biographies` | Notes |
| `phoneNumbers` (mobile) | Mobile telephone |
| `phoneNumbers` (home) | Home telephone |
| `phoneNumbers` (work) | Work telephone |
| `phoneNumbers` (alternate) | Alternate mobile/home/work |
| `emailAddresses` (home) | Email |
| `emailAddresses` (work) | Work email |
| `emailAddresses` (other) | Other email |
| `organizations.name` | Company |
| `organizations.title` | Job title |
| `urls` | Website |
| `birthdays` | Birthday |

Phones with no label default to **mobile**. Emails with no label default to **home**.

## Building

1. Open `GoogleContactSyncWP.sln` in VS2015
2. Set configuration to **Release / ARM**
3. **Build → Build Solution**
4. XAP output: `GoogleContactSyncWP\Bin\ARM\Release\GoogleContactSyncWP_Release_ARM.xap`
5. Deploy via USB: **Tools → Windows Phone → Application Deployment**

## Limitations

- No Store distribution — sideload only

## Credits
PeopleAPI code of this app is based on and inspired by user @Computershik73 code of corresponding applications for W10M https://github.com/Computershik73/WPGContacts and Symbian https://github.com/Computershik73/SymGContacts

Check his other programms here https://t.me/cmplog
- Background sync minimum interval: 30 minutes (OS enforced)
- Contact photos not synced
- Address fields not synced (WP8.1 PersonalInformation API limitation)
- Google CardDAV is deprecated and not used — People API only
