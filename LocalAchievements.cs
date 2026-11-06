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
        private SteamPlaytimeService _playtimeService;
        private readonly LocalAchievementsSettingsViewModel _settingsVM;
        private UpdateService _updateService;
        private FileSystemWatcher _successStoryWatcher;
        private System.Threading.Timer _reapplyTimer;
        private readonly HashSet<Guid> _pendingReapply = new HashSet<Guid>();
        private readonly HashSet<Guid> _reapplyRetried = new HashSet<Guid>();
        private readonly object _reapplyLock = new object();
        private class RestoredGameInfo
        {
            public Guid GameId { get; set; }
            public string Name { get; set; }
            public int Count { get; set; }
        }

        private readonly Dictionary<Guid, RestoredGameInfo> _pendingDisplay = new Dictionary<Guid, RestoredGameInfo>();
        private readonly object _pendingDisplayLock = new object();
        private bool _restartPromptOpen;

        private readonly Guid SteamPluginId = Guid.Parse("cb91dfc9-b977-43bf-8e70-55f46e410fab");

        public override Guid Id { get; } = Guid.Parse("a1b2c3d4-e5f6-7890-1234-567890abcdef");

        public LocalAchievements(IPlayniteAPI api) : base(api)
        {
            Properties = new GenericPluginProperties { HasSettings = true };
            AdvancedLogger.Initialize(GetPluginUserDataPath());
            _settingsVM = new LocalAchievementsSettingsViewModel(this);
            _syncService = new SuccessStorySyncService(api);
            _playtimeService = new SteamPlaytimeService(api);
            _updateService = new UpdateService(Path.GetDirectoryName(typeof(LocalAchievements).Assembly.Location), () => _settingsVM.Settings.UpdateFeedUrl);
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            string section = "Local Achievements";
            string icon = Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "icon.png");

            yield return new GameMenuItem
            {
                MenuSection = section,
                Description = ResourceProvider.GetString("LOCLocalAchievementsSyncAchievementsMenu"),
                Icon = icon,
                Action = (context) => SyncAchievementsFromMenu(context)
            };

            yield return new GameMenuItem
            {
                MenuSection = section,
                Description = ResourceProvider.GetString("LOCLocalAchievementsSyncPlaytimeMenu"),
                Icon = icon,
                Action = (context) => SyncPlaytimeFromMenu(context)
            };

            yield return new GameMenuItem
            {
                MenuSection = section,
                Description = ResourceProvider.GetString("LOCLocalAchievementsDiagnosticMenu"),
                Icon = icon,
                Action = (context) => CopyDiagnosticFromMenu(context)
            };
        }

        private void CopyDiagnosticFromMenu(GameMenuItemActionArgs context)
        {
            var game = context.Games.FirstOrDefault();
            if (game == null) return;

            string report = null;
            PlayniteApi.Dialogs.ActivateGlobalProgress((progress) =>
            {
                report = _syncService.Diagnose(game);
            }, new GlobalProgressOptions(ResourceProvider.GetString("LOCLocalAchievementsDiagnosticProgress")));

            AdvancedLogger.Log(report);

            try { System.Windows.Clipboard.SetText(report ?? ""); } catch { }

            PlayniteApi.Dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCLocalAchievementsDiagnosticDone"), report),
                ResourceProvider.GetString("LOCLocalAchievementsSteamToolsTitle"));
        }

        public override void Dispose()
        {
            try
            {
                PlayniteApi.Database.Games.ItemCollectionChanged -= Games_ItemCollectionChanged;

                if (_successStoryWatcher != null)
                {
                    _successStoryWatcher.EnableRaisingEvents = false;
                    _successStoryWatcher.Dispose();
                    _successStoryWatcher = null;
                }

                _reapplyTimer?.Dispose();
                _reapplyTimer = null;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Error while releasing plugin resources.");
            }
        }

        private void StartSuccessStoryWatcher()
        {
            try
            {
                string directory = _syncService.SuccessStoryDirectory;
                if (!Directory.Exists(directory))
                {
                    AdvancedLogger.Log("[REAPPLY] SuccessStory data folder not found; automatic restore is disabled.");
                    return;
                }

                _reapplyTimer = new System.Threading.Timer(_ => ProcessPendingReapply(), null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

                _successStoryWatcher = new FileSystemWatcher(directory, "*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    IncludeSubdirectories = false
                };
                _successStoryWatcher.Changed += (sender, e) => QueueReapply(e.FullPath);
                _successStoryWatcher.Created += (sender, e) => QueueReapply(e.FullPath);
                _successStoryWatcher.Renamed += (sender, e) => QueueReapply(e.FullPath);
                _successStoryWatcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                AdvancedLogger.Log("[FAIL] Could not watch the SuccessStory folder: " + ex.Message);
            }
        }

        private void QueueReapply(string path)
        {
            try
            {
                if (!_settingsVM.Settings.AutoReapplyAchievements) return;
                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(path), out Guid gameId)) return;
                if (SuccessStorySyncService.IsOwnWrite(path)) return;

                double? sinceOwnWrite = SuccessStorySyncService.SecondsSinceOwnWrite(path);
                if (sinceOwnWrite.HasValue && sinceOwnWrite.Value < 60)
                {
                    string gameName = PlayniteApi.Database.Games.Get(gameId)?.Name ?? gameId.ToString();
                    AdvancedLogger.Log($"[REAPPLY] SuccessStory rewrote the data of {gameName} {sinceOwnWrite.Value:0} s after our own write.");
                }

                lock (_reapplyLock) { _pendingReapply.Add(gameId); }
                _reapplyTimer?.Change(10000, System.Threading.Timeout.Infinite);
            }
            catch (Exception ex)
            {
                AdvancedLogger.Log("[FAIL] Could not queue a SuccessStory change: " + ex.Message);
            }
        }

        private bool MarkReapplyRetry(Guid id)
        {
            lock (_reapplyLock) { return _reapplyRetried.Add(id); }
        }

        private void ProcessPendingReapply()
        {
            List<Guid> ids;
            lock (_reapplyLock)
            {
                ids = _pendingReapply.ToList();
                _pendingReapply.Clear();
            }

            if (ids.Count == 0 || !_settingsVM.Settings.AutoReapplyAchievements) return;

            var restoredItems = new List<RestoredGameInfo>();
            bool retryScheduled = false;

            foreach (var id in ids)
            {
                try
                {
                    var game = PlayniteApi.Database.Games.Get(id);
                    if (game == null || !_syncService.IsSteamToolsGame(game)) continue;

                    int count = _syncService.Sync(game, out string note);

                    if (count > 0)
                    {
                        var info = new RestoredGameInfo { GameId = game.Id, Name = game.Name, Count = count };
                        restoredItems.Add(info);
                        MarkPendingDisplay(game, count);
                        AdvancedLogger.Log($"[REAPPLY] {game.Name}: SuccessStory data changed; {count} achievements restored.");
                    }
                    else if (note != null && note.StartsWith("Unexpected error", StringComparison.Ordinal) && MarkReapplyRetry(id))
                    {
                        lock (_reapplyLock) { _pendingReapply.Add(id); }
                        retryScheduled = true;
                    }
                }
                catch (Exception ex)
                {
                    AdvancedLogger.Log($"[FAIL] Restore after a SuccessStory refresh failed for {id}: {ex.Message}");
                }
            }

            if (retryScheduled)
                _reapplyTimer?.Change(10000, System.Threading.Timeout.Infinite);
            else
            {
                lock (_reapplyLock) { _reapplyRetried.Clear(); }
            }

            if (restoredItems.Count > 0)
            {
                string notifText;
                int totalCount = restoredItems.Sum(i => i.Count);

                if (restoredItems.Count == 1)
                {
                    notifText = string.Format(
                        ResourceProvider.GetString("LOCLocalAchievementsReappliedNotificationSingle"),
                        restoredItems[0].Name,
                        restoredItems[0].Count);
                }
                else
                {
                    string names = string.Join(", ", restoredItems.Take(3).Select(i => i.Name));
                    if (restoredItems.Count > 3) names += $" +{restoredItems.Count - 3}";

                    notifText = string.Format(
                        ResourceProvider.GetString("LOCLocalAchievementsReappliedNotificationMultiple"),
                        totalCount,
                        names);
                }

                PlayniteApi.Notifications.Add(new NotificationMessage(
                    Guid.NewGuid().ToString(),
                    notifText,
                    NotificationType.Info,
                    () => PromptRestartForPending()));

                PlayniteApi.MainView.UIDispatcher.Invoke(() => PromptRestartForRestored(restoredItems));
            }
        }

        private void MarkPendingDisplay(Game game, int count = 0)
        {
            if (game == null) return;
            lock (_pendingDisplayLock)
            {
                if (_pendingDisplay.TryGetValue(game.Id, out var existing))
                {
                    existing.Name = game.Name;
                    if (count > 0) existing.Count = count;
                }
                else
                {
                    _pendingDisplay[game.Id] = new RestoredGameInfo
                    {
                        GameId = game.Id,
                        Name = game.Name,
                        Count = count
                    };
                }
            }
        }

        private List<RestoredGameInfo> PendingDisplayInfos(IEnumerable<Guid> onlyIds = null)
        {
            lock (_pendingDisplayLock)
            {
                var ids = onlyIds == null ? null : new HashSet<Guid>(onlyIds);
                return _pendingDisplay.Values
                    .Where(p => ids == null || ids.Contains(p.GameId))
                    .ToList();
            }
        }

        private void PromptRestartForPending()
        {
            var pending = PendingDisplayInfos();
            if (pending.Count > 0)
            {
                PlayniteApi.MainView.UIDispatcher.Invoke(() => PromptRestartForRestored(pending));
            }
        }

        private void PromptRestartForRestored(List<RestoredGameInfo> items)
        {
            if (_restartPromptOpen) return;
            if (items == null || items.Count == 0) return;

            if (PlayniteApi.Database.Games.Any(g => g.IsRunning))
            {
                AdvancedLogger.Log("[REAPPLY] A game is running; on-screen restart dialog postponed.");
                return;
            }

            _restartPromptOpen = true;
            try
            {
                string promptMessage;
                string promptTitle = ResourceProvider.GetString("LOCLocalAchievementsSyncComplete");

                int totalCount = items.Sum(i => i.Count);

                if (items.Count == 1)
                {
                    var single = items[0];
                    promptMessage = string.Format(
                        ResourceProvider.GetString("LOCLocalAchievementsReappliedPromptSingle"),
                        single.Count,
                        single.Name);
                }
                else
                {
                    string list = string.Join("\n", items.Take(5).Select(i =>
                        $"- {i.Name}: {string.Format(ResourceProvider.GetString("LOCLocalAchievementsAchievementCount"), i.Count)}"));
                    if (items.Count > 5) list += "\n+" + (items.Count - 5);

                    promptMessage = string.Format(
                        ResourceProvider.GetString("LOCLocalAchievementsReappliedPromptMultiple"),
                        list,
                        totalCount);
                }

                var answer = PlayniteApi.Dialogs.ShowMessage(
                    promptMessage,
                    promptTitle,
                    System.Windows.MessageBoxButton.YesNo);

                if (answer == System.Windows.MessageBoxResult.Yes)
                {
                    AdvancedLogger.Log("[REAPPLY] Restarting Playnite so SuccessStory loads the restored achievements.");
                    RestartPlaynite();
                }
            }
            finally
            {
                _restartPromptOpen = false;
            }
        }

        private void RestartPlaynite()
        {
            try
            {
                lock (_pendingDisplayLock)
                {
                    foreach (var info in _pendingDisplay.Values)
                    {
                        var g = PlayniteApi.Database.Games.Get(info.GameId);
                        if (g != null && _syncService.IsSteamToolsGame(g))
                        {
                            _syncService.Sync(g);
                            AdvancedLogger.Log($"[REAPPLY] Pre-restart sync confirmed for {g.Name}.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AdvancedLogger.Log($"[FAIL] Pre-restart sync failed: {ex.Message}");
            }

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

        private void HandlePostUpdate()
        {
            try
            {
                _updateService.CleanupLeftovers();

                var installed = _updateService.GetInstalledVersion();
                if (installed == null) return;

                string current = installed.ToString();
                var settings = _settingsVM.Settings;
                if (settings.LastRunVersion == current) return;

                bool upgraded = !string.IsNullOrEmpty(settings.LastRunVersion);
                settings.LastRunVersion = current;
                SavePluginSettings(settings);

                if (upgraded)
                {
                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "LocalAchievements.Updated",
                        string.Format(ResourceProvider.GetString("LOCLocalAchievementsUpdated"), current),
                        NotificationType.Info));
                    AdvancedLogger.Log($"[UPDATE] Running version {current}.");
                }
            }
            catch (Exception ex)
            {
                AdvancedLogger.Log("[UPDATE] Post-update handling failed: " + ex.Message);
            }
        }

        private void ScheduleUpdateCheck()
        {
            if (_settingsVM.Settings.UpdatePromptMode == 2) return;

            Task.Run(async () =>
            {
                await Task.Delay(15000);
                StartUpdateCheck(false);
            });
        }

        private void StartUpdateCheck(bool interactive)
        {
            Task.Run(() =>
            {
                UpdateCheckResult result;
                try
                {
                    result = _updateService.Check();
                }
                catch (Exception ex)
                {
                    result = new UpdateCheckResult { Status = UpdateStatus.Error, Error = ex.Message };
                }

                PlayniteApi.MainView.UIDispatcher.Invoke(() => HandleUpdateResult(result, interactive));
            });
        }

        private void HandleUpdateResult(UpdateCheckResult result, bool interactive)
        {
            string title = ResourceProvider.GetString("LOCLocalAchievementsUpdateTitle");

            if (result.Status == UpdateStatus.Error)
            {
                AdvancedLogger.Log("[UPDATE] Check failed: " + result.Error);
                if (interactive)
                    PlayniteApi.Dialogs.ShowMessage(string.Format(ResourceProvider.GetString("LOCLocalAchievementsUpdateCheckFailed"), result.Error), title);
                return;
            }

            if (result.Status == UpdateStatus.UpToDate)
            {
                if (interactive)
                    PlayniteApi.Dialogs.ShowMessage(string.Format(ResourceProvider.GetString("LOCLocalAchievementsUpdateUpToDate"), result.Installed), title);
                return;
            }

            var info = result.Info;
            AdvancedLogger.Log($"[UPDATE] Version {info.Version} is available (installed: {result.Installed}).");

            if (!_updateService.IsInstalledCopy(PlayniteApi.Paths.ConfigurationPath))
            {
                AdvancedLogger.Log("[UPDATE] Running from a developer folder; automatic update disabled.");
                if (interactive)
                    PlayniteApi.Dialogs.ShowMessage(string.Format(ResourceProvider.GetString("LOCLocalAchievementsUpdateDevMode"), info.Version), title);
                return;
            }

            if (!interactive && _settingsVM.Settings.UpdatePromptMode == 1)
            {
                AddUpdateNotification(info, result.Installed);
                return;
            }

            PromptUpdate(info, result.Installed, true);
        }

        private void AddUpdateNotification(UpdateInfo info, Version installed)
        {
            PlayniteApi.Notifications.Add(new NotificationMessage(
                "LocalAchievements.Update." + info.Tag,
                string.Format(ResourceProvider.GetString("LOCLocalAchievementsUpdateNotification"), installed, info.Version),
                NotificationType.Info,
                () => PromptUpdate(info, installed, false)));
        }

        private void PromptUpdate(UpdateInfo info, Version installed, bool offerNotificationOnDecline)
        {
            string notes = string.IsNullOrWhiteSpace(info.Notes) ? ResourceProvider.GetString("LOCLocalAchievementsUpdateNoNotes") : info.Notes;

            var answer = PlayniteApi.Dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCLocalAchievementsUpdatePrompt"), installed, info.Version, notes),
                ResourceProvider.GetString("LOCLocalAchievementsUpdateTitle"),
                System.Windows.MessageBoxButton.YesNo);

            if (answer == System.Windows.MessageBoxResult.Yes)
            {
                InstallUpdate(info);
            }
            else if (offerNotificationOnDecline)
            {
                AddUpdateNotification(info, installed);
            }
        }

        private void InstallUpdate(UpdateInfo info)
        {
            string title = ResourceProvider.GetString("LOCLocalAchievementsUpdateTitle");

            if (PlayniteApi.Database.Games.Any(g => g.IsRunning))
            {
                PlayniteApi.Dialogs.ShowMessage(ResourceProvider.GetString("LOCLocalAchievementsUpdateGameRunning"), title);
                return;
            }

            string error = null;
            bool cancelled = false;

            PlayniteApi.Dialogs.ActivateGlobalProgress((progress) =>
            {
                try
                {
                    string package = _updateService.DownloadAndVerify(info, progress.CancelToken);
                    _updateService.Install(package, info);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    error = ex is UpdateException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                }
            }, new GlobalProgressOptions(ResourceProvider.GetString("LOCLocalAchievementsUpdateDownloading"), true));

            if (cancelled)
            {
                AdvancedLogger.Log("[UPDATE] Cancelled by the user.");
                return;
            }

            if (error != null)
            {
                AdvancedLogger.Log("[FAIL] Update to " + info.Version + " failed: " + error);

                var open = PlayniteApi.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCLocalAchievementsUpdateFailed"), error),
                    title,
                    System.Windows.MessageBoxButton.YesNo);

                if (open == System.Windows.MessageBoxResult.Yes)
                {
                    string url = string.IsNullOrEmpty(info.ReleaseUrl) ? "https://github.com/uWaazy/Local-Achievements/releases" : info.ReleaseUrl;
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch (Exception ex) { AdvancedLogger.Log("[UPDATE] Could not open the release page: " + ex.Message); }
                }
                return;
            }

            AdvancedLogger.Log("[UPDATE] Installed version " + info.Version + " (restart required).");

            var restart = PlayniteApi.Dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCLocalAchievementsUpdateDone"), info.Version),
                title,
                System.Windows.MessageBoxButton.YesNo);

            if (restart == System.Windows.MessageBoxResult.Yes)
            {
                RestartPlaynite();
            }
        }

        private void SyncPlaytimeFromMenu(GameMenuItemActionArgs context)
        {
            int synced = 0, skipped = 0, none = 0;

            PlayniteApi.Dialogs.ActivateGlobalProgress((progress) =>
            {
                progress.ProgressMaxValue = context.Games.Count;
                foreach (var game in context.Games)
                {
                    progress.Text = string.Format(ResourceProvider.GetString("LOCLocalAchievementsReadingData"), game.Name);

                    if (_syncService.IsSteamToolsGame(game))
                    {
                        switch (_playtimeService.Sync(game))
                        {
                            case PlaytimeSyncResult.Synced: synced++; break;
                            case PlaytimeSyncResult.SkippedNotHigher: skipped++; break;
                            default: none++; break;
                        }
                    }
                    else none++;
                    progress.CurrentProgressValue++;
                }
            }, new GlobalProgressOptions(ResourceProvider.GetString("LOCLocalAchievementsPlaytimeProgress")));

            PlayniteApi.Dialogs.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCLocalAchievementsPlaytimeResult"), synced, skipped, none),
                ResourceProvider.GetString("LOCLocalAchievementsPlaytimeTitle"));
        }

        private void SyncAchievementsFromMenu(GameMenuItemActionArgs context)
        {
            var gameToRefresh = context.Games.FirstOrDefault();
            int totalUpdated = 0;
            var failures = new List<string>();

            PlayniteApi.Dialogs.ActivateGlobalProgress((progress) =>
            {
                progress.ProgressMaxValue = context.Games.Count;

                foreach (var game in context.Games)
                {
                    progress.Text = string.Format(ResourceProvider.GetString("LOCLocalAchievementsReadingData"), game.Name);

                    VerificarEAdicionarTagSteamTools(game);

                    if (_syncService.IsSteamToolsGame(game))
                    {
                        int count = _syncService.Sync(game, out string reason);
                        if (count > 0)
                        {
                            game.Modified = DateTime.Now;
                            PlayniteApi.Database.Games.Update(game);
                            totalUpdated += count;
                            MarkPendingDisplay(game, count);
                        }
                        else if (!string.IsNullOrEmpty(reason))
                        {
                            failures.Add($"{game.Name}: {reason}");
                        }
                    }
                    else
                    {
                        string notSupported = $"Not a Steam Tools game (no stplug-in\\{game.GameId}.lua found, or not from the Steam library).";
                        AdvancedLogger.Log($"[SKIP] {game.Name}: {notSupported}");
                        failures.Add($"{game.Name}: {notSupported}");
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
                    RestartPlaynite();
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
            else if (PendingDisplayInfos(context.Games.Select(g => g.Id)).Count > 0)
            {
                PromptRestartForRestored(PendingDisplayInfos(context.Games.Select(g => g.Id)));
            }
            else if (failures.Count > 0)
            {
                string details = string.Join("\n\n", failures.Take(5).Select(f => f.Length > 400 ? f.Substring(0, 400) + "..." : f));
                PlayniteApi.Dialogs.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCLocalAchievementsSyncNothing"), details),
                    ResourceProvider.GetString("LOCLocalAchievementsSteamToolsTitle")
                );
            }
            else
            {
                PlayniteApi.Dialogs.ShowMessage(
                    ResourceProvider.GetString("LOCLocalAchievementsUpToDate"),
                    ResourceProvider.GetString("LOCLocalAchievementsSteamToolsTitle")
                );
            }
        }

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            try
            {
                var steamGames = PlayniteApi.Database.Games.Where(g => g.PluginId == SteamPluginId);

                foreach (var game in steamGames)
                {
                    VerificarEAdicionarTagSteamTools(game);

                    if (_settingsVM.Settings.AutoSyncPlaytime && _syncService.IsSteamToolsGame(game))
                        _playtimeService.Sync(game);
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
                    await Task.Delay(3000);

                    AdvancedLogger.Log($"[DISFARCE REMOVIDO] Injetando dados reais em {args.Game.Name}...");

                    args.Game.PluginId = SteamPluginId;
                    PlayniteApi.Database.Games.Update(args.Game);

                    int count = _syncService.Sync(args.Game);

                    if (count > 0)
                    {
                        MarkPendingDisplay(args.Game, count);

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
            PlayniteApi.Database.Games.ItemCollectionChanged += Games_ItemCollectionChanged;

            AdvancedLogger.Log($"[STARTUP] Local Achievements {PluginInfo.Version}");
            StartSuccessStoryWatcher();
            HandlePostUpdate();
            ScheduleUpdateCheck();

            if (!_settingsVM.Settings.EnableAutoSyncOnStartup) return;

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(8000);

                    var steamGames = PlayniteApi.Database.Games
                        .Where(g => g.PluginId == SteamPluginId)
                        .ToList();

                    int checkedGames = 0, updatedGames = 0, gamesWithNotes = 0;

                    foreach (var game in steamGames)
                    {
                        VerificarEAdicionarTagSteamTools(game);

                        if (_syncService.IsSteamToolsGame(game))
                        {
                            checkedGames++;
                            int count = _syncService.Sync(game, out string note);
                            if (note != null) gamesWithNotes++;
                            if (count > 0)
                            {
                                updatedGames++;
                                MarkPendingDisplay(game, count);
                            }

                            if (_settingsVM.Settings.AutoSyncPlaytime)
                                _playtimeService.Sync(game);
                        }
                    }

                    AdvancedLogger.Log($"[STARTUP] {PluginInfo.Version}: checked {checkedGames} Steam Tools games, {updatedGames} updated, {gamesWithNotes} with notes (only changes are logged per game).");

                    var selected = PlayniteApi.MainView.SelectedGames?.FirstOrDefault();
                    if (selected != null) RefreshPlayniteUI(selected);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Erro na sincronização automática.");
                }
            });
        }

        private void Games_ItemCollectionChanged(object sender, ItemCollectionChangedEventArgs<Game> e)
        {
            if (e.AddedItems == null || e.AddedItems.Count == 0) return;

            var added = e.AddedItems.Where(g => g.PluginId == SteamPluginId).ToList();
            if (added.Count == 0) return;

            Task.Run(async () =>
            {
                await Task.Delay(3000);

                foreach (var game in added)
                {
                    try
                    {
                        VerificarEAdicionarTagSteamTools(game);

                        if (_settingsVM.Settings.AutoSyncPlaytime
                            && _syncService.IsSteamToolsGame(game)
                            && _playtimeService.Sync(game) == PlaytimeSyncResult.Synced)
                        {
                            PlayniteApi.Notifications.Add(new NotificationMessage(
                                Guid.NewGuid().ToString(),
                                string.Format(ResourceProvider.GetString("LOCLocalAchievementsPlaytimeNotification"), game.Name, Math.Round(game.Playtime / 3600.0, 1)),
                                NotificationType.Info));
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Error(ex, $"Erro ao processar jogo novo: {game?.Name}");
                    }
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

        public override ISettings GetSettings(bool firstRunSettings) { return _settingsVM; }
        public override System.Windows.Controls.UserControl GetSettingsView(bool firstRunSettings) { return new LocalAchievementsSettingsView(() => StartUpdateCheck(true)) { DataContext = _settingsVM }; }
    }
}