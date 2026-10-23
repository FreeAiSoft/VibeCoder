using System;
using System.Net;
using System.Net.Http;

namespace VibeCoder.Core.Services
{
    /// <summary>
    /// Builds an <see cref="IWebProxy"/> (and matching <see cref="HttpClient"/>) from the
    /// user-configured proxy settings, so both the chat provider and speech-to-text pipeline
    /// can route their HTTP requests through the same proxy.
    /// </summary>
    public static class WebProxyBuilder
    {
        /// <summary>
        /// Creates a proxy for the given settings, or <c>null</c> when proxying is disabled
        /// or no host is configured.
        /// </summary>
        public static IWebProxy Build(bool enabled, string hostText, int port, string username, string password)
        {
            if (!enabled)
                return null;

            var host = (hostText ?? "").Trim().Trim('/');
            if (host.Length == 0)
                return null;

            // Allow a full URL (e.g. "http://proxy:3128") — extract host/port from it.
            if (Uri.TryCreate(host.Contains("://") ? host : "http://" + host, UriKind.Absolute, out var parsed) &&
                !string.IsNullOrEmpty(parsed.Host))
            {
                host = parsed.Host;
                // Only take the port from the pasted URL when it was specified explicitly
                // (IsDefaultPort is true for the scheme's implicit 80/443).
                if (!parsed.IsDefaultPort)
                    port = parsed.Port;
            }

            if (port <= 0 || port > 65535)
                port = 8080;

            var proxy = new WebProxy(host, port);
            if (!string.IsNullOrWhiteSpace(username))
                proxy.Credentials = new NetworkCredential(username, password ?? "");

            return proxy;
        }

        /// <summary>
        /// Creates an <see cref="HttpClient"/> that uses the given proxy, or a plain client
        /// when <paramref name="proxy"/> is <c>null</c>.
        /// </summary>
        public static HttpClient CreateHttpClient(IWebProxy proxy)
        {
            if (proxy == null)
                return new HttpClient();

            var handler = new HttpClientHandler
            {
                Proxy = proxy,
                UseProxy = true,
                UseDefaultCredentials = false
            };
            return new HttpClient(handler, disposeHandler: true);
        }
    }
}
