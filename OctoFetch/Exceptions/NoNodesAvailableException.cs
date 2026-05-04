namespace OctoFetch.Exceptions
{
    public class NoNodesAvailableException : OctoFetchException
    {
        public NoNodesAvailableException()
            : base("No active GitHub nodes available. Please add and test a node first.") { }
    }
}
