// Services/GoogleApiService.cs
// Uses HttpWebRequest with TaskCompletionSource — same pattern as CardDAVValidatorSL.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using GoogleContactSyncWP.Models;
using GoogleContactSyncWP.Helpers;

namespace GoogleContactSyncWP.Services
{
    public class DeviceCodeResult
    {
        public string DeviceCode      { get; set; }
        public string UserCode        { get; set; }
        public string VerificationUrl { get; set; }
        public int    Interval        { get; set; }
    }

    public class GoogleApiService
    {
        private const string TokenUrl      = "https://oauth2.googleapis.com/token";
        private const string DeviceCodeUrl = "https://oauth2.googleapis.com/device/code";
        private const string PeopleBase    = "https://people.googleapis.com/v1";
        private const string Scope         = "https://www.googleapis.com/auth/contacts";

        private readonly string _clientId;
        private readonly string _clientSecret;

        public GoogleApiService(string clientId, string clientSecret)
        {
            _clientId     = clientId;
            _clientSecret = clientSecret;
        }

        // ================================================================
        // HTTP POST — identical pattern to CardDAVValidatorSL
        // ================================================================
        private Task<string> PostAsync(string url, string body,
            string bearerToken = null)
        {
            var tcs     = new TaskCompletionSource<string>();
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method      = "POST";
            request.ContentType = "application/x-www-form-urlencoded";
            request.Accept      = "application/json";
            if (bearerToken != null)
                request.Headers[HttpRequestHeader.Authorization] =
                    "Bearer " + bearerToken;

            request.BeginGetRequestStream(arBody =>
            {
                try
                {
                    var req = (HttpWebRequest)arBody.AsyncState;
                    using (var stream = req.EndGetRequestStream(arBody))
                        stream.Write(bodyBytes, 0, bodyBytes.Length);

                    req.BeginGetResponse(arResp =>
                    {
                        try
                        {
                            var resp = (HttpWebResponse)
                                ((HttpWebRequest)arResp.AsyncState)
                                .EndGetResponse(arResp);
                            using (var r = new StreamReader(
                                resp.GetResponseStream()))
                                tcs.TrySetResult(r.ReadToEnd());
                        }
                        catch (WebException ex)
                        {
                            if (ex.Response != null)
                            {
                                try
                                {
                                    using (var r = new StreamReader(
                                        ex.Response.GetResponseStream()))
                                        tcs.TrySetResult(r.ReadToEnd());
                                }
                                catch
                                {
                                    tcs.TrySetResult(
                                        "{\"error\":\"" + ex.Status + "\"}");
                                }
                            }
                            else
                                tcs.TrySetResult(
                                    "{\"error\":\"" + ex.Message + "\"}");
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetResult(
                                "{\"error\":\"" + ex.Message + "\"}");
                        }
                    }, req);
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult("{\"error\":\"" + ex.Message + "\"}");
                }
            }, request);

            return tcs.Task;
        }

        // ================================================================
        // HTTP GET
        // ================================================================
        private Task<string> GetAsync(string url, string accessToken)
        {
            var tcs     = new TaskCompletionSource<string>();
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] =
                "Bearer " + accessToken;

            request.BeginGetResponse(arResp =>
            {
                try
                {
                    var resp = (HttpWebResponse)
                        ((HttpWebRequest)arResp.AsyncState)
                        .EndGetResponse(arResp);
                    using (var r = new StreamReader(resp.GetResponseStream()))
                        tcs.TrySetResult(r.ReadToEnd());
                }
                catch (WebException ex)
                {
                    if (ex.Response != null)
                    {
                        try
                        {
                            using (var r = new StreamReader(
                                ex.Response.GetResponseStream()))
                                tcs.TrySetResult(r.ReadToEnd());
                        }
                        catch
                        {
                            tcs.TrySetResult(
                                "{\"error\":\"" + ex.Status + "\"}");
                        }
                    }
                    else
                        tcs.TrySetResult(
                            "{\"error\":\"" + ex.Message + "\"}");
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult("{\"error\":\"" + ex.Message + "\"}");
                }
            }, request);

            return tcs.Task;
        }

        // ================================================================
        // STEP 1 — Request device code
        // ================================================================
        public async Task<DeviceCodeResult> RequestDeviceCodeAsync()
        {
            string body = "client_id=" + Uri.EscapeDataString(_clientId) +
                          "&scope="    + Uri.EscapeDataString(Scope);
            string json = await PostAsync(DeviceCodeUrl, body);

            string err = JsonHelper.GetString(json, "error");
            if (!string.IsNullOrEmpty(err))
                throw new Exception("Device code error: " + err +
                    "\nRaw: " + json.Substring(0, Math.Min(200, json.Length)));

            return new DeviceCodeResult
            {
                DeviceCode      = JsonHelper.GetString(json, "device_code"),
                UserCode        = JsonHelper.GetString(json, "user_code"),
                VerificationUrl = JsonHelper.GetString(json, "verification_url"),
                Interval        = JsonHelper.GetInt(json, "interval", 5)
            };
        }

        // ================================================================
        // STEP 2 — Poll for token
        // ================================================================
        public async Task<string> PollForTokenAsync(string deviceCode)
        {
            string grantType = Uri.EscapeDataString(
                "urn:ietf:params:oauth:grant-type:device_code");
            string body =
                "client_id="     + Uri.EscapeDataString(_clientId) +
                "&client_secret="+ Uri.EscapeDataString(_clientSecret) +
                "&device_code="  + Uri.EscapeDataString(deviceCode) +
                "&grant_type="   + grantType;

            string json  = await PostAsync(TokenUrl, body);
            string error = JsonHelper.GetString(json, "error");

            if (string.IsNullOrEmpty(error))
            {
                string token = JsonHelper.GetString(json, "refresh_token");
                if (!string.IsNullOrEmpty(token)) return token;
                return null;
            }

            if (error == "authorization_pending" || error == "slow_down")
                return null;

            throw new Exception("Auth error: " + error +
                "\nRaw: " + json.Substring(0, Math.Min(300, json.Length)));
        }

        // ================================================================
        // REFRESH access token
        // ================================================================
        public async Task<string> GetAccessTokenAsync(string refreshToken)
        {
            string body =
                "client_id="     + Uri.EscapeDataString(_clientId) +
                "&client_secret="+ Uri.EscapeDataString(_clientSecret) +
                "&refresh_token="+ Uri.EscapeDataString(refreshToken) +
                "&grant_type=refresh_token";

            string json  = await PostAsync(TokenUrl, body);
            string token = JsonHelper.GetString(json, "access_token");

            if (string.IsNullOrEmpty(token))
                throw new Exception("Token refresh failed: " +
                    json.Substring(0, Math.Min(300, json.Length)));

            return token;
        }

        // ================================================================
        // FETCH ALL contacts
        // ================================================================
        public async Task<List<GoogleContact>> FetchAllContactsAsync(
            Action<string> progress = null)
        {
            string refreshToken = CredentialStorage.LoadToken();
            if (progress != null) progress("Getting access token...");
            string accessToken = await GetAccessTokenAsync(refreshToken);
            if (progress != null) progress("Access token OK.");

            var    list      = new List<GoogleContact>();
            string nextToken = "";
            int    page      = 0;

            while (true)
            {
                page++;
                if (progress != null) progress("Fetching page " + page + "...");

                string url = PeopleBase +
                    "/people/me/connections" +
                    "?personFields=names,nicknames,phoneNumbers,emailAddresses," +
                    "addresses,urls,birthdays,organizations,biographies,metadata" +
                    "&pageSize=100";
                if (!string.IsNullOrEmpty(nextToken))
                    url += "&pageToken=" + Uri.EscapeDataString(nextToken);

                string json = await GetAsync(url, accessToken);
                if (progress != null) progress("Page " + page + " received.");

                string errMsg = JsonHelper.GetString(json, "error");
                if (!string.IsNullOrEmpty(errMsg))
                    throw new Exception("People API error: " + errMsg +
                        "\nRaw: " + json.Substring(0, Math.Min(300, json.Length)));

                JsonHelper.ParseContacts(json, list);
                if (progress != null) progress("Total: " + list.Count);

                nextToken = JsonHelper.GetString(json, "nextPageToken");
                if (string.IsNullOrEmpty(nextToken)) break;
            }

            if (progress != null) progress("Fetch complete: " + list.Count);
            return list;
        }
        // ================================================================
        // CREATE contact on Google
        // ================================================================
        public async Task<string> CreateContactAsync(
            string firstName, string lastName, string nickname,
            string notes,
            List<GPhone> phones, List<GEmail> emails,
            List<GAddress> addresses, List<GUrl> urls,
            List<GOrg> orgs, GDate birthday,
            string accessToken)
        {
            string body = JsonHelper.BuildPersonJson(
                firstName, lastName, nickname, notes,
                phones, emails, addresses, urls, orgs, birthday);

            string json = await PostAsync(
                PeopleBase + "/people:createContact",
                body, bearerToken: accessToken);

            string err = JsonHelper.GetString(json, "error");
            if (!string.IsNullOrEmpty(err))
            {
                // Return null on error — caller handles
                return null;
            }
            return JsonHelper.GetString(json, "resourceName");
        }

        // ================================================================
        // UPDATE contact on Google
        // ================================================================
        public async Task<bool> UpdateContactAsync(
            string resourceName, string etag,
            string firstName, string lastName, string nickname,
            string notes,
            List<GPhone> phones, List<GEmail> emails,
            List<GAddress> addresses, List<GUrl> urls,
            List<GOrg> orgs, GDate birthday,
            string accessToken)
        {
            string body = JsonHelper.BuildPersonJson(
                firstName, lastName, nickname, notes,
                phones, emails, addresses, urls, orgs, birthday,
                etag: etag);

            string id = resourceName.StartsWith("people/")
                ? resourceName : "people/" + resourceName;

            string url = PeopleBase + "/" + id +
                ":updateContact?updatePersonFields=" +
                "names,phoneNumbers,emailAddresses," +
                "organizations,urls,biographies,nicknames";

            // PATCH — use PostAsync with override
            string json = await PatchAsync(url, body, accessToken);

            string err = JsonHelper.GetString(json, "error");
            return string.IsNullOrEmpty(err);
        }

        // ================================================================
        // HTTP PATCH
        // ================================================================
        private Task<string> PatchAsync(string url, string body,
            string accessToken)
        {
            var tcs      = new TaskCompletionSource<string>();
            byte[] bytes = Encoding.UTF8.GetBytes(body);

            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method      = "PATCH";
            request.ContentType = "application/json";
            request.Accept      = "application/json";
            request.Headers[HttpRequestHeader.Authorization] =
                "Bearer " + accessToken;

            request.BeginGetRequestStream(arBody =>
            {
                try
                {
                    var req = (HttpWebRequest)arBody.AsyncState;
                    using (var s = req.EndGetRequestStream(arBody))
                        s.Write(bytes, 0, bytes.Length);

                    req.BeginGetResponse(arResp =>
                    {
                        try
                        {
                            var resp = (HttpWebResponse)
                                ((HttpWebRequest)arResp.AsyncState)
                                .EndGetResponse(arResp);
                            using (var r = new StreamReader(
                                resp.GetResponseStream()))
                                tcs.TrySetResult(r.ReadToEnd());
                        }
                        catch (WebException ex)
                        {
                            if (ex.Response != null)
                                try
                                {
                                    using (var r = new StreamReader(
                                        ex.Response.GetResponseStream()))
                                        tcs.TrySetResult(r.ReadToEnd());
                                }
                                catch { tcs.TrySetResult(""); }
                            else tcs.TrySetResult("");
                        }
                        catch { tcs.TrySetResult(""); }
                    }, req);
                }
                catch { tcs.TrySetResult(""); }
            }, request);

            return tcs.Task;
        }
    }
}
