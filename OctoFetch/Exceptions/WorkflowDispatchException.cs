using System;

namespace OctoFetch.Exceptions
{
    public class WorkflowDispatchException : OctoFetchException
    {
        public WorkflowDispatchException(string message) : base(message) { }
        public WorkflowDispatchException(string message, Exception inner) : base(message, inner) { }
    }
}
