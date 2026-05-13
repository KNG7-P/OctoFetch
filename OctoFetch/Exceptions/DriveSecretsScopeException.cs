using System;

namespace OctoFetch.Exceptions
{
    public sealed class DriveSecretsScopeException : OctoFetchException
    {
        public string RepoName { get; }
        public int StatusCode { get; }

        public DriveSecretsScopeException(string repoName, int statusCode, string message)
            : base(message)
        {
            RepoName = repoName;
            StatusCode = statusCode;
        }
    }
}
