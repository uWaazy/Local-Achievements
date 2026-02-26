using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using LocalAchievements.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LocalAchievements
{
    public class LocalAchievements : GenericPlugin
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private SuccessStorySyncService _syncService;

        private readonly Guid SteamPluginId = Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab");

        public override Guid Id { get; } = Guid.Parse("a1b2c3d4-e5f6-7890-1234-567890abcdef");

        public LocalAchievements(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = false };
            _syncService = new SuccessStorySyncService(api);
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCLocalAchievementsSyncMenu"),
                Icon = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "icon.png"),
                Action = (context) =>
                {
                    var gameToRefresh = context.Games.FirstOrDefault();
                    int totalUpdated = 0; 

                    PlayniteApi.Dialogs.ActivateGlobalProgress((progress) =>
                    {
                        progress.ProgressMaxValue = context.Games.Count;

                        foreach (var game in context.Games)
                        {
                            progress.Text = string.Format(ResourceProvider.GetString("LOCLocalAchievementsReadingData"), game.Name);
                            
                            VerificarEAdicionarTagSteamTools(game);

                            if (_syncService.IsSteamToolsGame(game))
                            {
                                int count = _syncService.Sync(game);
                                if (count > 0)
                                {
                                    game.Modified = DateTime.Now;
                                    PlayniteApi.Database.Games.Update(game);
                                    totalUpdated += count;
                                }
                            }
                            progress.CurrentProgressValue++;
                        }
                    }, new GlobalProgressOptions(ResourceProvider.GetString("LOCLocalAchievementsSyncProgress")));
                    
                    if (totalUpdated > 0)
                    {
                        string promptMessage = string.Format(ResourceProvider.GetString("LOCLocalAchievementsRestartPrompt"), totalUpdated);
                        string promptTitle = ResourceProvider.GetString("LOCLocalAchievementsSyncComplete");

                        var result = PlayniteApi.Dialogs.ShowMessage(
                            promptMessage,
                            promptTitle,
                            System.Windows.MessageBoxButton.YesNo
                        );

                        if (result == System.Windows.MessageBoxResult.Yes)
                        {
                            string playniteExe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                            
                            System.Diagnostics.ProcessStartInfo restartInfo = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "cmd.exe",
                                Arguments = $"/c timeout /t 3 /nobreak >nul & start \"\" \"{playniteExe}\"",
                                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                                CreateNoWindow = true
                            };
                            System.Diagnostics.Process.Start(restartInfo);
                            
                            PlayniteApi.MainView.UIDispatcher.Invoke(() => 
                            {
                                System.Windows.Application.Current.Shutdown();
                            });
                        }
                        else
                        {
                            Task.Run(async () =>
                            {
                                await Task.Delay(2000); 
                                RefreshPlayniteUI(gameToRefresh);
                            });
                        }
                    }
                    else
                    {
                        PlayniteApi.Dialogs.ShowMessage(
                            ResourceProvider.GetString("LOCLocalAchievementsUpToDate"), 
                            ResourceProvider.GetString("LOCLocalAchievementsSteamToolsTitle")
                        );
                    }
                }
            };
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            try
            {
                var steamGames = PlayniteApi.Database.Games.Where(g => g.PluginId == SteamPluginId);

                foreach (var game in steamGames)
                {
                    VerificarEAdicionarTagSteamTools(game);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Erro durante a varredura OnLibraryUpdated.");
            }
        }

        private void VerificarEAdicionarTagSteamTools(Game game)
        {
            if (_syncService.IsSteamToolsGame(game))
            {
                // A Tag em si não é traduzida no banco de dados para não quebrar filtros do usuário
                Tag steamToolsTag = PlayniteApi.Database.Tags.FirstOrDefault(t => t.Name == "Steam Tools");
                if (steamToolsTag == null)
                {
                    steamToolsTag = new Tag { Name = "Steam Tools" };
                    PlayniteApi.Database.Tags.Add(steamToolsTag);
                }

                if (game.TagIds == null)
                {
                    game.TagIds = new List<Guid>();
                }

                if (!game.TagIds.Contains(steamToolsTag.Id))
                {
                    game.TagIds.Add(steamToolsTag.Id);
                    PlayniteApi.Database.Games.Update(game);
                    AdvancedLogger.Log($"[TAG] Tag 'Steam Tools' adicionada automaticamente ao jogo {game.Name}.");
                }
            }
        }

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            if (args.Game.PluginId == SteamPluginId && _syncService.IsSteamToolsGame(args.Game))
            {
                AdvancedLogger.Log($"[DISFARCE ATIVADO] Escondendo {args.Game.Name} do SuccessStory...");
                args.Game.PluginId = Guid.Empty; 
                PlayniteApi.Database.Games.Update(args.Game);
            }
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            if (args.Game.PluginId == Guid.Empty && _syncService.IsSteamToolsGame(args.Game))
            {
                Task.Run(async () =>
                {
                    await Task.Delay(2000); 

                    AdvancedLogger.Log($"[DISFARCE REMOVIDO] Injetando dados reais em {args.Game.Name}...");
                    
                    args.Game.PluginId = SteamPluginId;
                    PlayniteApi.Database.Games.Update(args.Game);

                    int count = _syncService.Sync(args.Game);
                    
                    if (count > 0)
                    {
                        args.Game.Modified = DateTime.Now;
                        PlayniteApi.Database.Games.Update(args.Game);

                        string notifText = string.Format(ResourceProvider.GetString("LOCLocalAchievementsTrackerNotification"), count, args.Game.Name);

                        PlayniteApi.Notifications.Add(new NotificationMessage(
                            Guid.NewGuid().ToString(),
                            notifText,
                            NotificationType.Info
                        ));

                        RefreshPlayniteUI(args.Game);
                    }
                });
            }
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(8000); 

                    var steamGames = PlayniteApi.Database.Games
                        .Where(g => g.PluginId == SteamPluginId)
                        .ToList();

                    foreach (var game in steamGames)
                    {
                        VerificarEAdicionarTagSteamTools(game);

                        if (_syncService.IsSteamToolsGame(game))
                        {
                            int count = _syncService.Sync(game);
                            if (count > 0)
                            {
                                game.Modified = DateTime.Now;
                                PlayniteApi.Database.Games.Update(game);
                            }
                        }
                    }
                    
                    var selected = PlayniteApi.MainView.SelectedGames?.FirstOrDefault();
                    if (selected != null) RefreshPlayniteUI(selected);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Erro na sincronização automática.");
                }
            });
        }

        private void RefreshPlayniteUI(Game gameToRefresh)
        {
            if (gameToRefresh == null) return;

            Task.Run(async () =>
            {
                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    var currentlySelected = PlayniteApi.MainView.SelectedGames?.ToList();
                    if (currentlySelected != null && currentlySelected.Any(g => g.Id == gameToRefresh.Id))
                    {
                        PlayniteApi.MainView.SelectGames(new List<Guid>());
                    }
                });

                await Task.Delay(500);

                PlayniteApi.MainView.UIDispatcher.Invoke(() =>
                {
                    PlayniteApi.MainView.SelectGames(new List<Guid> { gameToRefresh.Id });
                });
            });
        }

        public override ISettings GetSettings(bool firstRunSettings) { return null; }
        public override System.Windows.Controls.UserControl GetSettingsView(bool firstRunSettings) { return null; }
    }
}