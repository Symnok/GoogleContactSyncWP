// Helpers/CredentialStorage.cs
// Stores OAuth2 refresh token + ClientId/Secret in IsolatedStorage settings.
// WP8.1 Silverlight has no PasswordVault — use IsolatedStorageSettings instead.

using System.IO.IsolatedStorage;

namespace GoogleContactSyncWP.Helpers
{
    public static class CredentialStorage
    {
        private static readonly IsolatedStorageSettings _settings =
            IsolatedStorageSettings.ApplicationSettings;

        private const string KeyToken  = "RefreshToken";
        private const string KeyId     = "ClientId";
        private const string KeySecret = "ClientSecret";

        public static void SaveToken(string refreshToken)
        {
            _settings[KeyToken] = refreshToken;
            _settings.Save();
        }

        public static string LoadToken()
        {
            return _settings.Contains(KeyToken)
                ? _settings[KeyToken] as string : null;
        }

        public static void DeleteToken()
        {
            if (_settings.Contains(KeyToken))
                _settings.Remove(KeyToken);
            _settings.Save();
        }

        public static bool HasToken()
        {
            string t = LoadToken();
            return !string.IsNullOrEmpty(t);
        }

        public static void SaveCredentials(string clientId, string clientSecret)
        {
            _settings[KeyId]     = clientId;
            _settings[KeySecret] = clientSecret;
            _settings.Save();
        }

        public static string LoadClientId()
        {
            return _settings.Contains(KeyId)
                ? _settings[KeyId] as string : "";
        }

        public static string LoadClientSecret()
        {
            return _settings.Contains(KeySecret)
                ? _settings[KeySecret] as string : "";
        }

        public static void ClearAll()
        {
            _settings.Clear();
            _settings.Save();
        }
    }
}
