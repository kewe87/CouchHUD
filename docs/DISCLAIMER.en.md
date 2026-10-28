# Disclaimer

[Deutsch](DISCLAIMER.md) | **English**

*As of October 2026, applies to CouchHUD from version 0.3*

> This is a translation. In case of doubt, the German version ([DISCLAIMER.md](DISCLAIMER.md)) prevails.

CouchHUD is free software under the [GNU General Public License v3.0](../LICENSE). It is provided free of charge and **without any warranty**, to the extent permitted by law (sections 15 and 16 of the GPL-3.0). You use it at your own risk.

## Changes to Windows

Depending on the mode you choose and the features you switch on, CouchHUD can change Windows settings. In **app mode** CouchHUD behaves like a normal application. In **shell mode** CouchHUD can, among other things:

- register itself as the Windows shell for your user account (instead of the desktop),
- affect the sign-in (couch lock, guard process, automatic Windows sign-in: the password is entered once, checked and stored by Windows in encrypted form as an LSA secret),
- block Task Manager in couch mode,
- pause selected Windows services and start the startup programs itself,
- if you ask it to, download **CaseHUD** (a separate program for the displays in the case), verify it and install it with an administrator's approval (its setup installs a sensor service and a driver), and start it in the background,
- minimize windows of game clients and update pop-ups, and take the Guide button away from the Xbox Game Bar ("CouchHUD stays in front", reset in PC mode and on quit).

Every change is described individually, with its location, whether it needs admin rights and how to undo it, in CouchHUD under **Settings › HUD › What CouchHUD changes in Windows**. For emergencies there is the recovery mode: hold **Ctrl + Shift** or **LB + RB + View** for about 1.5 seconds during start-up.

## Important notes

- **Automatic sign-in** means that anyone with access to the PC gets into your Windows account if no couch lock is active.
- **The couch lock is only as strong as your account's Windows password.** It keeps children and guests at the controller away from the desktop, but it is not a security solution: anyone with a keyboard and time, who starts the PC from other media or removes the drive, can get to your data. With automatic sign-in the password is stored on the PC (encrypted by Windows). So switch on **device encryption or BitLocker** and use a proper password. The lock also does not protect everything that is started from CouchHUD (e.g. game clients with their own browsers or file dialogs); CouchHUD keeps these clients in the background in couch mode, but cannot close every path. The Windows sign-in as the lock requires an account with a password; the controller code also works without one, with a clear warning (without a password anyone with a keyboard gets into the account). Automatic sign-in needs a password and is skipped otherwise. While the lock is closed, CouchHUD only starts games, minimizes clearly recognized non-games (Settings, Explorer, consoles, browsers), closes file and print dialogs and sets reversible Windows policies (details under *What CouchHUD changes in Windows*). This is kiosk protection against children and guests at the controller, not protection against attacks with a keyboard, boot media, a removed drive or administrator rights; for that you need a good password and BitLocker or device encryption. CouchHUD does not switch these on itself.
- **Parental controls have limits.** They protect against children at the controller, not against someone who knows their way around Windows: anyone with file access to the user folder (settings, counters) can change or delete them (a modified time file does make the day count as used up, but the rules themselves can be changed). They only work with CouchHUD as the Windows shell and the couch lock switched on, not in app mode and not in PC mode. Games that were not started through CouchHUD are only counted while CouchHUD recognizes them as a running game; entries without a recognizable program folder (websites, some Store apps) cannot be measured and therefore only start with the parents' code. Age limits are based on the Steam store's information and are not an official age rating. The parents' code is the couch lock's code or the Windows password: whoever knows it can change everything.
- **Pausing a game** suspends its processes. Online games can lose the connection, and anti-cheat systems can react to it. The feature is off by default.
- **Plugins** come from third parties. They run with the same rights as CouchHUD. Only install plugins from sources you trust. The respective authors are responsible for plugins and their consequences.
- **Cheats and mods** (e.g. via the Wand plugin) can violate the terms of use of games and lead to bans, especially in online games.
- **Trademarks** such as Xbox, PlayStation, Steam, Epic Games, GOG, Ubisoft, EA, Battle.net, Windows, Discord, WhatsApp and others belong to their owners. CouchHUD is not affiliated with them. The built-in skins have names of their own, use no original graphics or logos and only describe a style.

By confirming on first start you declare that you have read these notes.
