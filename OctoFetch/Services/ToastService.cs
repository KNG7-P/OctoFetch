using System;
using OctoFetch.Models;
 
#if WINDOWS10_0_17763_0_OR_GREATER
using Microsoft.Toolkit.Uwp.Notifications;
#endif
 
namespace OctoFetch.Services
{
    public class ToastService : IToastService
    {
        private readonly IAppLogger _logger;
        private readonly AppSettings _settings;

        public ToastService(IAppLogger logger, AppSettings settings)
        {
            _logger = logger;
            _settings = settings;
        }

        public void ShowSuccess(string title, string body) => Show(title, body, "✅");
        public void ShowFailure(string title, string body) => Show(title, body, "❌");
        public void ShowInfo(string title, string body) => Show(title, body, "ℹ️");

        private void Show(string title, string body, string iconPrefix)
        {
            if (!_settings.EnableToastNotifications) return;

#if WINDOWS10_0_17763_0_OR_GREATER
            try
            {
                new ToastContentBuilder()
                    .AddText($"{iconPrefix} {title}")
                    .AddText(body)
                    .AddAttributionText("OctoFetch")
                    .Show();
            }
            catch (Exception ex)
            {
                _logger.LogException(LogChannel.Settings, "Toast notification failed", ex);
            }
#else
            // No native toast support on this target framework. Log so the
            // user still sees the message inside the app.
            _logger.Log(LogChannel.Settings, $"{iconPrefix} {title} — {body}");
#endif
        }
    }
}
