using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using OctoFetch.Models;

namespace OctoFetch.Services
{
    public class UsageStatsService : IUsageStatsService
    {
        private readonly IAppLogger _logger;
        private readonly string _path;
        private readonly object _lock = new();
        private List<UsageEvent> _events = new();

        public UsageStatsService(IAppLogger logger)
        {
            _logger = logger;
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OctoFetch");
            Directory.CreateDirectory(folder);
            _path = Path.Combine(folder, "usage.json");

            Load();
        }

        // -- Persistence ------------------------------------------------------
        private void Load()
        {
            if (!File.Exists(_path)) return;
            try
            {
                var raw = File.ReadAllText(_path);
                var parsed = JsonConvert.DeserializeObject<List<UsageEvent>>(raw);
                if (parsed != null) _events = parsed;
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.General,
                    "Failed to load usage.json — starting fresh", ex);
                _events = new List<UsageEvent>();
            }
        }

        private void SaveLocked()
        {
            try
            {
                var json = JsonConvert.SerializeObject(_events, Formatting.Indented);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.General, "Failed to save usage.json", ex);
            }
        }

        // -- Public API -------------------------------------------------------
        public void Record(UsageEvent ev)
        {
            if (ev == null) return;
            if (string.IsNullOrWhiteSpace(ev.Id))
                ev.Id = Guid.NewGuid().ToString("N");

            lock (_lock)
            {
                if (_events.Any(e => string.Equals(e.Id, ev.Id, StringComparison.Ordinal)))
                    return;
                _events.Add(ev);
                SaveLocked();
            }
        }

        public IReadOnlyList<UsageEvent> GetEvents()
        {
            lock (_lock) return _events.ToArray();
        }

        public void Clear()
        {
            lock (_lock)
            {
                _events.Clear();
                SaveLocked();
            }
        }

        public async Task BackfillFromGitHubAsync(
            IGitHubService gitHubService,
            DateTime sinceUtc,
            CancellationToken cancellationToken = default)
        {
            if (gitHubService == null) return;
            try
            {
                var commits = await gitHubService
                    .GetRecentDownloadCommitsAsync(sinceUtc, cancellationToken)
                    .ConfigureAwait(false);

                lock (_lock)
                {
                    var known = new HashSet<string>(
                        _events.Select(e => e.Id), StringComparer.Ordinal);

                    var liveBuckets = _events
                        .Where(e => e.Source == "live")
                        .Select(e => (e.ServerRepo, Floor: e.TimestampUtc.AddMinutes(-5),
                                     Ceil: e.TimestampUtc.AddMinutes(5)))
                        .ToArray();

                    foreach (var c in commits)
                    {
                        if (string.IsNullOrEmpty(c.Sha) || known.Contains(c.Sha)) continue;

                        if (!LooksLikeDownloadCommit(c)) continue;

                        if (liveBuckets.Any(b =>
                                string.Equals(b.ServerRepo, c.RepoName, StringComparison.OrdinalIgnoreCase) &&
                                c.CommitDateUtc >= b.Floor && c.CommitDateUtc <= b.Ceil))
                        {
                            known.Add(c.Sha);
                            continue;
                        }

                        _events.Add(new UsageEvent
                        {
                            Id = c.Sha,
                            TimestampUtc = c.CommitDateUtc,
                            Bytes = 0,
                            Tag = InferTagFromCommitMessage(c.Message),
                            ServerRepo = c.RepoName,
                            Source = "backfill",
                        });
                        known.Add(c.Sha);
                    }

                    SaveLocked();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.General, "Stats backfill failed", ex);
            }
        }

        private static readonly string[] BookkeepingPhrases =
        {
            "delete folder", "delete folders", "delete file", "delete files",
            "remove folder", "remove file",
            "rename folder", "rename file", "rename ",
            "move ", "rewrite ", "merge ", "initial commit", "init repo",
            "gitkeep", "checksum",
        };

        private static readonly string[] WorkflowBotLogins =
        {
            "github-actions[bot]",
            "github-actions",
            "actions-user",
        };

        private static bool LooksLikeDownloadCommit(DownloadCommitInfo c)
        {
            if (c == null) return false;
            var message = c.Message ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message)) return false;

            var lowered = message.ToLowerInvariant();
            foreach (var phrase in BookkeepingPhrases)
                if (lowered.Contains(phrase)) return false;

            var author = c.AuthorLogin ?? string.Empty;
            foreach (var bot in WorkflowBotLogins)
                if (string.Equals(author, bot, StringComparison.OrdinalIgnoreCase))
                    return true;

            return message.IndexOf("downloads/", StringComparison.OrdinalIgnoreCase) >= 0
                && (message.StartsWith("Add ", StringComparison.OrdinalIgnoreCase)
                    || message.StartsWith("Upload ", StringComparison.OrdinalIgnoreCase)
                    || message.StartsWith("Download ", StringComparison.OrdinalIgnoreCase));
        }

        private static string InferTagFromCommitMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "Other";
            var idx = message.IndexOf("downloads/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return "Other";
            var rest = message.Substring(idx + "downloads/".Length);
            var sep = rest.IndexOf("__", StringComparison.Ordinal);
            if (sep <= 0 || sep > 32) return "Other";
            return rest.Substring(0, sep);
        }

        public IReadOnlyList<ChartBucket> Bucketize(ChartRange range, int bucketCount)
        {
            if (bucketCount <= 0) bucketCount = 1;

            var nowUtc = DateTime.UtcNow;
            var buckets = new List<ChartBucket>(bucketCount);
            UsageEvent[] events;
            lock (_lock) events = _events.ToArray();

            for (int i = 0; i < bucketCount; i++)
            {
                var (start, end, label) = ComputeWindow(range, nowUtc, bucketCount - 1 - i);
                var hits = events
                    .Where(e => e.TimestampUtc >= start && e.TimestampUtc < end)
                    .ToArray();

                buckets.Add(new ChartBucket
                {
                    StartUtc = start,
                    EndUtc = end,
                    Label = label,
                    Count = hits.Length,
                    Bytes = hits.Sum(h => h.Bytes),
                });
            }

            var peak = buckets.Max(b => b.Count);
            foreach (var b in buckets)
            {
                b.HeightFraction = peak == 0 ? 0 : (double)b.Count / peak;
                b.Tooltip = b.Bytes > 0
                    ? $"{b.Label}: {b.Count} download(s) · {FormatBytes(b.Bytes)}"
                    : $"{b.Label}: {b.Count} download(s)";
            }

            return buckets;
        }

        // -- Helpers ----------------------------------------------------------
        private static (DateTime start, DateTime end, string label)
            ComputeWindow(ChartRange range, DateTime nowUtc, int slotsBack)
        {
            switch (range)
            {
                case ChartRange.Daily:
                    {
                        var end = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day,
                            0, 0, 0, DateTimeKind.Utc).AddDays(1 - slotsBack);
                        var start = end.AddDays(-1);
                        var label = start.ToString("ddd", CultureInfo.InvariantCulture);
                        return (start, end, label);
                    }
                case ChartRange.Weekly:
                    {
                        var todayUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day,
                            0, 0, 0, DateTimeKind.Utc);
                        var diff = ((int)todayUtc.DayOfWeek + 6) % 7;
                        var thisWeekStart = todayUtc.AddDays(-diff);
                        var start = thisWeekStart.AddDays(-7 * slotsBack);
                        var end = start.AddDays(7);
                        var label = "W" + ISOWeek.GetWeekOfYear(start)
                            .ToString(CultureInfo.InvariantCulture);
                        return (start, end, label);
                    }
                case ChartRange.Monthly:
                default:
                    {
                        var thisMonth = new DateTime(nowUtc.Year, nowUtc.Month, 1,
                            0, 0, 0, DateTimeKind.Utc);
                        var start = thisMonth.AddMonths(-slotsBack);
                        var end = start.AddMonths(1);
                        var label = start.ToString("MMM", CultureInfo.InvariantCulture);
                        return (start, end, label);
                    }
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            string[] units = { "KB", "MB", "GB", "TB" };
            double v = bytes;
            int u = -1;
            do { v /= 1024.0; u++; }
            while (v >= 1024 && u < units.Length - 1);
            return $"{v:0.##} {units[u]}";
        }
    }
}
