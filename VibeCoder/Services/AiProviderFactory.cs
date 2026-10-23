using System;
using VibeCoder.Core.Contracts;
using VibeCoder.Core.Models;
using VibeCoder.Core.Services;

namespace VibeCoder.Services
{
    /// <summary>Creates an AI provider from settings: no API key → stub, otherwise OpenAI-compatible.</summary>
    public static class AiProviderFactory
    {
        public static IAiProvider Create(SettingsData settings)
        {
            var apiKey = (settings?.ApiKey ?? "").Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
                return new StubAiProvider();

            var toolCallingMode = string.Equals(settings?.ToolCallingMode, "Native", StringComparison.OrdinalIgnoreCase)
                ? ToolCallingMode.Native
                : ToolCallingMode.JsonChat;

            var proxy = WebProxyBuilder.Build(
                settings?.ProxyEnabled ?? false,
                settings?.ProxyHost,
                settings?.ProxyPort ?? 8080,
                settings?.ProxyUsername,
                settings?.ProxyPassword);

            return new OpenAiCompatibleProvider(new OpenAiOptions
            {
                BaseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl) ? "https://api.openai.com/v1" : settings.BaseUrl.Trim(),
                ApiKey = apiKey,
                Model = string.IsNullOrWhiteSpace(settings.Model) ? "gpt-4o-mini" : settings.Model.Trim(),
                ToolCallingMode = toolCallingMode,
                ContextWindowSize = settings.ContextWindowSize > 0 ? settings.ContextWindowSize : 409600,
                TimeoutSeconds = settings.TimeoutSeconds > 0 ? settings.TimeoutSeconds : 300,
                RetryTimeout = TimeSpan.FromMinutes(settings.RetryTimeoutMinutes > 0 ? settings.RetryTimeoutMinutes : 30),
                RetryBaseDelay = TimeSpan.FromSeconds(settings.RetryBaseDelaySeconds > 0 ? settings.RetryBaseDelaySeconds : 1),
                RetryMaxDelay = TimeSpan.FromSeconds(settings.RetryMaxDelaySeconds > 0 ? settings.RetryMaxDelaySeconds : 30),
                InputPricePerMillion = settings.InputPricePerMillion >= 0 ? settings.InputPricePerMillion : 0.27,
                CacheHitPricePerMillion = settings.CacheHitPricePerMillion >= 0 ? settings.CacheHitPricePerMillion : 0.07,
                OutputPricePerMillion = settings.OutputPricePerMillion >= 0 ? settings.OutputPricePerMillion : 1.10,
                Temperature = Math.Max(0.0, Math.Min(1.0, settings.Temperature))
            }, WebProxyBuilder.CreateHttpClient(proxy));
        }
    }
}
