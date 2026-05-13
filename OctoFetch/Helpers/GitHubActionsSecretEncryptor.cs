using System;
using System.Text;

namespace OctoFetch.Helpers
{
    internal static class GitHubActionsSecretEncryptor
    {
        public static string EncryptSecret(string secretValue, string publicKeyBase64)
        {
            if (string.IsNullOrEmpty(publicKeyBase64))
                throw new ArgumentException("Public key is required.", nameof(publicKeyBase64));

            var recipientPk = Convert.FromBase64String(publicKeyBase64);
            var plaintext = Encoding.UTF8.GetBytes(secretValue ?? string.Empty);

            var sealedBox = Sodium.SealedPublicKeyBox.Create(plaintext, recipientPk);
            return Convert.ToBase64String(sealedBox);
        }
    }
}
