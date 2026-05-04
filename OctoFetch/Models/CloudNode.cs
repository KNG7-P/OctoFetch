using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
using Octokit;

namespace OctoFetch.Models
{
    public partial class CloudNode : ObservableObject
    {
        [ObservableProperty] private string _token = string.Empty;
        [ObservableProperty] private string _repoName = string.Empty;

        [JsonIgnore]
        [ObservableProperty]
        [property: JsonIgnore]
        private string? _username;

        [JsonIgnore]
        [ObservableProperty]
        [property: JsonIgnore]
        private bool _isConnected;

        [JsonIgnore]
        [ObservableProperty]
        [property: JsonIgnore]
        private string _badgeColor = "#E53935";

        [JsonIgnore]
        [ObservableProperty]
        [property: JsonIgnore]
        private string _visibilityText = "Unknown";

        [JsonIgnore]
        [ObservableProperty]
        [property: JsonIgnore]
        private bool _isPrivate;

        [JsonIgnore]
        [ObservableProperty]
        [property: JsonIgnore]
        private string _defaultBranch = "main";

        [JsonIgnore] public GitHubClient? Client { get; set; }

        [JsonIgnore]
        public string DisplayTitle
        {
            get
            {
                var maskedToken = MaskToken(Token);
                return string.IsNullOrEmpty(RepoName) ? maskedToken : $"{RepoName} ({maskedToken})";
            }
        }

        partial void OnTokenChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
        partial void OnRepoNameChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));

        private static string MaskToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "••••";
            if (token.Length <= 4) return new string('•', token.Length);
            return $"••••{token[^4..]}";
        }
    }
}
