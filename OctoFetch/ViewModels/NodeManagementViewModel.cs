using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OctoFetch.Models;
using OctoFetch.Services;

namespace OctoFetch.ViewModels
{
    public partial class NodeManagementViewModel : ObservableObject
    {
        private readonly IGitHubService _gitHubService;
        private readonly IAppLogger _logger;
        private readonly AppSettings _settings;
        private readonly Action _onPersistAndRefresh;

        public ObservableCollection<CloudNode> Nodes { get; } = new();

        [ObservableProperty] private string _newRepoName = "Cloud-Downloads";
        [ObservableProperty] private bool _isTestingNodes;

        public NodeManagementViewModel(
            IGitHubService gitHubService,
            IAppLogger logger,
            AppSettings settings,
            Action onPersistAndRefresh)
        {
            _gitHubService = gitHubService;
            _logger = logger;
            _settings = settings;
            _onPersistAndRefresh = onPersistAndRefresh;
        }

        public Func<string?>? NewTokenProvider { get; set; }
        public Action? ClearNewTokenInput { get; set; }

        [RelayCommand]
        private void AddNode()
        {
            var token = NewTokenProvider?.Invoke();
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(NewRepoName))
            {
                _logger.Log(LogChannel.Settings, "⚠️ Both token and repo name are required.");
                return;
            }

            Nodes.Add(new CloudNode { Token = token!.Trim(), RepoName = NewRepoName.Trim() });
            ClearNewTokenInput?.Invoke();
            _logger.Log(LogChannel.Settings, "Added new node. Click TEST ALL NODES to verify.");
            _onPersistAndRefresh();
        }

        [RelayCommand]
        private void RemoveNode(CloudNode? node)
        {
            if (node is null) return;
            Nodes.Remove(node);
            _gitHubService.RemoveNode(node);
            _logger.Log(LogChannel.Settings, $"Removed node: {node.RepoName}");
            _onPersistAndRefresh();
        }

        [RelayCommand]
        private async Task ToggleNodeVisibilityAsync(CloudNode? node)
        {
            if (node is null) return;

            if (!node.IsConnected)
            {
                MessageBox.Show("Node must be connected before toggling visibility.",
                    "Not Connected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                await _gitHubService.ToggleNodeVisibilityAsync(node).ConfigureAwait(true);
                _logger.Log(LogChannel.Settings, $"Visibility of {node.RepoName} changed to {node.VisibilityText}");
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Failed to toggle visibility", ex);
                MessageBox.Show($"Failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private async Task TestAllNodesAsync()
        {
            IsTestingNodes = true;
            try
            {
                _logger.Log(LogChannel.Settings, "Testing all servers…");
                foreach (var node in Nodes.ToList())
                {
                    node.BadgeColor = "#F59E0B";
                    node.VisibilityText = "Testing…";
                    await _gitHubService.InitializeNodeAsync(node).ConfigureAwait(true);
                }
                _onPersistAndRefresh();
            }
            finally
            {
                IsTestingNodes = false;
            }
        }

        [RelayCommand]
        private void SaveSettings()
        {
            _onPersistAndRefresh();
            MessageBox.Show("Nodes saved successfully!", "Saved",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
