using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using Agent.Core.Services;
using Agent.Mvvm;
using Agent.Models;
using Agent.Toolkit.Wpf.Controls;
using VibeCoder.Services;

namespace VibeCoder
{
    /// <summary>
    /// Thin VSIX shell: hosts the shared <see cref="ChatControl"/> and <see cref="SettingsControl"/>
    /// and wires them to the VS-specific <see cref="VibeCoderChatHost"/>.
    /// </summary>
    public partial class VibeCoderToolWindowControl : UserControl
    {
        private VibeCoderChatHost _host;
        private SettingsModel _settingsModel;

        public VibeCoderToolWindowControl()
        {
            InitializeComponent();

            _host = new VibeCoderChatHost(Chat, Resources);
            Chat.Host = _host;
            Chat.DataContext = _host.Chat;
            Chat.Settings = _host.Settings;
            Chat.SettingsPersistence = new VibeCoderSettingsStore();
            Chat.AttachServices(new MicrophoneRecorder(), SpeechToTextFactory.Create(_host.Settings));

            WireSettings();
        }

        private void WireSettings()
        {
            _settingsModel = new SettingsModel(new VibeCoderSettingsStore());
            _settingsModel.Run();
            _settingsModel.SettingsSaved += OnSettingsSaved;

            var vm = new SettingsViewModel(_settingsModel);
            vm.SetModel(_settingsModel, null);
            Settings.DataContext = vm;
        }

        private void OnSettingsSaved(object sender, EventArgs e)
        {
            _host.SaveSettings(_settingsModel.ToSettingsData());
        }

        /// <summary>Feeds an externally produced message (e.g. an Error List item) into the chat.</summary>
        public async Task SubmitErrorToChatAsync(string text)
        {
            await _host.SubmitErrorToChatAsync(text);
        }

        /// <summary>Appends a system notice to the chat (used by the external VS commands).</summary>
        public void AppendSystemMessage(string text) => _host.AppendSystemMessage(text);

        /// <summary>Appends text to the chat input box (used by the external VS commands).</summary>
        public void AppendSelectionToInput(string text) => _host.AppendSelectionToInput(text);

    }

    /// <summary>DataContext adapter exposing the settings model and the command dispatcher to the shared settings panel.</summary>
    internal sealed class SettingsViewModel : BaseViewModel
    {
        public SettingsModel Settings { get; }

        public SettingsViewModel(SettingsModel settings)
        {
            Settings = settings;
        }
    }
}
