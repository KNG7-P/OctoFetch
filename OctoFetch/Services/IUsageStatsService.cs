using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OctoFetch.Models;

namespace OctoFetch.Services
{
    public enum ChartRange
    {
        Daily,
        Weekly,
        Monthly,
    }
    public interface IUsageStatsService
    {
        void Record(UsageEvent ev);

        IReadOnlyList<UsageEvent> GetEvents();

        void Clear();

        Task BackfillFromGitHubAsync(
            IGitHubService gitHubService,
            DateTime sinceUtc,
            CancellationToken cancellationToken = default);

        IReadOnlyList<ChartBucket> Bucketize(ChartRange range, int bucketCount);
    }
}
