// Helpers/SyncStateStorage.cs
// Stores per-contact sync state in IsolatedStorageSettings.
// WP8.1 Silverlight port — no Windows.Data.Json, no ApplicationData.
// Uses pipe-delimited serialization.

using System;
using System.Collections.Generic;
using System.IO.IsolatedStorage;
using System.Text;

namespace GoogleContactSyncWP.Helpers
{
    public class ContactSyncState
    {
        public string ETag       { get; set; }
        public string UpdateTime { get; set; }
        public string LocalHash  { get; set; }
    }

    public static class SyncStateStorage
    {
        private static readonly IsolatedStorageSettings _settings =
            IsolatedStorageSettings.ApplicationSettings;
        private const string Key = "SyncStateV2";

        // Format: one line per contact
        // people/xxx\tetag\tupdateTime\tlocalHash
        public static void Save(Dictionary<string, ContactSyncState> state)
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
                _settings[Key] = sb.ToString();
                _settings.Save();
            }
            catch { }
        }

        public static Dictionary<string, ContactSyncState> Load()
        {
            var result = new Dictionary<string, ContactSyncState>();
            try
            {
                if (!_settings.Contains(Key)) return result;
                string raw = _settings[Key] as string;
                if (string.IsNullOrEmpty(raw)) return result;

                foreach (string line in raw.Split('\n'))
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split('\t');
                    if (parts.Length < 4) continue;
                    string id = Unescape(parts[0]);
                    if (string.IsNullOrEmpty(id)) continue;
                    result[id] = new ContactSyncState
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

        public static void Clear()
        {
            if (_settings.Contains(Key))
                _settings.Remove(Key);
            _settings.Save();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\t", "\\t")
                    .Replace("\n", "\\n");
        }

        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\t", "\t")
                    .Replace("\\n", "\n")
                    .Replace("\\\\", "\\");
        }
    }
}
