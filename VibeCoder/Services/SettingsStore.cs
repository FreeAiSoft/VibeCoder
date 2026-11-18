using System;
using System.IO;
using Newtonsoft.Json;
using Agent.Core.Contracts;
using Agent.Core.Services;

namespace VibeCoder.Services
{
    /// <summary>
    /// Loads/saves the shared <see cref="Agent.Core.Services.SettingsData"/> at
    /// %AppData%\VibeCoder\settings.json. VibeCoder keeps its own storage folder; the shared
    /// static <see cref="Agent.Core.Services.SettingsStore"/> targets the RPAWriter2 folder and
    /// is therefore not used here.
    /// </summary>
    public static class SettingsStore
    {
        public static string SettingsDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeCoder");

        public static string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

        public static SettingsData Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    var settings = JsonConvert.DeserializeObject<SettingsData>(json) ?? new SettingsData();
                    SettingsData.Normalize(settings);
                    return settings;
                }
            }
            catch
            {
                // ignore read errors - fall back to defaults
            }
            var defaults = new SettingsData();
            SettingsData.Normalize(defaults);
            return defaults;
        }

        public static void Save(SettingsData settings)
        {
            if (settings == null) return;
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                File.WriteAllText(SettingsFile, JsonConvert.SerializeObject(settings, Formatting.Indented));
            }
            catch
            {
                // ignore write errors
            }
        }
    }

    /// <summary>ISettingsStore adapter over the static <see cref="SettingsStore"/>.</summary>
    public sealed class VibeCoderSettingsStore : ISettingsStore
    {
        public SettingsData Load() => SettingsStore.Load();

        public void Save(SettingsData settings) => SettingsStore.Save(settings);
    }
}
