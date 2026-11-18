using System;
using System.IO;
using Agent.Core.Contracts;
using Agent.Core.Models;
using Newtonsoft.Json;

namespace VibeCoder.Services
{
    /// <summary>
    /// Loads/saves the UI state JSON at %AppData%\VibeCoder\ui_state.json, implementing the
    /// shared <see cref="IUiStateStore"/> contract so the shared <see cref="Agent.Models.ChatModel"/>
    /// persists its session state.
    /// </summary>
    public sealed class UiStateStore : IUiStateStore
    {
        public static string StateFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeCoder", "ui_state.json");

        public UiStateData Load()
        {
            try
            {
                if (File.Exists(StateFile))
                {
                    var json = File.ReadAllText(StateFile);
                    var state = JsonConvert.DeserializeObject<UiStateData>(json) ?? new UiStateData();
                    EnsureDefaults(state);
                    return state;
                }
            }
            catch
            {
                // ignore read errors — fall back to defaults
            }

            var defaults = new UiStateData();
            EnsureDefaults(defaults);
            return defaults;
        }

        public void Save(UiStateData state)
        {
            if (state == null) return;
            try
            {
                var directory = Path.GetDirectoryName(StateFile);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(StateFile, JsonConvert.SerializeObject(state, Formatting.Indented));
            }
            catch
            {
                // ignore write errors
            }
        }

        private static void EnsureDefaults(UiStateData state)
        {
            if (state == null) return;

            if (state.ChatViewMode != ChatViewMode.Conversation && state.ChatViewMode != ChatViewMode.History)
                state.ChatViewMode = ChatViewMode.Conversation;

            if (state.LastConversationId == null)
                state.LastConversationId = "";

            if (state.AgentMode != AgentMode.Plan && state.AgentMode != AgentMode.Act && state.AgentMode != AgentMode.Help)
                state.AgentMode = AgentMode.Plan;

            if (!Enum.IsDefined(typeof(MainTab), state.ActiveTab))
                state.ActiveTab = MainTab.Chat;
        }
    }
}
