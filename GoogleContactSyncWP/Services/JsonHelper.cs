// Services/JsonHelper.cs
// Minimal JSON parser/builder for WP8.1 Silverlight.

using System;
using System.Collections.Generic;
using System.Text;
using GoogleContactSyncWP.Models;

namespace GoogleContactSyncWP.Services
{
    public static class JsonHelper
    {
        // ================================================================
        // GET string value from flat JSON
        // ================================================================
        public static string GetString(string json, string key)
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
            return json.Substring(start + 1, end - start - 1)
                       .Replace("\\\"", "\"")
                       .Replace("\\n", "\n")
                       .Replace("\\r", "")
                       .Replace("\\t", "\t")
                       .Replace("\\\\", "\\");
        }

        public static int GetInt(string json, string key, int defaultVal = 0)
        {
            if (string.IsNullOrEmpty(json)) return defaultVal;
            string search = "\"" + key + "\"";
            int ki = json.IndexOf(search);
            if (ki < 0) return defaultVal;
            int ci = json.IndexOf(':', ki + search.Length);
            if (ci < 0) return defaultVal;
            int start = ci + 1;
            while (start < json.Length &&
                   (json[start] == ' ' || json[start] == '\t'))
                start++;
            int end = start;
            while (end < json.Length &&
                   (char.IsDigit(json[end]) || json[end] == '-'))
                end++;
            int result;
            return int.TryParse(json.Substring(start, end - start), out result)
                ? result : defaultVal;
        }

        // ================================================================
        // EXTRACT array
        // ================================================================
        public static string GetArray(string json, string key)
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
                else if (json[i] == ']')
                {
                    depth--;
                    if (depth == 0) return json.Substring(ci, i - ci + 1);
                }
                i++;
            }
            return null;
        }

        // ================================================================
        // SPLIT array into objects
        // ================================================================
        public static List<string> SplitObjects(string arrayJson)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(arrayJson)) return result;
            int i = 0;
            while (i < arrayJson.Length)
            {
                if (arrayJson[i] == '{')
                {
                    int depth = 0, start = i;
                    while (i < arrayJson.Length)
                    {
                        if (arrayJson[i] == '{') depth++;
                        else if (arrayJson[i] == '}')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                result.Add(arrayJson.Substring(
                                    start, i - start + 1));
                                break;
                            }
                        }
                        i++;
                    }
                }
                i++;
            }
            return result;
        }

        // ================================================================
        // PARSE contacts from People API response
        // ================================================================
        public static void ParseContacts(string json, List<GoogleContact> list)
        {
            string connectionsArr = GetArray(json, "connections");
            if (connectionsArr == null) return;

            foreach (string personJson in SplitObjects(connectionsArr))
            {
                var gc = ParsePerson(personJson);
                if (gc != null) list.Add(gc);
            }
        }

        private static GoogleContact ParsePerson(string json)
        {
            var gc = new GoogleContact();
            gc.Id = GetString(json, "resourceName");

            // Extract top-level etag — it appears near the start of the object
            // before any nested structures
            string etagKey = "\"etag\"";
            int etagIdx = json.IndexOf(etagKey);
            if (etagIdx >= 0)
            {
                int ci = json.IndexOf(':', etagIdx + etagKey.Length);
                if (ci >= 0)
                {
                    int start = json.IndexOf('"', ci + 1);
                    if (start >= 0)
                    {
                        int end = start + 1;
                        while (end < json.Length)
                        {
                            if (json[end] == '"' && json[end - 1] != '\\')
                                break;
                            end++;
                        }
                        gc.ETag = json.Substring(start + 1, end - start - 1);
                    }
                }
            }

            // updateTime from metadata.sources
            string metaArr = GetArray(json, "sources");
            if (metaArr != null)
                foreach (var src in SplitObjects(metaArr))
                {
                    string t = GetString(src, "updateTime");
                    if (string.Compare(t, gc.UpdateTime,
                        StringComparison.Ordinal) > 0)
                        gc.UpdateTime = t;
                }

            // Names
            string namesArr = GetArray(json, "names");
            if (namesArr != null)
            {
                var objs = SplitObjects(namesArr);
                if (objs.Count > 0)
                {
                    gc.FirstName = GetString(objs[0], "givenName");
                    gc.LastName  = GetString(objs[0], "familyName");
                }
            }

            // Nicknames
            string nicksArr = GetArray(json, "nicknames");
            if (nicksArr != null)
            {
                var objs = SplitObjects(nicksArr);
                if (objs.Count > 0)
                    gc.Nickname = GetString(objs[0], "value");
            }

            // Biographies
            string biosArr = GetArray(json, "biographies");
            if (biosArr != null)
            {
                var objs = SplitObjects(biosArr);
                if (objs.Count > 0)
                    gc.Notes = GetString(objs[0], "value");
            }

            // Phones
            string phonesArr = GetArray(json, "phoneNumbers");
            if (phonesArr != null)
                foreach (var o in SplitObjects(phonesArr))
                {
                    string num  = GetString(o, "value");
                    string type = GetString(o, "type");
                    if (string.IsNullOrEmpty(type)) type = "mobile";
                    if (!string.IsNullOrEmpty(num))
                        gc.Phones.Add(new GPhone
                        {
                            Number = num,
                            Type   = type
                        });
                }

            // Emails
            string emailsArr = GetArray(json, "emailAddresses");
            if (emailsArr != null)
                foreach (var o in SplitObjects(emailsArr))
                {
                    string addr = GetString(o, "value");
                    string type = GetString(o, "type");
                    if (string.IsNullOrEmpty(type)) type = "home";
                    if (!string.IsNullOrEmpty(addr))
                        gc.Emails.Add(new GEmail
                        {
                            Address = addr,
                            Type    = type
                        });
                }

            // Addresses
            string addrsArr = GetArray(json, "addresses");
            if (addrsArr != null)
                foreach (var o in SplitObjects(addrsArr))
                    gc.Addresses.Add(new GAddress
                    {
                        Street     = GetString(o, "streetAddress"),
                        City       = GetString(o, "city"),
                        Region     = GetString(o, "region"),
                        PostalCode = GetString(o, "postalCode"),
                        Country    = GetString(o, "country"),
                        Type       = GetString(o, "type")
                    });

            // Organizations
            string orgsArr = GetArray(json, "organizations");
            if (orgsArr != null)
                foreach (var o in SplitObjects(orgsArr))
                    gc.Organizations.Add(new GOrg
                    {
                        Name  = GetString(o, "name"),
                        Title = GetString(o, "title")
                    });

            // URLs
            string urlsArr = GetArray(json, "urls");
            if (urlsArr != null)
                foreach (var o in SplitObjects(urlsArr))
                {
                    string v = GetString(o, "value");
                    if (!string.IsNullOrEmpty(v))
                        gc.Urls.Add(new GUrl
                        {
                            Value = v,
                            Type  = GetString(o, "type")
                        });
                }

            // Birthday
            string bdayArr = GetArray(json, "birthdays");
            if (bdayArr != null)
            {
                var objs = SplitObjects(bdayArr);
                if (objs.Count > 0)
                {
                    int di = objs[0].IndexOf("\"date\"");
                    if (di >= 0)
                    {
                        int bo = objs[0].IndexOf('{', di);
                        int bc = objs[0].IndexOf('}', bo);
                        if (bo >= 0 && bc > bo)
                        {
                            string dateObj = objs[0].Substring(
                                bo, bc - bo + 1);
                            int y = GetInt(dateObj, "year");
                            int m = GetInt(dateObj, "month");
                            int d = GetInt(dateObj, "day");
                            if (m > 0 && d > 0)
                                gc.Birthday = new GDate
                                {
                                    Year  = y > 0 ? (int?)y : null,
                                    Month = m,
                                    Day   = d
                                };
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(gc.FirstName) &&
                string.IsNullOrEmpty(gc.LastName) &&
                gc.Phones.Count == 0 &&
                gc.Emails.Count == 0)
                return null;

            return gc;
        }

        // ================================================================
        // BUILD person JSON for create/update
        // ================================================================
        public static string BuildPersonJson(
            string firstName, string lastName, string nickname, string notes,
            List<GPhone> phones, List<GEmail> emails,
            List<GAddress> addresses, List<GUrl> urls,
            List<GOrg> orgs, GDate birthday,
            string etag = null)
        {
            var sb = new StringBuilder();
            sb.Append("{");

            if (etag != null)
            {
                sb.Append("\"etag\":");
                AppendString(sb, etag);
                sb.Append(",");
            }

            // Names
            sb.Append("\"names\":[{");
            sb.Append("\"givenName\":");
            AppendString(sb, firstName ?? "");
            sb.Append(",\"familyName\":");
            AppendString(sb, lastName ?? "");
            sb.Append("}]");

            if (!string.IsNullOrEmpty(nickname))
            {
                sb.Append(",\"nicknames\":[{\"value\":");
                AppendString(sb, nickname);
                sb.Append("}]");
            }

            if (!string.IsNullOrEmpty(notes))
            {
                sb.Append(",\"biographies\":[{\"value\":");
                AppendString(sb, notes);
                sb.Append(",\"contentType\":\"TEXT_PLAIN\"}]");
            }

            if (phones != null && phones.Count > 0)
            {
                sb.Append(",\"phoneNumbers\":[");
                for (int i = 0; i < phones.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"value\":");
                    AppendString(sb, phones[i].Number);
                    sb.Append(",\"type\":");
                    AppendString(sb, phones[i].Type ?? "mobile");
                    sb.Append("}");
                }
                sb.Append("]");
            }

            if (emails != null && emails.Count > 0)
            {
                sb.Append(",\"emailAddresses\":[");
                for (int i = 0; i < emails.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"value\":");
                    AppendString(sb, emails[i].Address);
                    sb.Append(",\"type\":");
                    AppendString(sb, emails[i].Type ?? "home");
                    sb.Append("}");
                }
                sb.Append("]");
            }

            if (addresses != null && addresses.Count > 0)
            {
                sb.Append(",\"addresses\":[");
                for (int i = 0; i < addresses.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var a = addresses[i];
                    sb.Append("{\"type\":");
                    AppendString(sb, a.Type ?? "home");
                    sb.Append(",\"streetAddress\":");
                    AppendString(sb, a.Street ?? "");
                    sb.Append(",\"city\":");
                    AppendString(sb, a.City ?? "");
                    sb.Append(",\"region\":");
                    AppendString(sb, a.Region ?? "");
                    sb.Append(",\"postalCode\":");
                    AppendString(sb, a.PostalCode ?? "");
                    sb.Append(",\"country\":");
                    AppendString(sb, a.Country ?? "");
                    sb.Append("}");
                }
                sb.Append("]");
            }

            if (orgs != null && orgs.Count > 0)
            {
                sb.Append(",\"organizations\":[");
                for (int i = 0; i < orgs.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"name\":");
                    AppendString(sb, orgs[i].Name ?? "");
                    sb.Append(",\"title\":");
                    AppendString(sb, orgs[i].Title ?? "");
                    sb.Append("}");
                }
                sb.Append("]");
            }

            if (urls != null && urls.Count > 0)
            {
                sb.Append(",\"urls\":[");
                for (int i = 0; i < urls.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append("{\"value\":");
                    AppendString(sb, urls[i].Value ?? "");
                    sb.Append(",\"type\":");
                    AppendString(sb, urls[i].Type ?? "other");
                    sb.Append("}");
                }
                sb.Append("]");
            }

            if (birthday != null && birthday.Month > 0 && birthday.Day > 0)
            {
                sb.Append(",\"birthdays\":[{\"date\":{");
                if (birthday.Year.HasValue)
                    sb.Append("\"year\":" + birthday.Year.Value + ",");
                sb.Append("\"month\":" + birthday.Month + ",");
                sb.Append("\"day\":" + birthday.Day);
                sb.Append("}}]");
            }

            sb.Append("}");
            return sb.ToString();
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            sb.Append("\"");
            if (!string.IsNullOrEmpty(s))
                sb.Append(s.Replace("\\", "\\\\")
                           .Replace("\"", "\\\"")
                           .Replace("\n", "\\n")
                           .Replace("\r", "")
                           .Replace("\t", "\\t"));
            sb.Append("\"");
        }
    }
}
