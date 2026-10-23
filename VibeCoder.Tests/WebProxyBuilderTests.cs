using System.Net;
using NUnit.Framework;
using VibeCoder.Core.Services;

namespace VibeCoder.Tests
{
    [TestFixture]
    public class WebProxyBuilderTests
    {
        [Test]
        public void Build_Disabled_ReturnsNull()
        {
            Assert.That(WebProxyBuilder.Build(false, "proxy.example.com", 8080, "", ""), Is.Null);
        }

        [Test]
        public void Build_NoHost_ReturnsNull()
        {
            Assert.That(WebProxyBuilder.Build(true, "", 8080, "", ""), Is.Null);
            Assert.That(WebProxyBuilder.Build(true, "   ", 8080, "", ""), Is.Null);
        }

        [Test]
        public void Build_HostAndPort_ReturnsWebProxy()
        {
            var proxy = WebProxyBuilder.Build(true, "proxy.example.com", 3128, "", "") as WebProxy;
            Assert.That(proxy, Is.Not.Null);
            Assert.That(proxy.Address.Host, Is.EqualTo("proxy.example.com"));
            Assert.That(proxy.Address.Port, Is.EqualTo(3128));
        }

        [Test]
        public void Build_InvalidPort_FallsBackTo8080()
        {
            var proxy = WebProxyBuilder.Build(true, "proxy.example.com", 0, "", "") as WebProxy;
            Assert.That(proxy.Address.Port, Is.EqualTo(8080));

            proxy = WebProxyBuilder.Build(true, "proxy.example.com", 99999, "", "") as WebProxy;
            Assert.That(proxy.Address.Port, Is.EqualTo(8080));
        }

        [Test]
        public void Build_FullUrl_ExtractsHostAndPort()
        {
            var proxy = WebProxyBuilder.Build(true, "http://proxy.example.com:9000", 0, "", "") as WebProxy;
            Assert.That(proxy.Address.Host, Is.EqualTo("proxy.example.com"));
            Assert.That(proxy.Address.Port, Is.EqualTo(9000));
        }

        [Test]
        public void Build_WithCredentials_SetsNetworkCredential()
        {
            var proxy = WebProxyBuilder.Build(true, "proxy.example.com", 8080, "user", "pass") as WebProxy;
            var credential = proxy.Credentials as NetworkCredential;
            Assert.That(credential, Is.Not.Null);
            Assert.That(credential.UserName, Is.EqualTo("user"));
            Assert.That(credential.Password, Is.EqualTo("pass"));
        }

        [Test]
        public void Build_WithoutCredentials_LeavesCredentialsNull()
        {
            var proxy = WebProxyBuilder.Build(true, "proxy.example.com", 8080, "", "") as WebProxy;
            Assert.That(proxy.Credentials, Is.Null);
        }

        [Test]
        public void CreateHttpClient_NullProxy_ReturnsClient()
        {
            using (var client = WebProxyBuilder.CreateHttpClient(null))
            {
                Assert.That(client, Is.Not.Null);
            }
        }
    }
}
