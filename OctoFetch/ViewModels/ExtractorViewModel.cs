using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OctoFetch.Services;

namespace OctoFetch.ViewModels
{
    public partial class ExtractorViewModel : ObservableObject
    {
        private readonly IExtractorService _extractorService;
        private readonly IAppLogger _logger;

        [ObservableProperty] private string _displayPath = string.Empty;
        [ObservableProperty] private bool _canExtract;

        private string[] _selectedFiles = Array.Empty<string>();

        public ExtractorViewModel(IExtractorService extractorService, IAppLogger logger)
        {
            _extractorService = extractorService;
            _logger = logger;
        }

        [RelayCommand]
        private void Browse()
        {
            try
            {
                var ofd = new OpenFileDialog
                {
                    Multiselect = true,
                    Filter = "ZIP Parts (*.zip.001)|*.zip.001|All files (*.*)|*.*",
                    Title = "Select ALL downloaded parts",
                };
                if (ofd.ShowDialog() != true) return;

                _selectedFiles = ofd.FileNames;
                DisplayPath = _selectedFiles.Length > 1
                    ? $"{_selectedFiles.Length} parts selected"
                    : _selectedFiles[0];
                CanExtract = _extractorService.VerifySelectedParts(_selectedFiles);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Extractor, "File dialog error", ex);
            }
        }

        [RelayCommand(CanExecute = nameof(CanExtractCommand))]
        private async Task ExtractAsync()
        {
            CanExtract = false;
            try
            {
                var first = _selectedFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).First();
                await _extractorService.ExtractAsync(first).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Extractor, "Extraction error", ex);
                MessageBox.Show(ex.Message, "Extraction error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                CanExtract = true;
            }
        }

        private bool CanExtractCommand() => CanExtract;

        partial void OnCanExtractChanged(bool value) => ExtractCommand.NotifyCanExecuteChanged();
    }
}
