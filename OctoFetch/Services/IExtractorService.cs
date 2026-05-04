using System.Threading;
using System.Threading.Tasks;

namespace OctoFetch.Services
{
    public interface IExtractorService
    {
        bool VerifySelectedParts(string[] files);
        Task ExtractAsync(string firstPartPath, CancellationToken cancellationToken = default);
    }
}
