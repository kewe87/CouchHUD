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
        catch (ProfileHeldException ex)
        {
            Console.WriteLine($"[bridge] {method}: profile picker first");
            Post(new { id, error = ex.Message, held = true });
        }
        catch (ParentCodeNeededException ex)
        {
            // Expected: the page asks the parents' code for exactly this action and item, then repeats the call.
            Console.WriteLine($"[bridge] {method}: parents' code needed");
            Post(new { id, error = ex.Message, parent = new { action = ex.Action, item = ex.Item, reason = ex.Reason } });
        }
        catch (CouchHud.Shell.Services.CodeStoreDamagedException ex)
        {
            // Expected while the service's code file is damaged: the page says so and offers the administrator's reset.
            Console.WriteLine($"[bridge] {method}: the service's code store is damaged");
            Post(new { id, error = ex.Message, codesDamaged = true });
        }
        catch (LockedException ex)
        {
            // Expected while the couch is locked: one line, no stack trace; the page knows it by the flag.
            Console.WriteLine($"[bridge] {method}: locked");
            Post(new { id, error = ex.Message, locked = true });
        }
        catch (Exception ex) when (ProfilePolicy.All.Contains(method) && ex is InvalidOperationException or ArgumentException or KeyNotFoundException or IOException)
        {
            // A refused profile step (a game runs, an unknown profile, a name too long, not saved) is expected: one line, no stack trace.
            Console.WriteLine($"[bridge] {method} refused: {ex.Message}");
            Post(new { id, error = ShownText(ex) });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bridge] {method} failed: {ex}");
            Post(new { id, error = ShownText(ex) });
        }
    }

    /// <summary>
    /// The text the page shows: an <see cref="ArgumentException"/> thrown with a parameter name gets " (Parameter 'name')" added by .NET,
    /// which means nothing to the person on the couch (VM test 0.5); the log above keeps the full exception.
    /// </summary>
    private static string ShownText(Exception ex)
    {
        if (ex is ArgumentException { ParamName: { Length: > 0 } name })
        {
            var suffix = $" (Parameter '{name}')";
            if (ex.Message.EndsWith(suffix, StringComparison.Ordinal)) return ex.Message[..^suffix.Length];
        }
        return ex.Message;
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

        // A protected profile waits for its code (the picker is required): only the picker's own list, before the couch lock is even looked at
        // (review M1). The page stays quiet about the refusals ("held").
        if (!ProfilePolicy.OpenWhileHeld.Contains(method) && hud.ProfileHeld)
            throw new ProfileHeldException(Strings.Get("profiles.chooseFirst"));

        // Couch lock: a guest on the couch gets exactly this list. Everything else (adding
        // apps, plugins, settings, file browsing, uninstallers …) could start a program of
        // their choice, so it needs the code first. Leaving the couch checks in Hud itself.
        if (hud.Locked && !OpenWhileLocked.Contains(method)
            && !CouchHud.Core.Integrations.Tv.TvPolicy.OpenWhileLocked.Contains(method)
            && !ProfilePolicy.OpenWhileLocked.Contains(method)
            && !(method == "setSetting" && Arg(0).ValueKind == JsonValueKind.String && CouchHud.Core.Guard.LockPolicy.IsHarmlessSetting(Arg(0).GetString()!, hud.ProfilesGuarded)))
        {
            throw new LockedException(Strings.Get("lock.unlockFirst"));
        }

        // Switched off areas (Settings › Console › Areas): their calls and their entries are refused before anything happens, for the page,
        // the Guide menu and plugin pages alike. The one map of what belongs where is CouchHud.Core.Config.FeatureAreas.
        var areas = hud.Areas;
        if (!areas.AllowsMethod(method)
            || (method == "setSetting" && Arg(0).ValueKind == JsonValueKind.String && !areas.AllowsSetting(Arg(0).GetString()!))
            || (CouchHud.Core.Config.FeatureAreas.ItemMethods.Contains(method) && Arg(0).ValueKind == JsonValueKind.String && hud.HiddenByArea(Arg(0).GetString()!)))
        {
            throw new InvalidOperationException(Strings.Get("areas.off"));
        }

        // TV page and Guide menu rows: one prefix, one handler (lock rules: TvPolicy above; parents: Hud.RequireTvInputChange).
        if (method.StartsWith("tv", StringComparison.Ordinal)) return await hud.Tv.HandleAsync(method, args);

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
            case "toggleFavoriteSite":
                return hud.ToggleFavoriteSite(StringArg(0));
            case "getFavorites":
                return hud.GetFavorites();
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
                // Not open while locked (LockPolicy); with parental controls the parents' code for this game (Hud.RequireSaveChange).
                return await hud.RestoreSavesAsync(StringArg(0), StringArg(1));
            case "deleteSaveBackup":
                return await hud.DeleteSaveBackupAsync(StringArg(0), StringArg(1));
            case "getSaveSettings":
                return hud.Saves.GetSettings();
            case "setSaveSetting":
            {
                // With parental controls the parents' code (Hud.RequireSaveSettingsChange): the settings can make backups disappear.
                hud.RequireSaveSettingsChange(StringArg(0));
                switch (StringArg(0))
                {
                    case "backupDir": hud.Saves.SetBackupDir(Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString() : null); break;
                    case "keep": hud.Saves.SetKeep(Arg(1).GetInt32()); break;
                    case "includeConfig": hud.Saves.SetIncludeConfig(Arg(1).ValueKind == JsonValueKind.True); break;
                    case "autoBackup": hud.Saves.SetAutoBackup(Arg(1).ValueKind == JsonValueKind.True); break;
                    case "autoKeep": hud.Saves.SetAutoKeep(Arg(1).GetInt32()); break;
                    case "autoMaxGameMb": hud.Saves.SetAutoMaxGameMb(Arg(1).GetInt32()); break;
                    case "autoMaxTotalMb": hud.Saves.SetAutoMaxTotalMb(Arg(1).GetInt32()); break;
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
                await hud.PowerAsync(StringArg(0));
                return null;
            case "quit":
                hud.QuitFromUi();
                return null;
            case "restartCouchHud":
                hud.RestartFromUi();
                return null;
            case "getSettings":
                return hud.GetSettings();
            case "setFeatureAreas":
                return hud.SetFeatureAreas(Arg(0).ValueKind == JsonValueKind.Array
                    ? Arg(0).EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : "").ToList()
                    : throw new ArgumentException("setFeatureAreas: argument 0 must be a list"));
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
            case "getLaunchOptions":
                return hud.GetLaunchOptions(StringArg(0));
            case "setLaunchOverride":
            {
                // { args, cwd, target }: a string sets, null goes back to automatic, a missing key stays. Checks: Hud.RequireLaunchChange.
                var changes = Arg(1);
                (bool Set, string? Value) Field(string name) =>
                    changes.ValueKind == JsonValueKind.Object && changes.TryGetProperty(name, out var p)
                        ? p.ValueKind switch
                        {
                            JsonValueKind.String => (true, p.GetString()),
                            JsonValueKind.Null => (true, null),
                            _ => throw new ArgumentException($"{method}: {name} must be a string or null")
                        }
                        : (false, null);
                var (setArgs, startArgs) = Field("args");
                var (setCwd, cwd) = Field("cwd");
                var (setTarget, target) = Field("target");
                return hud.SetLaunchOverride(StringArg(0), new LaunchOverrideChange
                {
                    SetArgs = setArgs, Args = startArgs, SetCwd = setCwd, Cwd = cwd, SetTarget = setTarget, Target = target
                });
            }
            case "searchStore":
                return await hud.SearchStoreAsync(StringArg(0));
            case "getItemInfo":
                return await hud.GetItemInfoAsync(StringArg(0));
            case "setStoreMatch":
                // (id, match, name): match is an app id, "none", or null for automatic. Checks: Hud.SetStoreMatchAsync.
                return await hud.SetStoreMatchAsync(StringArg(0),
                    Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString() : Arg(1).ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : throw new ArgumentException($"{method}: match must be a string or null"),
                    Arg(2).ValueKind == JsonValueKind.String ? Arg(2).GetString() : null);
            case "setItemInfo":
            {
                // { description, developers, publishers, releaseDate, genres }: a string sets, null goes back to the store's, a missing key stays.
                var changes = Arg(1);
                (bool Set, string? Value) Field(string name) =>
                    changes.ValueKind == JsonValueKind.Object && changes.TryGetProperty(name, out var p)
                        ? p.ValueKind switch
                        {
                            JsonValueKind.String => (true, p.GetString()),
                            JsonValueKind.Null => (true, null),
                            _ => throw new ArgumentException($"{method}: {name} must be a string or null")
                        }
                        : (false, null);
                var (setDescription, description) = Field("description");
                var (setDevelopers, developers) = Field("developers");
                var (setPublishers, publishers) = Field("publishers");
                var (setDate, date) = Field("releaseDate");
                var (setGenres, genres) = Field("genres");
                return hud.SetItemInfo(StringArg(0), new InfoChange
                {
                    SetDescription = setDescription, Description = description, SetDevelopers = setDevelopers, Developers = developers,
                    SetPublishers = setPublishers, Publishers = publishers, SetReleaseDate = setDate, ReleaseDate = date, SetGenres = setGenres, Genres = genres
                });
            }
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
            case "reloadArtwork":
                return await hud.ReloadArtworkAsync(StringArg(0));
            case "setArtwork":
            {
                try
                {
                    return ToDto(await hud.SetArtworkAsync(StringArg(0), StringArg(1), Arg(2).ValueKind == JsonValueKind.String ? Arg(2).GetString() : null));
                }
                catch (CouchHud.Core.Library.PickSupersededException)
                {
                    return null; // a later pick for the slot won: the page shows nothing for this one
                }
            }
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
            case "getBattery":
                return hud.Batteries();
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
                // Who has the controller now: the page only while it is on screen (the HUD follows the answer).
                return hud.SetBrowserFocus(Arg(0).ValueKind == JsonValueKind.True);
            case "browserDialogAnswer":
                hud.AnswerBrowserDialog(Arg(0).ValueKind == JsonValueKind.Number ? Arg(0).GetInt32() : 0, Arg(1).ValueKind == JsonValueKind.True,
                    Arg(2).ValueKind == JsonValueKind.String ? Arg(2).GetString() : null);
                return null;
            case "browserType":
                await hud.BrowserTypeAsync(StringArg(0), Arg(1).ValueKind == JsonValueKind.True);
                return null;
            case "browserCommand":
                hud.BrowserCommand(StringArg(0));
                return null;
            case "setBrowserZoom":
                return hud.SetBrowserZoom(Arg(0).GetInt32());
            case "revealBrowserRecent":
                // The parents' code for the recent sites (with parental controls on): checked and used up by the host.
                return hud.RevealBrowserRecent();
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
            case "resetCodeStore":
                // Open while locked like the repair: Windows' UAC prompt is the gate (the service takes an administrator's approval only),
                // and with a damaged code file no code can unlock the couch.
                return await hud.ResetCodeStoreAsync();
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
            // Profiles: only here, the HUD's own page (ProfilePolicy). The picker works at the locked couch; every change needs the unlock
            // (the check above) and the parents' code for exactly that change (Hud.RequireProfileManage).
            case "getProfiles":
                return hud.GetProfiles();
            case "switchProfile":
                return hud.SwitchProfile(StringArg(0), Buttons(1));
            case "profileScreen":
                hud.SetProfileScreen(Arg(0).ValueKind == JsonValueKind.True);
                return null;
            case "profileIdle":
                hud.ProfileIdle();
                return null;
            case "addProfile":
                return hud.AddProfile(StringArg(0), StringArg(1), StringArg(2), Arg(3).ValueKind == JsonValueKind.True);
            case "renameProfile":
                return hud.RenameProfile(StringArg(0), StringArg(1));
            case "setProfileLook":
                return hud.SetProfileLook(StringArg(0), StringArg(1), StringArg(2));
            case "setProfileRestricted":
                return hud.SetProfileRestricted(StringArg(0), Arg(1).ValueKind == JsonValueKind.True);
            case "setProfileCode":
                return hud.SetProfileCode(StringArg(0), Buttons(1));
            case "removeProfile":
                return hud.RemoveProfile(StringArg(0));
            case "getProfileRules":
                return hud.GetProfileRules(StringArg(0));
            case "setProfileRule":
                return hud.SetProfileRule(StringArg(0), StringArg(1), Arg(2));
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
                return await hud.CaptureAsync(StringArg(0), Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString() : null);
            case "getDownloads":
                return await hud.GetDownloadsAsync();
            case "getDownloadCenter":
                return hud.GetDownloadCenter();
            case "dismissDownload":
                return hud.DismissDownload(Arg(0).ValueKind == JsonValueKind.String ? Arg(0).GetString() : null);
            case "getUninstalled":
            {
                var o = Arg(0);
                int Int(string name, int fallback) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;
                string? Text(string name) => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var refresh = o.ValueKind == JsonValueKind.Object && o.TryGetProperty("refresh", out var rf) && rf.ValueKind == JsonValueKind.True;
                return await hud.GetUninstalledAsync(Int("offset", 0), Int("limit", 60), Text("source"), Text("query"), refresh);
            }
            case "installGame":
                return hud.InstallGame(StringArg(0));
            case "cancelDownload":
                return hud.CancelDownload(StringArg(0));
            case "openClientDownloads":
                return hud.OpenClientDownloads(StringArg(0));
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
            case "openPluginSettingPage":
                return hud.OpenPluginSettingPage(StringArg(0), StringArg(1));
            case "endPluginVisit":
                hud.EndPluginVisit(StringArg(0), StringArg(1));
                return true;
            case "getPluginTabs":
                return hud.GetPluginTabs();
            case "getPluginPage":
                return await hud.GetPluginPageAsync(StringArg(0), StringArg(1));
            case "activatePluginRow":
                return await hud.ActivatePluginRowAsync(StringArg(0), StringArg(1), StringArg(2));
            case "activatePluginAction":
                return await hud.ActivatePluginActionAsync(StringArg(0), StringArg(1), StringArg(2));
            case "changePluginRow":
                return await hud.ChangePluginRowAsync(StringArg(0), StringArg(1), StringArg(2), StringArg(3));
            case "getQuickActions":
                return await hud.GetQuickActionsAsync();
            case "getPluginGate":
                return hud.GetPluginGate(StringArg(0), Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString() : null,
                    Arg(2).ValueKind == JsonValueKind.String ? Arg(2).GetString() : null, Arg(3).ValueKind == JsonValueKind.String ? Arg(3).GetString() : null);
            case "setPluginFunctionLevel":
                return hud.SetPluginFunctionLevel(StringArg(0), StringArg(1), StringArg(2));
            case "approvePlugin":
                return hud.ApprovePlugin(StringArg(0), StringArg(1));
            case "runQuickAction":
                return await hud.RunQuickActionAsync(StringArg(0), StringArg(1));
            case "installPackage":
                return hud.InstallPackage(StringArg(0), StringArg(1));
            case "getCatalog":
                return await hud.GetCatalogAsync(Arg(0).ValueKind == JsonValueKind.True);
            case "getCatalogUpdates":
                return hud.GetCatalogUpdates();
            case "installCatalogEntry":
            case "updateCatalogEntry":
                return await hud.InstallCatalogEntryAsync(StringArg(0), StringArg(1));
            case "getCatalogDetail":
                return await hud.GetCatalogDetailAsync(StringArg(0), StringArg(1));
            case "getCatalogSources":
                return hud.GetCatalogSources();
            case "addCatalogSource":
                // The fourth argument is the "I trust this developer" confirmation: only an explicit true counts.
                return hud.AddCatalogSource(StringArg(0), Arg(1).ValueKind == JsonValueKind.String ? Arg(1).GetString() : null, Arg(2).ValueKind == JsonValueKind.String ? Arg(2).GetString() : null,
                    Arg(3).ValueKind == JsonValueKind.True);
            case "removeCatalogSource":
                return hud.RemoveCatalogSource(StringArg(0));
            case "uninstallPlugin":
                return hud.UninstallPlugin(StringArg(0));
            case "acceptRevokedPlugin":
                return hud.AcceptRevokedPlugin(StringArg(0));
            case "confirmPluginExe":
                return hud.ConfirmPluginExe(StringArg(0), StringArg(1));
            case "getPluginSuggestions":
                return await hud.GetPluginSuggestionsAsync();
            case "dismissPluginSuggestion":
                hud.DismissPluginSuggestion(StringArg(0));
                return null;
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
            case "codeEntryState":
                // Only makes screenshots refuse while true: no harm in any value.
                hud.SetCodeEntryState(Arg(0).ValueKind == JsonValueKind.True);
                return null;
            case "pairingState":
                // The phone's pairing code or QR code on screen: again only makes screenshots refuse.
                hud.SetPairingState(Arg(0).ValueKind == JsonValueKind.True);
                return null;
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
            case "refreshUpdates":
                hud.RefreshUpdatesIfStale();
                return null;
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
        return choices with { Cover = Map(choices.Cover), Hero = Map(choices.Hero), Logo = Map(choices.Logo), Square = Map(choices.Square) };
    }

    public static LibraryItem ToDto(LibraryItem item) => item with { Art = ToDto(item.Art) };

    /// <summary>Image addresses as the UI may load them: cached copies and local files, never the net.</summary>
    public static Artwork ToDto(Artwork art) => new()
    {
        Cover = art.Cover.Select(LocalFiles.RewriteArt).ToList(),
        Hero = art.Hero.Select(LocalFiles.RewriteArt).ToList(),
        Logo = art.Logo.Select(LocalFiles.RewriteArt).ToList(),
        Square = art.Square.Select(LocalFiles.RewriteArt).ToList(),
        Icon = art.Icon.Select(LocalFiles.RewriteArt).ToList()
    };
}
