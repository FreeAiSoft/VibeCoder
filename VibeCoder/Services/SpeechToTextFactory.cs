using System;
using VibeCoder.Core.Contracts;
using VibeCoder.Core.Services;

namespace VibeCoder.Services
{
    /// <summary>Creates the speech-to-text pipeline from settings (Whisper + optional Vosk fallback).</summary>
    public static class SpeechToTextFactory
    {
        public static ISpeechToTextService Create(SettingsData settings)
        {
            if (settings == null) settings = new SettingsData();

            var proxy = WebProxyBuilder.Build(
                settings.ProxyEnabled,
                settings.ProxyHost,
                settings.ProxyPort,
                settings.ProxyUsername,
                settings.ProxyPassword);

            var whisper = new OpenAiWhisperSttService(
                settings.BaseUrl,
                settings.ApiKey,
                settings.SpeechToTextModel,
                WebProxyBuilder.CreateHttpClient(proxy),
                LogService.Info);

            var provider = (settings.SpeechToTextProvider ?? "").Trim();
            if (string.Equals(provider, "Vosk", StringComparison.OrdinalIgnoreCase))
                return new VoskSttService(settings.VoskModelPath);

            if (string.Equals(provider, "OpenAiWhisper", StringComparison.OrdinalIgnoreCase))
                return whisper;

            // "Auto": Whisper first, fall back to Vosk when a model directory is configured.
            return new FallbackSpeechToTextService(whisper, new VoskSttService(settings.VoskModelPath));
        }
    }
}
