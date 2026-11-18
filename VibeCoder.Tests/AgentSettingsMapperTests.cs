using NUnit.Framework;
using Agent.Core.Models;
using Agent.Core.Services;
using VibeCoder.Core.Services;

namespace VibeCoder.Tests
{
    [TestFixture]
    public class AgentSettingsMapperTests
    {
        [Test]
        public void BuildPermissions_NullSettings_ReturnsNone()
        {
            Assert.That(AgentSettingsMapper.BuildPermissions(null), Is.EqualTo((AgentPermissions)0));
        }

        [Test]
        public void BuildPermissions_AllDisabled_ReturnsNone()
        {
            var settings = new SettingsData
            {
                AllowReadFiles = false,
                AllowEditFiles = false,
                AllowExecuteCommand = false,
                AllowWebFetch = false,
                AllowMcp = false,
                AllowSubagents = false,
            };

            Assert.That(AgentSettingsMapper.BuildPermissions(settings), Is.EqualTo((AgentPermissions)0));
        }

        [Test]
        public void BuildPermissions_AllEnabled_ReturnsAll()
        {
            var settings = new SettingsData
            {
                AllowReadFiles = true,
                AllowEditFiles = true,
                AllowExecuteCommand = true,
                AllowWebFetch = true,
                AllowMcp = true,
                AllowSubagents = true,
            };

            Assert.That(AgentSettingsMapper.BuildPermissions(settings), Is.EqualTo(AgentPermissions.All));
        }

        [Test]
        public void BuildPermissions_Subset_ReturnsMatchingFlags()
        {
            var settings = new SettingsData
            {
                AllowReadFiles = true,
                AllowEditFiles = true,
                AllowExecuteCommand = false,
                AllowWebFetch = false,
                AllowMcp = false,
                AllowSubagents = false,
            };

            Assert.That(
                AgentSettingsMapper.BuildPermissions(settings),
                Is.EqualTo(AgentPermissions.ReadFiles | AgentPermissions.EditFiles));
        }

        [Test]
        public void ParseMemoryBankMode_ToolAndConvention_AreCaseInsensitive()
        {
            Assert.That(AgentSettingsMapper.ParseMemoryBankMode("Tool"), Is.EqualTo(MemoryBankMode.Tool));
            Assert.That(AgentSettingsMapper.ParseMemoryBankMode("tool"), Is.EqualTo(MemoryBankMode.Tool));
            Assert.That(AgentSettingsMapper.ParseMemoryBankMode("Convention"), Is.EqualTo(MemoryBankMode.Convention));
            Assert.That(AgentSettingsMapper.ParseMemoryBankMode("convention"), Is.EqualTo(MemoryBankMode.Convention));
        }

        [Test]
        public void ParseMemoryBankMode_UnknownOrEmpty_FallsBackToDisabled()
        {
            Assert.That(AgentSettingsMapper.ParseMemoryBankMode(null), Is.EqualTo(MemoryBankMode.Disabled));
            Assert.That(AgentSettingsMapper.ParseMemoryBankMode(""), Is.EqualTo(MemoryBankMode.Disabled));
            Assert.That(AgentSettingsMapper.ParseMemoryBankMode("Disabled"), Is.EqualTo(MemoryBankMode.Disabled));
            Assert.That(AgentSettingsMapper.ParseMemoryBankMode("Bogus"), Is.EqualTo(MemoryBankMode.Disabled));
        }
    }
}
