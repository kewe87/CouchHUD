using System.Text.Json;
using System.Text.Json.Serialization;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Sdk;
using Microsoft.Web.WebView2.Core;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.UiHost;

/// <summary>
/// JSON message protocol between the web UI (ui/bridge.js) and the host.
/// Requests:  { id, method, args: [...] }  →  { id, result } | { id, error }
/// Events:    { event, payload }
/// </summary>
public sealed class UiBridge(Hud hud, CoreWebView2 web)
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>What works while the couch is locked: the list lives in Core (LockPolicy) so it is tested there.</summary>
    private static IReadOnlySet<string> OpenWhileLocked => CouchHud.Core.Guard.LockPolicy.OpenWhileLocked;

    public async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Navigation is locked to our UI, but never trust a message from anywhere else.
        if (!e.Source.StartsWith(MainWindow.AppOrigin, StringComparison.OrdinalIgnoreCase)) return;

        int id;
        string method;
        JsonElement args;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt32();
            method = root.GetProperty("method").GetString() ?? "";
            args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[bridge] bad message: {ex.Message}");
            return;
        }

        try
        {
            var result = await DispatchAsync(method, args);
            Post(new { id, result });
        }
        catch (LockedException ex)
        {
            // Expected while the couch is locked: one line, no stack trace; the page knows it by the flag.
            Console.WriteLine($"[bridge] {method}: locked");
            Post(new { id, error = ex.Message, locked = true });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bridge] {method} failed: {ex}");
            Post(new { id, error = ex.Message });
        }
    }

    public void Emit(string name, object? payload) => Post(new { @event = name, payload });

    private void Post(object message) => web.PostWebMessageAsJson(JsonSerializer.Serialize(message, Json));

    private async Task<object?> DispatchAsync(string method, JsonElement args)
    {
        JsonElement Arg(int i) =>
            args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > i ? args[i] : default;

        string[]? Buttons(int i) => Arg(i).ValueKind == JsonValueKind.Array
            ? Arg(i).EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "").ToArray()
            : null;

        string StringArg(int i) => Arg(i).ValueKind == JsonValueKind.String
            ? Arg(i).GetString()!
            : throw new ArgumentException($"{method}: argument {i} must be a string");

        // Couch lock: a guest on the couch gets exactly this list. Everything else (adding
        // apps, plugins, settings, file browsing, uninstallers …) could start a program of
        // their choice, so it needs the code first. Leaving the couch checks in Hud itself.
        if (hud.Locked && !OpenWhileLocked.Contains(method) && !CouchHud.Core.Integrations.CaseHud.CaseHudPolicy.OpenWhileLocked.Contains(method)
            && !(method == "setSetting" && Arg(0).ValueKind == JsonValueKind.String && CouchHud.Core.Guard.LockPolicy.HarmlessSettings.Contains(Arg(0).GetString()!)))
        {
            throw new LockedException(Strings.Get("lock.unlockFirst"));
        }

        // CaseHUD page: one prefix, one handler (the lock rules are CaseHudPolicy, checked above).
        if (method.StartsWith("caseHud", StringComparison.Ordinal)) return await hud.CaseHud.HandleAsync(method, args);

        switch (method)
        {
            case "getLibrary":
                var rescan = Arg(0) is { ValueKind: JsonValueKind.Object } opts
                             && opts.TryGetProperty("rescan", out var r) && r.ValueKind == JsonValueKind.True;
                return ToDto(await hud.GetLibraryAsync(rescan));
            case "launch":
                return ToDto(hud.Launch(StringArg(0), byPerson: true));
            case "toggleFavorite":
                return hud.ToggleFavorite(StringArg(0));
            case "getStoreInfo":
                return await hud.GetStoreInfoAsync(StringArg(0));
            case "getDetails":
                return await hud.GetDetailsAsync(StringArg(0));
            case "uninstall":
                await hud.UninstallAsync(StringArg(0));
                return null;
            case "getSaveInfo":
                return await hud.Saves.GetInfoAsync(StringArg(0));
            case "backupSaves":
                return await hud.Saves.BackupAsync(StringArg(0));
            case "restoreSaves":
                return await hud.Saves.RestoreAsync(StringArg(0), StringArg(1));
            case "deleteSaveBackup":
                return await hud.Saves.DeleteBackupAsync(StringArg(0), StringArg(1));
            case "getSaveSettings":
                return hud.Saves.GetSettings();
            case "setSaveSetting":
            {
                switch (StringArg(0))
                {
                    case "backupDir": hud.Saves.SetBackupDir(Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString() : null); break;
                    case "keep": hud.Saves.SetKeep(Arg(1).GetInt32()); break;
                    case "includeConfig": hud.Saves.SetIncludeConfig(Arg(1).ValueKind == JsonValueKind.True); break;
                    default: throw new ArgumentException(Strings.Get("bridge.unknownSetting"));
                }
                return hud.Saves.GetSettings();
            }
            case "systemInfo":
                return hud.SystemInfo();
            case "setShell":
                return hud.SetShell(Arg(0).ValueKind == JsonValueKind.True);
            case "enterPcMode":
                await hud.EnterPcModeAsync();
                return null;
            case "power":
                hud.Power(StringArg(0));
                return null;
            case "quit":
                hud.QuitFromUi();
                return null;
            case "getSettings":
                return hud.GetSettings();
            case "setSetting":
                await hud.SetSettingAsync(StringArg(0), Arg(1));
                return null;
            case "browse":
            {
                var path = Arg(0).ValueKind == JsonValueKind.String ? Arg(0).GetString() : null;
                var extensions = Arg(1).ValueKind == JsonValueKind.Array
                    ? Arg(1).EnumerateArray().Select(e => e.GetString() ?? "").Where(e => e.Length > 0).ToList()
                    : null;
                return hud.Browse(path, extensions);
            }
            case "getGameFolders":
                return hud.GetGameFolders();
            case "addGameFolder":
                return await hud.AddGameFolderAsync(StringArg(0));
            case "removeGameFolder":
                return await hud.RemoveGameFolderAsync(StringArg(0));
            case "discoverGameFolders":
                return await hud.DiscoverGameFoldersAsync();
            case "scanFolder":
                return await hud.ScanFolderAsync(StringArg(0));
            case "addEntries":
            {
                var entries = Arg(0).Deserialize<List<CustomEntry>>(Json) ?? new List<CustomEntry>();
                // The add flow knows no parameters: those are set in the edit dialog (editLaunch) or confirmed on the TV (add_entry).
                foreach (var entry in entries) (entry.Args, entry.Cwd) = ([], null);
                return ToDto(await hud.AddEntriesAsync(entries));
            }
            case "getInstalledApps":
                return await hud.GetInstalledAppsAsync();
            case "getAppIcons":
                return await hud.GetAppIconsAsync(Arg(0).ValueKind == JsonValueKind.Array
                    ? Arg(0).EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                    : []);
            case "addInstalledApp":
                return ToDto(await hud.AddInstalledAppAsync(StringArg(0), Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString()! : ""));
            case "editLaunch":
                return ToDto(hud.EditLaunch(StringArg(0), Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString() : null, Arg(2).ValueKind == JsonValueKind.String ? Arg(2).GetString() : null));
            case "editItem":
            {
                var changes = Arg(1);
                string? Prop(string name) =>
                    changes.ValueKind == JsonValueKind.Object && changes.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                bool? hidden = changes.ValueKind == JsonValueKind.Object && changes.TryGetProperty("hidden", out var h) && h.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? h.GetBoolean()
                    : null;
                return ToDto(hud.EditItem(StringArg(0), Prop("name"), Prop("type"), hidden));
            }
            case "getArtworkChoices":
                return ToDto(await hud.GetArtworkChoicesAsync(StringArg(0)));
            case "setArtwork":
                return ToDto(hud.SetArtwork(StringArg(0), StringArg(1), Arg(2).ValueKind == JsonValueKind.String ? Arg(2).GetString() : null));
            case "getCollections":
                return hud.GetCollections();
            case "createCollection":
            {
                ItemCollection? created = null;
                var all = hud.UpdateCollections(list => created = Collections.Create(list, StringArg(0)));
                return new { id = created!.Id, collections = all };
            }
            case "renameCollection":
                return hud.UpdateCollections(all => Collections.Rename(all, StringArg(0), StringArg(1)));
            case "deleteCollection":
                return hud.UpdateCollections(all => Collections.Delete(all, StringArg(0)));
            case "setCollectionMember":
                return hud.UpdateCollections(all => Collections.SetMember(all, StringArg(0), StringArg(1), Arg(2).ValueKind == JsonValueKind.True));
            case "setCollectionOnHome":
                return hud.UpdateCollections(all => Collections.SetOnHome(all, StringArg(0), Arg(1).ValueKind == JsonValueKind.True));
            case "removeEntry":
                hud.RemoveEntry(StringArg(0));
                return null;
            case "getSession":
                return hud.SessionDto();
            case "resumeGame":
                hud.ResumeGame();
                return null;
            case "quitGame":
                await hud.QuitGameAsync(Arg(0).ValueKind == JsonValueKind.True);
                return null;
            case "getAudio":
                return hud.GetAudio();
            case "setVolume":
                return hud.SetVolume(Arg(0).GetInt32());
            case "setMute":
                return hud.SetMute(Arg(0).ValueKind == JsonValueKind.True);
            case "setAudioOutput":
                return hud.SetAudioOutput(StringArg(0));
            case "getAppVolumes":
                return hud.GetAppVolumes();
            case "setAppVolume":
                hud.SetAppVolume(StringArg(0), Arg(1).GetInt32());
                return null;
            case "setAppMute":
                hud.SetAppMute(StringArg(0), Arg(1).ValueKind == JsonValueKind.True);
                return null;
            case "getPads":
                return hud.Pads();
            case "rumble":
                hud.Rumble(StringArg(0));
                return null;
            case "getNews":
                return await hud.GetNewsAsync();
            case "getFeeds":
                return hud.GetFeeds();
            case "addFeed":
                hud.AddFeed(Arg(0).ValueKind == JsonValueKind.String ? Arg(0).GetString()! : "", StringArg(1));
                return hud.GetFeeds();
            case "removeFeed":
                hud.RemoveFeed(StringArg(0));
                return hud.GetFeeds();
            case "openUrl":
                await hud.OpenUrlFromHudAsync(StringArg(0), Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString()! : "ui");
                return null;
            case "setEntryEdge":
                return ToDto(await hud.SetEntryEdgeAsync(StringArg(0), Arg(1).ValueKind == JsonValueKind.True));
            case "assistantInfo":
                return hud.AssistantInfo();
            case "askAssistant":
                return await hud.AskAssistantAsync(StringArg(0));
            case "confirmAssistant":
                hud.ConfirmAssistant(StringArg(0), Arg(1).ValueKind == JsonValueKind.True);
                return null;
            case "resetAssistant":
                hud.ResetAssistant();
                return null;
            case "setIntegrationMode":
                return hud.SetIntegrationMode(StringArg(0));
            case "getBrowser":
                return hud.BrowserState();
            case "openBrowser":
                await hud.OpenBrowserFromHudAsync(Arg(0).ValueKind == JsonValueKind.String ? Arg(0).GetString() : null);
                return null;
            case "browserLayout":
                hud.BrowserLayout(Arg(0).GetDouble(), Arg(1).GetDouble(), Arg(2).GetDouble(), Arg(3).GetDouble(), Arg(4).ValueKind == JsonValueKind.True);
                return null;
            case "browserFocus":
                hud.SetBrowserFocus(Arg(0).ValueKind == JsonValueKind.True);
                return null;
            case "browserCommand":
                hud.BrowserCommand(StringArg(0));
                return null;
            case "setBrowserZoom":
                return hud.SetBrowserZoom(Arg(0).GetInt32());
            case "addBookmark":
                return hud.AddBookmark(StringArg(0), StringArg(1));
            case "removeBookmark":
                return hud.RemoveBookmark(StringArg(0));
            case "displayOn":
                hud.DisplayOn();
                return null;
            case "displayOff":
                hud.DisplayOff();
                return null;
            case "getSecurity":
                return hud.GetSecurity();
            case "getServiceStatus":
                return await hud.GetServiceStatusAsync(Arg(0).ValueKind == JsonValueKind.True);
            case "repairService":
                // Open while locked: Windows' UAC prompt is the gate, and the repair is the way out of a fail-closed lock.
                return await hud.RepairServiceAsync();
            case "getParental":
                return hud.GetParental();
            case "setParental":
                return await hud.SetParentalAsync(StringArg(0), Arg(1));
            case "setParentalItemLock":
                return hud.SetParentalItemLock(StringArg(0), Arg(1).ValueKind == JsonValueKind.True);
            case "parentalCheck":
                return await hud.ParentalCheckAsync(StringArg(0));
            case "parentalExtend":
                return hud.ParentalExtend(Arg(0).GetInt32());
            case "parentalAuth":
                // The code is given for one action on one entry (null: any): see ParentGrant.
                return await hud.ParentalAuthAsync(Buttons(0),
                    Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString() : null,
                    Arg(2).ValueKind == JsonValueKind.String ? Arg(2).GetString() : null);
            case "confirmWindowsUnlock":
                return await hud.ConfirmWindowsUnlockAsync(Arg(0).ValueKind == JsonValueKind.True);
            case "parentalReset":
                return hud.ParentalReset();
            case "parentalAcceptClock":
                return hud.ParentalAcceptClock();
            case "parentalAdoptZone":
                return hud.ParentalAdoptZone();
            case "parentalCloseGame":
                hud.ParentalCloseGame();
                return null;
            case "unlock":
                return await hud.UnlockAsync(Buttons(0), Arg(1).ValueKind == JsonValueKind.True);
            case "windowsSignIn":
                return hud.WindowsSignIn();
            case "setPasscode":
                return await hud.SetPasscodeAsync(Buttons(0) ?? throw new ArgumentException(Strings.Get("bridge.sequenceMissing")));
            case "setCouchLock":
                return await hud.SetCouchLockAsync(StringArg(0), Arg(1).ValueKind == JsonValueKind.Array ? Buttons(1) : null);
            case "setParentCode":
                return await hud.SetParentCodeAsync(Buttons(0) ?? throw new ArgumentException(Strings.Get("bridge.sequenceMissing")));
            case "readClipboard":
                return hud.ReadClipboard();
            case "holdUnlockForSetup":
                hud.HoldUnlockForSetup(Arg(0).ValueKind == JsonValueKind.True);
                return null;
            case "setBlockTaskManager":
                return hud.SetBlockTaskManager(Arg(0).ValueKind == JsonValueKind.True);
            case "openAutoLogonDialog":
                hud.OpenAutoLogonDialog();
                return null;
            case "enableAutoLogon":
                return await hud.EnableAutoLogonAsync(StringArg(0), Arg(1).ValueKind == JsonValueKind.True);
            case "setAutoLogon":
                return await hud.SetAutoLogonAsync(StringArg(0));
            case "getLaunchers":
                return hud.GetLaunchers();
            case "setLauncherMode":
                return hud.SetLauncherMode(StringArg(0), StringArg(1));
            case "openLauncher":
                hud.OpenLauncher(StringArg(0));
                return null;
            case "getProfile":
                return hud.GetProfile(StringArg(0));
            case "setProfile":
                return hud.SetProfile(StringArg(0), Arg(1).Deserialize<GameProfile>(Json) ?? new GameProfile());
            case "capture":
                await hud.CaptureAsync(StringArg(0));
                return null;
            case "getDownloads":
                return await hud.GetDownloadsAsync();
            case "getDisplay":
                return hud.GetDisplay();
            case "setDisplayMode":
                return hud.SetDisplayMode(Arg(0).GetInt32(), Arg(1).GetInt32(), Arg(2).GetInt32());
            case "setHdr":
                return hud.SetHdr(Arg(0).ValueKind == JsonValueKind.True);
            case "getPerformance":
                return await hud.GetPerformanceAsync();
            case "getCaptures":
                return await hud.GetCapturesAsync();
            case "getPlugins":
                return hud.GetPlugins();
            case "setPluginEnabled":
                hud.SetPluginEnabled(StringArg(0), Arg(1).ValueKind == JsonValueKind.True);
                return hud.GetPlugins();
            case "setPluginSetting":
                hud.SetPluginSetting(StringArg(0), StringArg(1), Arg(2).ValueKind == JsonValueKind.String ? Arg(2).GetString() : null);
                return hud.GetPlugins();
            case "getPluginPage":
                return await hud.GetPluginPageAsync(StringArg(0), StringArg(1));
            case "activatePluginRow":
                return await hud.ActivatePluginRowAsync(StringArg(0), StringArg(1), StringArg(2));
            case "getQuickActions":
                return await hud.GetQuickActionsAsync();
            case "approvePlugin":
                return hud.ApprovePlugin(StringArg(0));
            case "runQuickAction":
                return await hud.RunQuickActionAsync(StringArg(0), StringArg(1));
            case "installPackage":
                return hud.InstallPackage(StringArg(0), StringArg(1));
            case "getCatalog":
                return await hud.GetCatalogAsync(Arg(0).ValueKind == JsonValueKind.True);
            case "getCatalogUpdates":
                return hud.GetCatalogUpdates();
            case "installCatalogEntry":
                return await hud.InstallCatalogEntryAsync(StringArg(0), StringArg(1));
            case "getNotifications":
                return hud.GetNotifications();
            case "markNotificationsRead":
                hud.MarkNotificationsRead();
                return null;
            case "clearNotifications":
                hud.ClearNotifications();
                return null;
            case "getBluetooth":
                return await hud.GetBluetoothStateAsync();
            case "scanBluetooth":
                return await hud.ScanBluetoothAsync();
            case "pairBluetooth":
                return await hud.PairBluetoothAsync(StringArg(0));
            case "unpairBluetooth":
                return await hud.UnpairBluetoothAsync(StringArg(0));
            case "getWifi":
                return await hud.GetWifiAsync();
            case "scanWifi":
                return await hud.ScanWifiAsync();
            case "connectWifi":
                return await hud.ConnectWifiAsync(StringArg(0), Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString() : null);
            case "disconnectWifi":
                return await hud.DisconnectWifiAsync();
            case "answerBluetoothRecovery":
                return hud.AnswerBluetoothRecovery(Arg(0).ValueKind == JsonValueKind.True);
            case "setBluetooth":
                await hud.SetBluetoothAsync(Arg(0).ValueKind == JsonValueKind.True,
                    Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString() : null);
                return await hud.GetBluetoothStateAsync();
            case "getRemote":
                return hud.GetRemote(Arg(0).ValueKind == JsonValueKind.True);
            case "keyboardState":
            {
                var k = Arg(0);
                string P(string n) => k.ValueKind == JsonValueKind.Object && k.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                bool B(string n) => k.ValueKind == JsonValueKind.Object && k.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
                hud.SetKeyboardState(B("open"), P("title"), B("secret"), P("text"));
                return null;
            }
            case "revokeRemote":
                hud.RevokeRemote();
                return hud.GetRemote();
            case "revokeRemoteDevice":
                hud.RevokeRemoteDevice(StringArg(0));
                return hud.GetRemote();
            case "allowFirewall":
                return await hud.AllowFirewallAsync();
            case "getStartup":
                return hud.GetStartup();
            case "setStartupMode":
                hud.SetStartupMode(StringArg(0), StringArg(1));
                return hud.GetStartup();
            case "applyStartupSuggestions":
                hud.ApplyStartupSuggestions();
                return hud.GetStartup();
            case "getServices":
                return await hud.GetServicesAsync();
            case "toggleCouchService":
                return hud.ToggleCouchService(StringArg(0));
            case "setupServiceTask":
                // false: the way back (the tasks of the lock policies go with the service tasks).
                await hud.SetupServiceTaskAsync(Arg(0).ValueKind != JsonValueKind.False);
                return await hud.GetServicesAsync();
            case "releaseLockPolicies":
                // First step of the way back: the lock's Windows policies go before any helper task is deleted.
                return await hud.ReleaseLockPoliciesAsync();
            case "cleanupStuckPolicies":
                await hud.CleanupStuckPoliciesAsync();
                return await hud.GetServicesAsync();
            case "getProcesses":
                return await hud.GetProcessesAsync();
            case "switchProcess":
                hud.SwitchToProcess(StringArg(0));
                return null;
            case "killProcess":
                return await hud.QuitProcessAsync(StringArg(0), Arg(1).ValueKind == JsonValueKind.True);
            case "getUpdates":
                return hud.GetUpdates();
            case "checkUpdates":
                return await hud.CheckUpdatesAsync();
            case "getSelfUpdate":
                return hud.GetSelfUpdate();
            case "checkSelfUpdate":
                return await hud.CheckSelfUpdateAsync();
            case "startSelfUpdate":
                hud.StartSelfUpdate();
                return null;
            case "startUpdate":
                hud.StartUpdate(StringArg(0), Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString() : null);
                return null;
            case "getWeather":
                return await hud.GetWeatherAsync();
            case "searchPlaces":
                return await hud.SearchPlacesAsync(StringArg(0));
            case "getHomeEntities":
                return await hud.GetHomeEntitiesAsync();
            case "activateHomeEntity":
                await hud.ActivateHomeEntityAsync(StringArg(0));
                return null;
            case "pinHomeEntity":
                return hud.PinHomeEntity(StringArg(0));
            case "listSkins":
                return hud.ListSkins().Select(s => s with
                {
                    Css = LocalFiles.Rewrite(s.Css),
                    Sounds = s.Sounds.ToDictionary(kv => kv.Key, kv => LocalFiles.Rewrite(kv.Value))
                });
            case "setSkin":
                hud.SetSkin(StringArg(0));
                return null;
            default:
                throw new InvalidOperationException($"unknown method {method}");
        }
    }

    public static IEnumerable<LibraryItem> ToDto(IEnumerable<LibraryItem> items) => items.Select(i => ToDto(i));

    /// <summary>Thumbnails of local files go through the virtual host; the stored Url stays the original.</summary>
    public static ArtworkChoices ToDto(ArtworkChoices choices)
    {
        static IReadOnlyList<ArtChoice> Map(IReadOnlyList<ArtChoice> list) => list.Select(c => c with { Thumb = LocalFiles.RewriteThumb(c.Thumb) }).ToList();
        return choices with { Cover = Map(choices.Cover), Hero = Map(choices.Hero), Logo = Map(choices.Logo) };
    }

    public static LibraryItem ToDto(LibraryItem item) => item with
    {
        Art = new Artwork
        {
            Cover = item.Art.Cover.Select(LocalFiles.RewriteArt).ToList(),
            Hero = item.Art.Hero.Select(LocalFiles.RewriteArt).ToList(),
            Logo = item.Art.Logo.Select(LocalFiles.RewriteArt).ToList(),
            Icon = item.Art.Icon.Select(LocalFiles.RewriteArt).ToList()
        }
    };
}
