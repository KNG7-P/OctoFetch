using System;

namespace OctoFetch.Exceptions
{
    public class OctoFetchException : Exception
    {
        public OctoFetchException(string message) : base(message) { }
        public OctoFetchException(string message, Exception inner) : base(message, inner) { }
    }
}
