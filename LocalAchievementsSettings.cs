using Playnite.SDK;
using Playnite.SDK.Data;
using System.Collections.Generic;

namespace LocalAchievements
{
    public class LocalAchievementsSettings : ObservableObject
    {
        private string steamApiKey = string.Empty;
        private string steamId64 = string.Empty;
        private bool enableAutoSyncOnStartup = true;

        public string SteamApiKey { get => steamApiKey; set => SetValue(ref steamApiKey, value); }
        public string SteamId64 { get => steamId64; set => SetValue(ref steamId64, value); }
        public bool EnableAutoSyncOnStartup { get => enableAutoSyncOnStartup; set => SetValue(ref enableAutoSyncOnStartup, value); }
    }

    public class LocalAchievementsSettingsViewModel : ObservableObject, ISettings
    {
        private readonly LocalAchievements plugin;
        private LocalAchievementsSettings editingClone { get; set; }

        private LocalAchievementsSettings settings;
        public LocalAchievementsSettings Settings { get => settings; set => SetValue(ref settings, value); }

        public LocalAchievementsSettingsViewModel(LocalAchievements plugin)
        {
            this.plugin = plugin;
            var savedSettings = plugin.LoadPluginSettings<LocalAchievementsSettings>();

            if (savedSettings != null)
            {
                Settings = savedSettings;
            }
            else
            {
                Settings = new LocalAchievementsSettings();
            }
        }

        public void BeginEdit()
        {
            editingClone = Serialization.GetClone(Settings);
        }

        public void CancelEdit()
        {
            Settings = editingClone;
        }

        public void EndEdit()
        {
            plugin.SavePluginSettings(Settings);
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();
            return true;
        }
    }
}