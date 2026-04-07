// Helpers/ETagStorage.cs
// Stores per-contact ETag map in IsolatedStorageSettings.
// Key: resourceName (people/xxx), Value: etag string.
// Uses simple pipe-delimited serialization — no JSON lib needed.

using System.Collections.Generic;
using System.IO.IsolatedStorage;
using System.Text;

namespace GoogleContactSyncWP.Helpers
{
    public static class ETagStorage
    {
        private static readonly IsolatedStorageSettings _settings =
            IsolatedStorageSettings.ApplicationSettings;
        private const string Key = "ETagMapV1";

        public static void SaveAll(Dictionary<string, string> map)
        {
            var sb = new StringBuilder();
            foreach (var kv in map)
            {
                if (string.IsNullOrEmpty(kv.Key)) continue;
                // Use \n as record separator, | as field separator
                sb.Append(kv.Key.Replace("|", "").Replace("\n", ""));
                sb.Append("|");
                sb.Append((kv.Value ?? "").Replace("|", "").Replace("\n", ""));
                sb.Append("\n");
            }
            _settings[Key] = sb.ToString();
            _settings.Save();
        }

        public static Dictionary<string, string> LoadAll()
        {
            var result = new Dictionary<string, string>();
            if (!_settings.Contains(Key)) return result;

            string raw = _settings[Key] as string;
            if (string.IsNullOrEmpty(raw)) return result;

            foreach (string line in raw.Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;
                int sep = line.IndexOf('|');
                if (sep < 0) continue;
                string id   = line.Substring(0, sep);
                string etag = line.Substring(sep + 1);
                if (!string.IsNullOrEmpty(id))
                    result[id] = etag;
            }
            return result;
        }

        public static void Clear()
        {
            if (_settings.Contains(Key))
                _settings.Remove(Key);
            _settings.Save();
        }
    }
}
