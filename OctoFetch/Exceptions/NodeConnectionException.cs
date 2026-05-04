using System;

namespace OctoFetch.Exceptions
{
    public class NodeConnectionException : OctoFetchException
    {
        public string NodeName { get; }

        public NodeConnectionException(string nodeName, string message)
            : base($"[{nodeName}] {message}") => NodeName = nodeName;

        public NodeConnectionException(string nodeName, string message, Exception inner)
            : base($"[{nodeName}] {message}", inner) => NodeName = nodeName;
    }
}
