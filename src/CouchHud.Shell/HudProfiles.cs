using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Localization;
using CouchHud.Core.Parental;
using CouchHud.Core.Security;
using CouchHud.Shell.Services;

namespace CouchHud.Shell;

/// <summary>
/// Profiles part 2: the profile picker, the switch and the parents' management of profiles. The rules are pure decisions in Core
/// (<see cref="ProfileSwitch"/>); here they are enforced. Only the HUD's own page reaches this (the bridge, <see cref="ProfilePolicy"/>):
/// not the phone remote (its keys do not count while a profile screen is open), not MCP, the assistant or a plugin.
/// </summary>
public sealed partial class Hud
{
    // Somebody chose the profile on the couch since the start (or there is only one profile). False again after the couch lock, the
    // screensaver and the display going off when the profile on the couch is protected (it needs its code to be entered again).
    private bool _profileChosen;
    // The picker or the profile management is on the TV: the phone's keys do not count there (no switching, creating or codes by remote).
    private bool _profileScreen;
    // The wrong-code counter per child's code, kept in a file per profile (a restart gives no fresh tries).
    private readonly Dictionary<string, Lockout> _profileLockouts = new();
    // The parents' code entered for a profile change, as a fresh salted hash in memory (never on disk, never logged), 90 seconds: only to
    // refuse a child's code that equals it when the parents' code itself is kept in the service (review N4).
    private (string Hash, DateTimeOffset Until)? _lastManageCode;

    /// <summary>The parents just entered their code for a profile change: kept as a salted hash for the comparison in <see cref="SetProfileCode"/>.</summary>
    private void RememberManageCode(string? action, string[]? sequence)
    {
        if (action != ProfileSwitch.ManageAction || sequence is null) return;
        _lastManageCode = (Passcode.Hash(sequence), MonoNow() + TimeSpan.FromSeconds(90));
    }

    /// <summary>A new couch or parents' code equals a child's own code (review N4): refused, a child would know the parents' code.</summary>
    private void RefuseChildCode(IReadOnlyList<string> sequence)
    {
        if (config.Current.AllProfiles.Any(p => p.HasCode && Passcode.Verify(sequence, p.CodeHash)))
            throw new ArgumentException(Strings.Get("profiles.codeIsChild"));
    }

    /// <summary>The picker stands in front: at the start with several profiles, and after a lock for a protected profile.</summary>
    private bool ProfilePickerPending => !_profileChosen && ProfileSwitch.PickerAtStart(config.Current);

    /// <summary>A protected profile waits for its code: nothing starts until it (or another one) is entered.</summary>
    public bool ProfileHeld => ProfilePickerPending && ProfileSwitch.IsProtected(config.Current, config.Current.ActiveProfile.Id);

    /// <summary>Starting something while a protected profile waits for its code is refused (the picker is in front anyway).</summary>
    public void RequireProfileChosen()
    {
        if (ProfileHeld) throw new ProfileHeldException(Strings.Get("profiles.chooseFirst"));
    }

    /// <summary>
    /// Whose skin, language, sounds and animations the page gets (<see cref="ProfileSwitch.Look"/>): while a protected profile waits for its
    /// code the household's (the default profile's), so the picker gives nothing of the waiting person away; otherwise the profile on the couch.
    /// </summary>
    public ProfileView LookProfile => ProfileSwitch.Look(config.Current, ProfileHeld);

    /// <summary>The host's texts follow <see cref="LookProfile"/> (the picker came up or a profile was chosen).</summary>
    private void UseLookLanguage()
    {
        var before = Strings.Language;
        Strings.Use(LookProfile.Language);
        if (Strings.Language != before) plugins.Events.RaiseLanguageChanged(Strings.Language);
    }

    /// <summary>Parental controls on and several profiles: the screensaver and display-off settings are the parents' (review NEU-1).</summary>
    public bool ProfilesGuarded => ProfileSwitch.ChildrenGuarded(config.Current);

    // The last real input on the couch (controller, keyboard, mouse; not CouchHUD's own, not the phone), the end of the last game or the
    // moment a profile was chosen: the host's own idle threshold for a protected profile (ProfileIdleClock, review NEU-1). MonoNow.
    private DateTimeOffset _profileActivity = DateTimeOffset.MinValue;

    /// <summary>Somebody is on the couch (or a game just ended, or a profile was chosen): the idle threshold starts again.</summary>
    private void ProfileActivity() => _profileActivity = MonoNow();

    /// <summary>
    /// From the session timer: real keyboard or mouse input since the last tick counts as activity (the controller counts in OnPadDown),
    /// a running game keeps the time from running; after the threshold without input a protected profile goes back to the picker,
    /// whatever the screensaver and display settings say and whether CouchHUD is in front or not.
    /// </summary>
    private void ProfileIdleTick()
    {
        if (_profileActivity == DateTimeOffset.MinValue) ProfileActivity();
        var running = AnyGameRunning;
        if (running || RealInputWithin(TimeSpan.FromSeconds(4))) ProfileActivity();
        if (!_profileChosen || !ProfileSwitch.PickerAtStart(config.Current)) return;
        if (!ProfileIdleClock.Due(_profileActivity, MonoNow(), running, ProfileSwitch.IsProtected(config.Current, config.Current.ActiveProfile.Id))) return;
        ProfileActivity();
        ProfileIdle();
    }

    /// <summary>A game, an app session or a game outside CouchHUD runs: no switch.</summary>
    private bool AnyGameRunning => GameBusy || _sessions.Others.Count > 0;

    /// <summary>The profile on the couch has no time left, is outside its time window, blocked or its account was tampered with.</summary>
    private bool ProfileExhausted
    {
        get
        {
            if (!config.Current.ParentalEnabled) return false;
            var controller = Parental;
            if (controller.Service.Expired || controller.Blocked) return true;
            return controller.Service.Status(null) is { Enabled: true } st && (st.RemainingSeconds is <= 0 || !st.InWindow);
        }
    }

    private SwitchDecision DecideSwitch(string to) =>
        ProfileSwitch.Decide(config.Current, config.Current.ActiveProfile.Id, to, ProfileExhausted, AnyGameRunning, reentry: ProfileHeld);

    private static string NeedName(SwitchDecision d) => d.Need switch
    {
        SwitchNeed.Free => "free",
        SwitchNeed.ProfileCode => "profileCode",
        SwitchNeed.ParentCode => "parentCode",
        _ => d.Refusal == SwitchRefusal.Same ? "same" : d.Refusal == SwitchRefusal.GameRunning ? "gameRunning" : "refused"
    };

    /// <summary>The picker and the management page: every profile with what entering it needs from here. Never a code or its hash.</summary>
    public object GetProfiles()
    {
        var c = config.Current;
        var active = c.ActiveProfile.Id;
        var exhausted = ProfileExhausted;
        var running = AnyGameRunning;
        var held = ProfileHeld;
        return new
        {
            active,
            picker = ProfilePickerPending,
            pickRequired = held,
            max = AppConfig.MaxProfiles,
            parentalEnabled = c.ParentalEnabled,
            childrenGuarded = ProfileSwitch.ChildrenGuarded(c),
            exhausted,
            gameRunning = running,
            profiles = c.AllProfiles.Select(p => new
            {
                id = p.Id,
                name = p.DisplayName,
                avatar = p.DisplayAvatar,
                color = p.DisplayColor,
                isDefault = p.IsDefault,
                restricted = p.Restricted,
                child = ProfileSwitch.IsChild(c, p.Id),
                hasCode = p.HasCode,
                active = p.Id == active,
                isProtected = ProfileSwitch.IsProtected(c, p.Id),
                need = NeedName(ProfileSwitch.Decide(c, active, p.Id, exhausted, running, held))
            }).ToList()
        };
    }

    /// <summary>The picker or the management page is open (true) or closed: while open, the phone's keys do not reach the HUD.</summary>
    public void SetProfileScreen(bool open) => _profileScreen = open;

    /// <summary>
    /// The screensaver started (the UI says so) or the display went off (<see cref="DisplayOff"/>): a protected profile goes back to the
    /// picker; after a restart it is there anyway. Not on the end of the two-minute unlock, a game start or the way back to the HUD
    /// (review N3: those lock the couch too, but the same person is still there), and never during a game.
    /// </summary>
    public void ProfileIdle()
    {
        if (!_profileChosen || !ProfileSwitch.PickerAtStart(config.Current)) return;
        // Never in the middle of a game (review N3): the person playing stays until the game ends.
        if (AnyGameRunning) return;
        if (!ProfileSwitch.IsProtected(config.Current, config.Current.ActiveProfile.Id)) return;
        _profileChosen = false;
        // Whoever opened the couch before is not the next person at the picker (VM test of part 2): the unlock with the setup hold, the
        // parents' grants and the service's cached grants go, so nothing unlocked (the MCP token in the settings, say) waits behind the picker.
        _setupHoldUntil = DateTimeOffset.MinValue;
        _lastManageCode = null;
        ForgetUnlock();
        UseLookLanguage();
        Console.WriteLine("[profiles] back to the picker (protected profile)");
        Emit("profiles", new { picker = true });
    }

    /// <summary>
    /// Switch to <paramref name="id"/>. What it needs comes from <see cref="ProfileSwitch.Decide"/>: nothing, the child's own code
    /// (<paramref name="code"/>, with its own lockout; the parents' grant for this switch opens it too) or the parents' grant for exactly
    /// this switch. Without what it needs the answer says so (<c>need</c>), nothing changes.
    /// </summary>
    public object SwitchProfile(string id, string[]? code)
    {
        var decision = DecideSwitch(id);
        switch (decision.Need)
        {
            case SwitchNeed.Refused when decision.Refusal == SwitchRefusal.Same:
                // The picker at the start, the profile on the couch chosen again (it needs no code): it stays.
                _profileChosen = true;
                UseLookLanguage();
                ProfileActivity();
                Emit("profiles", new { picker = false });
                return new { ok = true, switched = false };
            case SwitchNeed.Refused:
                throw new InvalidOperationException(Strings.Get(decision.Refusal == SwitchRefusal.GameRunning ? "profiles.gameRunning" : "profiles.unknown"));
            case SwitchNeed.ParentCode:
                if (!ParentGrantForSwitch(id)) return new { ok = false, need = "parentCode" };
                break;
            case SwitchNeed.ProfileCode:
            {
                // Only the parents' code itself stands in for a child's code, never the unlocked couch (review M2): whoever just unlocked
                // the couch for something else is not entitled to a sibling's profile.
                if (HasParentCode && _grant.TryConsume(ProfileSwitch.SwitchAction, id)) break;
                if (code is null) return new { ok = false, need = "profileCode" };
                var lockout = ProfileLockout(id);
                var wait = lockout.Wait;
                if (wait > TimeSpan.Zero) return new { ok = false, need = "profileCode", waitSeconds = (int)Math.Ceiling(wait.TotalSeconds) };
                if (!Passcode.Verify(code, config.Current.Profile(id).CodeHash))
                {
                    lockout.Failed();
                    return new { ok = false, need = "profileCode", wrong = true, waitSeconds = (int)Math.Ceiling(lockout.Wait.TotalSeconds) };
                }
                lockout.Succeeded();
                break;
            }
        }
        // A game may have started while the code was entered: decided again right before the switch.
        if (AnyGameRunning) throw new InvalidOperationException(Strings.Get("profiles.gameRunning"));
        var same = config.Current.ActiveProfile.Id == id;
        if (!same) ActivateProfile(id);
        _profileChosen = true;
        // The person is in: the host's texts in their language again (the picker had the household's).
        UseLookLanguage();
        ProfileActivity();
        Emit("profiles", new { picker = false, switched = !same });
        return new { ok = true, switched = !same };
    }

    /// <summary>The parents' grant for exactly this switch (without a parents' code set up: the unlocked couch).</summary>
    private bool ParentGrantForSwitch(string id) =>
        HasParentCode ? _grant.TryConsume(ProfileSwitch.SwitchAction, id) : IsUnlocked;

    private Lockout ProfileLockout(string id)
    {
        if (!_profileLockouts.TryGetValue(id, out var lockout))
            _profileLockouts[id] = lockout = new Lockout(MonoNow, ProfileIds.LockoutFile(dataDir, id));
        return lockout;
    }

    /// <summary>
    /// The switch itself: the old account written, the profile set, the parental controller rebuilt for the new profile, everything
    /// that belongs to a person rebuilt (library state, favorites, collections, language; the UI reloads for skin and sounds), and every
    /// release of the old profile dropped (parents' grants, the recent sites, the unlock chain); an open couch browser closes.
    /// </summary>
    private void ActivateProfile(string id)
    {
        _parentalController?.Service.Flush();
        // First everything of the old profile goes (review N1, M3): the parents' grants and the code chain, the revealed recent sites, the
        // open couch browser, the unlock with the setup hold and the service's cached grants, the parents' code kept for comparing.
        _grant.Clear();
        _parentCodeChainUntil = DateTimeOffset.MinValue;
        _browserRecentShown = false;
        _lastManageCode = null;
        if (_browser is not null) CloseBrowser(_browsing);
        _setupHoldUntil = DateTimeOffset.MinValue;
        ForgetUnlock();
        // Then the switch, then the parental controller for the new profile.
        var switched = false;
        config.UpdateOrThrow(c => switched = c.SwitchActiveProfile(id));
        if (!switched) throw new InvalidOperationException(Strings.Get("profiles.unknown"));
        _parentalController = null;
        _ = Parental;
        Strings.Use(config.Current.ActiveProfile.Language);
        plugins.Events.RaiseLanguageChanged(Strings.Language);
        library.ReapplyUserState();
        SyncBrowserRecent();
        Emit("parental", new { kind = "status" });
        Console.WriteLine($"[profiles] switched to {config.Current.ActiveProfile.Id}");
    }

    // -----------------------------------------------------------------------
    // Management: every change needs the unlock (the bridge) and the parents' code for exactly this change.

    private void RequireProfileManage(string? id) => RequireParentGrant(ProfileSwitch.ManageAction, id);

    private void RequireKnownProfile(string id)
    {
        if (config.Current.Profile(id).Id != id) throw new KeyNotFoundException(Strings.Get("profiles.unknown"));
    }

    public object AddProfile(string name, string avatar, string color, bool restricted)
    {
        var clean = ProfileNames.Accept(name) ?? throw new ArgumentException(Strings.Get("profiles.nameInvalid", ProfileNames.MaxLength));
        if (!ProfileLooks.IsAvatar(avatar) || !ProfileLooks.IsColor(color)) throw new ArgumentException(Strings.Get("profiles.lookInvalid"));
        if (config.Current.AllProfiles.Count >= AppConfig.MaxProfiles) throw new InvalidOperationException(Strings.Get("profiles.max", AppConfig.MaxProfiles));
        RequireProfileManage(null);
        string? created = null;
        config.UpdateOrThrow(c => created = c.AddProfile(clean, avatar, color, restricted).Id);
        // The parents are here and chose: the second profile does not put the picker in front of them right away.
        _profileChosen = true;
        ProfileActivity();
        // With parental controls on, the new profile's play time account starts now (like every other one), so from here on a missing
        // account is tampering, not "0 minutes played".
        if (config.Current.ParentalEnabled) StartAllPlayAccounts();
        Console.WriteLine($"[profiles] created {created}");
        return GetProfiles();
    }

    public object RenameProfile(string id, string name)
    {
        RequireKnownProfile(id);
        var clean = ProfileNames.Accept(name) ?? throw new ArgumentException(Strings.Get("profiles.nameInvalid", ProfileNames.MaxLength));
        RequireProfileManage(id);
        var ok = false;
        config.UpdateOrThrow(c => ok = c.RenameProfile(id, clean));
        if (!ok) throw new InvalidOperationException(Strings.Get("profiles.notSaved"));
        return GetProfiles();
    }

    public object SetProfileLook(string id, string avatar, string color)
    {
        RequireKnownProfile(id);
        if (!ProfileLooks.IsAvatar(avatar) || !ProfileLooks.IsColor(color)) throw new ArgumentException(Strings.Get("profiles.lookInvalid"));
        RequireProfileManage(id);
        var ok = false;
        config.UpdateOrThrow(c => ok = c.SetProfileLook(id, avatar, color));
        if (!ok) throw new InvalidOperationException(Strings.Get("profiles.notSaved"));
        return GetProfiles();
    }

    public object SetProfileRestricted(string id, bool restricted)
    {
        RequireKnownProfile(id);
        if (id == ProfileIds.Default) throw new InvalidOperationException(Strings.Get("profiles.notChild"));
        RequireProfileManage(id);
        var ok = false;
        config.UpdateOrThrow(c => ok = c.SetProfileRestricted(id, restricted));
        if (!ok) throw new InvalidOperationException(Strings.Get("profiles.notSaved"));
        if (!restricted) ForgetProfileLockout(id);
        return GetProfiles();
    }

    /// <summary>
    /// Sets (<paramref name="code"/>) or removes (null) a child's own code. Only the hash is kept (salted PBKDF2), the code is never logged;
    /// the wrong-code counter starts over with a new code.
    /// </summary>
    public object SetProfileCode(string id, string[]? code)
    {
        RequireKnownProfile(id);
        if (!config.Current.Profile(id).Restricted) throw new InvalidOperationException(Strings.Get("profiles.notChild"));
        if (code is not null) Passcode.Validate(code);
        RequireProfileManage(id);
        // Never the parents' code (review N4): a child who knows its own code would know the parents' one. Compared with the local hash, or
        // (codes in the service) with the parents' code entered for this change, kept as a salted hash in memory only, for this check.
        var parent = _lastManageCode is { } kept && MonoNow() < kept.Until ? kept.Hash : null;
        _lastManageCode = null;
        if (code is not null && (Passcode.MatchesLocal(code, config.Current.PasscodeHash) || parent is not null && Passcode.Verify(code, parent)))
            throw new ArgumentException(Strings.Get("profiles.codeIsParent"));
        var hash = code is null ? null : Passcode.Hash(code);
        var ok = false;
        config.UpdateOrThrow(c => ok = c.SetProfileCode(id, hash));
        if (!ok) throw new InvalidOperationException(Strings.Get("profiles.notSaved"));
        ForgetProfileLockout(id);
        Console.WriteLine($"[profiles] {(code is null ? "removed the code of" : "set a code for")} {id}");
        return GetProfiles();
    }

    /// <summary>
    /// Deletes a further profile: never the default one, never the one on the couch. Its play time account is renamed to
    /// <c>….deleted-&lt;time&gt;</c> (kept as a trace, not loaded again), its wrong-code counter removed.
    /// </summary>
    public object RemoveProfile(string id)
    {
        if (id == ProfileIds.Default) throw new InvalidOperationException(Strings.Get("profiles.deleteDefault"));
        RequireKnownProfile(id);
        if (config.Current.ActiveProfile.Id == id) throw new InvalidOperationException(Strings.Get("profiles.deleteActive"));
        RequireProfileManage(id);
        var ok = false;
        config.UpdateOrThrow(c => ok = c.RemoveProfile(id));
        if (!ok) throw new InvalidOperationException(Strings.Get("profiles.notSaved"));
        try
        {
            ProfileIds.RetireLedger(dataDir, id, DateTime.Now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[profiles] the play time account of {id} stays: {ex.Message}");
        }
        ForgetProfileLockout(id);
        Console.WriteLine($"[profiles] deleted {id}");
        return GetProfiles();
    }

    private void ForgetProfileLockout(string id)
    {
        _profileLockouts.Remove(id);
        try
        {
            CouchHud.Core.Files.DurableFile.DeleteWithBackup(ProfileIds.LockoutFile(dataDir, id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[profiles] wrong-code counter of {id}: {ex.Message}");
        }
    }

    /// <summary>The rules of one profile for the management page (no play time account: that is the profile on the couch's).</summary>
    public object GetProfileRules(string id)
    {
        RequireKnownProfile(id);
        var r = config.Current.Profile(id).Rules;
        return new
        {
            id,
            weekdayMinutes = r.WeekdayMinutes,
            weekendMinutes = r.WeekendMinutes,
            perGameMinutes = r.PerGameMinutes,
            maxAge = AgeRatings.LimitFor(r.AgeLimit, ActiveRatingSystem),
            // The system the picker offers its steps in (the household's, see SetRatingSystem).
            ratingSystemActive = ActiveRatingSystem,
            // After a change of the Windows region under "auto": the system the limit was saved under and the limit in it (shown then).
            ratingSystemSaved = JudgedRatingSystems is [_, var saved] ? saved : null,
            maxAgeSaved = JudgedRatingSystems is [_, var savedIn] ? AgeRatings.LimitFor(r.AgeLimit, savedIn) : (int?)null,
            blockUnknownAge = r.BlockUnknownAge,
            countApps = r.CountApps,
            windowStart = r.HasWindow ? r.WindowStartMinute : -1,
            windowEnd = r.HasWindow ? r.WindowEndMinute : -1
        };
    }

    /// <summary>One rule of one profile, with the parents' code for exactly this profile. The main switch is not per profile.</summary>
    public object SetProfileRule(string id, string key, System.Text.Json.JsonElement value)
    {
        RequireKnownProfile(id);
        if (key == "enabled") throw new ArgumentException(Strings.Get("parental.unknownSetting", key));
        RequireProfileManage(id);
        ApplyProfileRule(id, key, value);
        if (id == Parental.ProfileId) Emit("parental", new { kind = "status" });
        return GetProfileRules(id);
    }
}
