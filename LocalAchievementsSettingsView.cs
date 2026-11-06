using LocalAchievements.Services;
using Playnite.SDK;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace LocalAchievements
{
    public class LocalAchievementsSettingsView : UserControl
    {
        public LocalAchievementsSettingsView(Action checkForUpdates)
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

            var autoReapply = new CheckBox { Margin = new Thickness(0, 16, 0, 0) };
            autoReapply.SetResourceReference(ContentControl.ContentProperty, "LOCLocalAchievementsSettingsAutoReapply");
            autoReapply.SetBinding(CheckBox.IsCheckedProperty, new Binding("Settings.AutoReapplyAchievements"));

            var hint = new TextBlock
            {
                Margin = new Thickness(0, 6, 0, 0),
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            };
            hint.SetResourceReference(TextBlock.TextProperty, "LOCLocalAchievementsSettingsAutoPlaytimeHint");

            var openLog = new Button
            {
                Margin = new Thickness(0, 20, 0, 0),
                Padding = new Thickness(14, 5, 14, 5),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            openLog.SetResourceReference(ContentControl.ContentProperty, "LOCLocalAchievementsSettingsOpenLog");
            openLog.Click += (sender, e) => AdvancedLogger.OpenLogLocation();

            var updateLabel = new TextBlock
            {
                Margin = new Thickness(0, 24, 0, 6),
                FontWeight = FontWeights.Bold
            };
            updateLabel.SetResourceReference(TextBlock.TextProperty, "LOCLocalAchievementsSettingsUpdateMode");

            var updateMode = new ComboBox
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 320
            };
            updateMode.Items.Add(ResourceProvider.GetString("LOCLocalAchievementsSettingsUpdateModeDialog"));
            updateMode.Items.Add(ResourceProvider.GetString("LOCLocalAchievementsSettingsUpdateModeNotification"));
            updateMode.Items.Add(ResourceProvider.GetString("LOCLocalAchievementsSettingsUpdateModeOff"));
            updateMode.SetBinding(Selector.SelectedIndexProperty, new Binding("Settings.UpdatePromptMode") { Mode = BindingMode.TwoWay });

            var checkUpdates = new Button
            {
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(14, 5, 14, 5),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            checkUpdates.SetResourceReference(ContentControl.ContentProperty, "LOCLocalAchievementsSettingsCheckUpdates");
            checkUpdates.Click += (sender, e) => checkForUpdates();

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(title);
            panel.Children.Add(subtitle);
            panel.Children.Add(autoStartup);
            panel.Children.Add(autoPlaytime);
            panel.Children.Add(hint);
            panel.Children.Add(autoReapply);
            panel.Children.Add(openLog);
            panel.Children.Add(updateLabel);
            panel.Children.Add(updateMode);
            panel.Children.Add(checkUpdates);

            Content = panel;
        }
    }
}
