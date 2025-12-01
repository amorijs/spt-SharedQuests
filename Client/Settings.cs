using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SharedQuests
{
    /// <summary>
    /// Configuration settings for SharedQuests
    /// </summary>
    internal class Settings
    {
        private const string GeneralSection = "1. General";
        private const string ProfilesSection = "2. Profile Visibility";
        
        private static ConfigFile _config;
        
        // General settings
        public static ConfigEntry<bool> Enabled;
        
        // Store excluded profiles as comma-separated string
        public static ConfigEntry<string> ExcludedProfiles;
        
        // Dynamic profile checkboxes - created after fetching from server
        private static Dictionary<string, ConfigEntry<bool>> _profileVisibility = new Dictionary<string, ConfigEntry<bool>>();
        
        // Cache of excluded profile names for quick lookup
        private static HashSet<string> _excludedProfilesCache = new HashSet<string>();

        public static void Init(ConfigFile config)
        {
            _config = config;
            
            Enabled = config.Bind(
                GeneralSection,
                "Enable SharedQuests",
                true,
                new ConfigDescription(
                    "Enable or disable the SharedQuests status display",
                    null,
                    new ConfigurationManagerAttributes { Order = 100 }));

            ExcludedProfiles = config.Bind(
                ProfilesSection,
                "Excluded Profiles",
                "",
                new ConfigDescription(
                    "Currently excluded profile names (managed by checkboxes above)",
                    null,
                    new ConfigurationManagerAttributes { Order = 0, ReadOnly = true }));
            
            // Load excluded profiles into cache
            UpdateExcludedCache();
            
            // Subscribe to changes
            ExcludedProfiles.SettingChanged += (sender, args) => UpdateExcludedCache();
        }

        /// <summary>
        /// Update the profile visibility checkboxes based on fetched profiles
        /// </summary>
        public static void UpdateProfileList(IEnumerable<string> profileNames)
        {
            if (_config == null) return;
            
            int order = 50;
            foreach (var profileName in profileNames)
            {
                if (string.IsNullOrEmpty(profileName)) continue;
                
                // Check if this profile is currently excluded
                bool isVisible = !_excludedProfilesCache.Contains(profileName);
                
                // Create or update the config entry
                if (!_profileVisibility.ContainsKey(profileName))
                {
                    var entry = _config.Bind(
                        ProfilesSection,
                        $"Show {profileName}",
                        isVisible,
                        new ConfigDescription(
                            $"Show quest status for profile '{profileName}'",
                            null,
                            new ConfigurationManagerAttributes { Order = order-- }));
                    
                    // Subscribe to changes
                    entry.SettingChanged += (sender, args) => OnProfileVisibilityChanged(profileName, entry.Value);
                    
                    _profileVisibility[profileName] = entry;
                    
                    Plugin.LogSource?.LogDebug($"SharedQuests: Created config entry for profile '{profileName}'");
                }
                else
                {
                    // Update existing entry if needed
                    _profileVisibility[profileName].Value = isVisible;
                }
            }
        }

        /// <summary>
        /// Called when a profile visibility checkbox is changed
        /// </summary>
        private static void OnProfileVisibilityChanged(string profileName, bool isVisible)
        {
            if (isVisible)
            {
                // Remove from excluded list
                _excludedProfilesCache.Remove(profileName);
            }
            else
            {
                // Add to excluded list
                _excludedProfilesCache.Add(profileName);
            }
            
            // Update the config string
            ExcludedProfiles.Value = string.Join(",", _excludedProfilesCache);
            
            Plugin.LogSource?.LogInfo($"SharedQuests: Profile '{profileName}' visibility changed to {isVisible}");
        }

        /// <summary>
        /// Update the excluded profiles cache from the config string
        /// </summary>
        private static void UpdateExcludedCache()
        {
            _excludedProfilesCache.Clear();
            
            if (!string.IsNullOrEmpty(ExcludedProfiles.Value))
            {
                var names = ExcludedProfiles.Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var name in names)
                {
                    _excludedProfilesCache.Add(name.Trim());
                }
            }
        }

        /// <summary>
        /// Check if a profile should be displayed
        /// </summary>
        public static bool IsProfileVisible(string profileName)
        {
            if (!Enabled.Value) return false;
            return !_excludedProfilesCache.Contains(profileName);
        }

        /// <summary>
        /// Get all visible profile names
        /// </summary>
        public static IEnumerable<string> GetVisibleProfiles(IEnumerable<string> allProfiles)
        {
            return allProfiles.Where(p => IsProfileVisible(p));
        }
    }

    /// <summary>
    /// Configuration manager attributes for F12 menu customization
    /// </summary>
#pragma warning disable CS0649 // Field is never assigned to
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? ShowRangeAsPercent;
        public System.Action<ConfigEntryBase> CustomDrawer;
        public bool? Browsable;
        public string Category;
        public object DefaultValue;
        public bool? HideDefaultButton;
        public bool? HideSettingName;
        public string Description;
        public string DispName;
        public int? Order;
        public bool? ReadOnly;
        public bool? IsAdvanced;
        public System.Func<object, string> ObjToStr;
        public System.Func<string, object> StrToObj;
    }
#pragma warning restore CS0649
}

