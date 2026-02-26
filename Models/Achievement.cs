using System;

namespace LocalAchievements.Models
{
    public class Achievement
    {
        public string ApiName { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public bool Unlocked { get; set; }
        public DateTime UnlockTime { get; set; }
        public string IconUrl { get; set; }
        public bool IsLocal { get; set; }
    }
}
