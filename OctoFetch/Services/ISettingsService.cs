using OctoFetch.Models;

namespace OctoFetch.Services
{
    public interface ISettingsService
    {
        AppSettings Load();
        void Save(AppSettings settings);
    }
}
