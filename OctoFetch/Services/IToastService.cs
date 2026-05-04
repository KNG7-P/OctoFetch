using OctoFetch.Models;

namespace OctoFetch.Services
{
    public interface IToastService
    {
        void ShowSuccess(string title, string body);
        void ShowFailure(string title, string body);
        void ShowInfo(string title, string body);
    }
}