using System;
using Agent.Core.Models;
using Agent.Core.Services;

namespace VibeCoder.Core.Services
{
    /// <summary>
    /// Maps user settings (<see cref="SettingsData"/>) to the agent runtime configuration
    /// (<see cref="AgentPermissions"/> and <see cref="MemoryBankMode"/>). Kept in Core
    /// (no WPF/VS dependency) so the mapping can be unit-tested.
    /// </summary>
    public static class AgentSettingsMapper
    {
        /// <summary>Builds the permission flags from the boolean settings switches.</summary>
        public static AgentPermissions BuildPermissions(SettingsData settings)
        {
            var allowed = (AgentPermissions)0;
            if (settings == null)
                return allowed;

            if (settings.AllowReadFiles) allowed |= AgentPermissions.ReadFiles;
            if (settings.AllowEditFiles) allowed |= AgentPermissions.EditFiles;
            if (settings.AllowExecuteCommand) allowed |= AgentPermissions.ExecuteCommand;
            if (settings.AllowWebFetch) allowed |= AgentPermissions.WebFetch;
            if (settings.AllowMcp) allowed |= AgentPermissions.Mcp;
            if (settings.AllowSubagents) allowed |= AgentPermissions.Subagents;
            return allowed;
        }

        /// <summary>Parses the memory-bank mode string; unknown/empty values fall back to <see cref="MemoryBankMode.Disabled"/>.</summary>
        public static MemoryBankMode ParseMemoryBankMode(string value)
        {
            if (string.Equals(value, "Tool", StringComparison.OrdinalIgnoreCase)) return MemoryBankMode.Tool;
            if (string.Equals(value, "Convention", StringComparison.OrdinalIgnoreCase)) return MemoryBankMode.Convention;
            return MemoryBankMode.Disabled;
        }
    }
}
