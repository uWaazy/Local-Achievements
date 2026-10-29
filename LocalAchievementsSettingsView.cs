using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace LocalAchievements
{
    public class LocalAchievementsSettingsView : UserControl
    {
        public LocalAchievementsSettingsView()
        {
            var title = new TextBlock
            {
                Text = "Local Achievements",
                FontSize = 18,
                FontWeight = FontWeights.Bold
            };

            var subtitle = new TextBlock
            {
                Margin = new Thickness(0, 2, 0, 16),
                Opacity = 0.7
            };
            subtitle.SetResourceReference(TextBlock.TextProperty, "LOCLocalAchievementsSettingsSubtitle");

            var autoStartup = new CheckBox();
            autoStartup.SetResourceReference(ContentControl.ContentProperty, "LOCLocalAchievementsSettingsAutoStartup");
            autoStartup.SetBinding(CheckBox.IsCheckedProperty, new Binding("Settings.EnableAutoSyncOnStartup"));

            var autoPlaytime = new CheckBox { Margin = new Thickness(0, 10, 0, 0) };
            autoPlaytime.SetResourceReference(ContentControl.ContentProperty, "LOCLocalAchievementsSettingsAutoPlaytime");
            autoPlaytime.SetBinding(CheckBox.IsCheckedProperty, new Binding("Settings.AutoSyncPlaytime"));

            var hint = new TextBlock
            {
                Margin = new Thickness(0, 6, 0, 0),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            };
            hint.SetResourceReference(TextBlock.TextProperty, "LOCLocalAchievementsSettingsAutoPlaytimeHint");

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(title);
            panel.Children.Add(subtitle);
            panel.Children.Add(autoStartup);
            panel.Children.Add(autoPlaytime);
            panel.Children.Add(hint);

            Content = panel;
        }
    }
}
