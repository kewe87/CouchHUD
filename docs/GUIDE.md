# CouchHUD – Handbuch

Das vollständige Handbuch: alle Funktionen, Einstellungen, Steuerung, Konfiguration und Architektur. Kurzüberblick und Screenshots: [README](../README.md). English: [GUIDE.en.md](GUIDE.en.md)

Controller-first Fullscreen-Oberfläche für den Wohnzimmer-PC am TV. Ersetzt auf Wunsch die komplette Windows-Shell (`explorer.exe`): Nach dem Login landest du direkt in CouchHUD, bedienst alles mit dem Controller und verlässt es nur, wenn du bewusst in den **PC-Modus** wechselst.

Unabhängig von Steam Big Picture, Playnite & Co. Steam-, Epic-, GOG-, Ubisoft- und Xbox-Spiele werden aber automatisch erkannt.

![Home](screenshot-home.png)

## Features

**Oberfläche**
- **Deutsch und Englisch**: Die Sprache folgt standardmäßig der Windows-Anzeigesprache und lässt sich unter *Einstellungen › Info › Sprache* sofort umstellen (Oberfläche, Meldungen, Tray-Menü und Setup). Texte und Begriffe: [i18n.md](i18n.md).
- **Konsolen-UI** mit den Tabs Home, Spiele, Apps, News, Smart Home, Assistent und Einstellungen. Smart Home erscheint, sobald Home Assistant verbunden ist, der Assistent, sobald ein KI-Anbieter eingerichtet ist. Skaliert von 1080p bis 4K.
- **Home-Shelf** mit Clear Logos, großem Hero-Artwork und Hintergrund-Trailern: Bleibt der Fokus kurz auf einem Spiel, läuft der Steam-Trailer stumm im Hintergrund an (abschaltbar).
- **Farben aus dem Artwork**: Der Hintergrund leuchtet in der Farbe des fokussierten Covers.
- **Tiefe und Bewegung**: Parallax, leichtes Kippen mit Lichtreflex auf der fokussierten Kachel, Boot-Screen, Seitenübergänge und ein Übergang beim Spielstart („Spiel startet …“ mit Artwork). *Einstellungen › Darstellung › Animationen reduzieren* nimmt Tiefeneffekt, Kippen und Lichtreflex heraus.
- **Skins**: CouchHUD, PS4 Classic, Xbox, Cyberpunk und Nova, mit Live-Vorschau. Eigene Skins sind möglich (siehe [Skins](#skins)).
- **HDR-Modus** (Aus, Automatisch, An): Artwork und Trailer in voller HDR-Helligkeit, Wide-Gamut-Farben pro Skin (echtes Neon bei Cyberpunk), Lichtreflex über der fokussierten Kachel, Boot-Blitz. „An“ schaltet Windows-HDR ein, solange CouchHUD läuft.
- **UI-Sounds pro Skin** (live synthetisiert; eigene Skins können eigene Sounddateien mitbringen) und optional dezente **Hintergrundmusik** pro Skin, mit eigenen Lautstärkereglern. Alles verstummt, sobald ein Spiel oder eine andere Vollbild-App vorne ist.
- Controller-Vibration (abschaltbar).
- **Bildschirmschoner** gegen OLED-Einbrennen: Artwork-Diashow, wandernde Uhr, Abdunkeln oder Schwarz, dazu Pixel-Shift und auf Wunsch Fernseher aus. Controller-Eingaben zählen auch für Windows als Aktivität.

**Bibliothek**
- Steam (alle Library-Folders), Epic, GOG (mit und ohne Galaxy), Ubisoft Connect und Xbox/Game Pass werden automatisch erkannt, dazu eigene Einträge. EA app und Battle.net starten zwar als Spiele-Clients mit, ihre Spiele erscheinen aber nur über überwachte Spieleordner oder eigene Einträge.
- **Spieleordner überwachen**: Alles in z.B. `D:\Games` landet automatisch in der Bibliothek, auch später installierte Spiele. Eine Suche findet typische Spieleordner auf allen Laufwerken.
- **Hinzufügen**: Auf den Seiten Spiele und Apps ist die letzte Kachel **+ Hinzufügen** (auch im Filter-Menü, X). Auf der Apps-Seite bietet CouchHUD zuerst die **Liste der installierten Programme** wie im Startmenü, auch Store-Apps wie Claude (LB/RB springen von Buchstabe zu Buchstabe, X sucht; „Andere …“ am Ende wählt eine Datei). Schon vorhandene Einträge sind markiert. Store-Apps starten über Windows selbst (`shell:AppsFolder`); da kein Programmordner bekannt ist, zeigt CouchHUD sie nicht als „läuft“ an. Hinzufügen braucht bei gesperrter Couch den Code.
- **Startparameter und Arbeitsordner** (Expertenmodus, nur eigene Einträge mit EXE): im Bearbeiten-Dialog des Eintrags. Die Parameter gehen einzeln an das Programm, ohne Shell; der Arbeitsordner muss existieren. Der Assistent und MCP können sie nicht ändern (nur beim Anlegen über `add_entry`, mit Bestätigung auf dem TV). Beispiel „Claude Code“: Programm `C:\Users\<Name>\.local\bin\claude.exe`, Startparameter `--continue`, Arbeitsordner `D:\couchhud` (oder `wt.exe` mit `-d D:\couchhud claude --continue` für Windows Terminal). Terminals, Shells und Interpreter (`claude`, `python`, `node`, `bash` …) sind nie Spiele, nur Apps; auf einer gesperrten Couch brauchen sie den Code.
- **Ordner scannen**: CouchHUD schlägt die passenden EXEs vor und ignoriert Installer, Crash-Reporter und Redistributables. Einträge kannst du umbenennen, ausblenden oder als Spiel bzw. App einordnen.
- **Suchen, sortieren, filtern** (X in Spiele und Apps): per Bildschirmtastatur oder Handy, nach Name, zuletzt benutzt, Spielzeit oder Store, nur Favoriten.
- **Artwork** von SteamGridDB (Cover, Hero, Clear Logo; mit eigenem Key) und aus dem Steam-Store, auch für Nicht-Steam-Spiele. Zuerst kommen lokale Quellen (Steams `librarycache`, der Katalog-Cache des Epic Launchers, Paket-Logos von Xbox-Spielen, Ubisoft-Cache, Symbole der Programmdateien), erst dann Steam-CDN und SteamGridDB; fehlt alles, zeichnet CouchHUD selbst ein Banner. **Jedes Bild wird nur einmal geladen und liegt danach im lokalen Artwork-Cache** (`%LOCALAPPDATA%\CouchHudrtwork`, höchstens ca. 750 MB, Bilder aktueller Einträge bleiben erhalten). Die Oberfläche lädt Artwork nie selbst aus dem Internet. Im Expertenmodus lässt sich Online-Artwork abschalten und der Cache leeren.
- **Artwork ändern**: Auf der Detailseite zeigt „Artwork ändern“ alle Treffer von SteamGridDB (mit Key) und dem Steam-CDN für Cover, Banner und Logo. Mit dem Controller wählen, A übernimmt, „Automatisch“ setzt zurück, „Eigenes Bild“ nimmt eine Datei vom PC. Die Wahl gilt pro Eintrag und bleibt nach jedem Scan erhalten.
- **Sammlungen**: Eigene Listen wie „Couch-Koop“ oder „Kinder“. Y auf einer Kachel in Spiele und Apps öffnet das Menü mit „Sammlungen …“ (auf der Detailseite gibt es die Aktion „Sammlungen“; Y bleibt dort Favorit). X in Spiele und Apps zeigt eine Sammlung als Filter an und verwaltet sie (anlegen, umbenennen, löschen, Name per Bildschirmtastatur). „An Home anheften“ legt eine Kachel in die Reihe auf Home.
- **Detailseite** pro Spiel: Beschreibung, Screenshots, Entwickler, Genres (aus dem Steam-Store, in der Sprache der Oberfläche), Spielzeit, zuletzt gespielt, Installationsort, Größe, Deinstallieren.
- **Laufendes Spiel und Spielzeit**: CouchHUD erkennt das laufende Spiel (Steam-AppID bzw. Prozesse im Installationsordner) und zählt die Spielzeit pro Spiel.
- **Spielstände sichern**: Speicherorte, Größe und letzte Änderung aus der Community-Datenbank [Ludusavi-Manifest](https://github.com/mtkennerly/ludusavi-manifest) (PCGamingWiki, Zuordnung per Steam-ID oder Name). Auf der Detailseite *Spielstände* › *Jetzt sichern* (ZIP mit Datum unter `%APPDATA%\CouchHud\Backups\<Spiel>`, Ordner und Anzahl in den Einstellungen) und *Wiederherstellen* (vorher wird der aktuelle Stand automatisch gesichert). Vor dem Deinstallieren wird ein Backup angeboten. Sicherungen mit Fehlern werden als unvollständig markiert und nie automatisch gelöscht, Sicherheits-Backups vor einer Wiederherstellung rotieren getrennt (letzte 3), und scheitert das Zurückschreiben, wird der alte Stand automatisch zurückgespielt. Während das Spiel läuft (auch wenn es außerhalb von CouchHUD gestartet wurde), ist beides gesperrt. Registry-Spielstände werden angezeigt, aber noch nicht gesichert. Die Datenbank (~18 MB) wird höchstens einmal pro Woche von GitHub geladen.

**Bedienung**
- **Guide-Taste wie auf der Konsole**: Der Guide-Button wird global per XInput abgefragt und funktioniert auch, während ein Spiel läuft. Im Spiel antippen holt CouchHUD nach vorne, das laufende Spiel steht als erste Kachel auf Home und läuft weiter (auf Wunsch eingefroren). Halten bringt dich aus einem Spiel oder dem PC-Modus immer zurück. Was ein Antippen tut, stellst du unter *Einstellungen › Controller › Guide-Taste im Spiel* ein (*CouchHUD öffnen*, *Game Bar öffnen*, *Nur Guide halten*). Zwei Controller-Slots, die sich spiegeln (virtuelle Pads von Steam Input oder DS4Windows), zählen nur einmal.
- **Guide-Menü**: Zurück zum Spiel, Spiel beenden (bei Hängern erzwingen), Lautstärke, Lautstärke pro App, Audio-Ausgang, CPU/GPU/RAM-Auslastung, Wetter, Controller-Akku, Benachrichtigungen, Bluetooth, Aufnahmen, laufende Programme, Plugin-Aktionen, Ausschalt-Timer, Energie.
- **Browser** für den Fernseher: Websites mit dem Controller, Stick als Mauszeiger, großer Zoom, Lesezeichen, Adresse per Bildschirmtastatur oder Handy. Eigenes Profil ohne Zugriff auf CouchHUD. Der Browser ist ein Teil des CouchHUD-Fensters (kein eigenes Fenster) mit schlanker Leiste oben: Zurück, Vor, Neu laden, Adresse (antippen öffnet die Bildschirmtastatur), Zoom, Startseite, Schließen. Die Leiste blendet sich nach einigen Sekunden aus, die Ansicht-Taste holt sie zurück; B geht im Verlauf zurück und schließt am Anfang, LB/RB zoomen, Guide führt zu CouchHUD, Videos laufen im Vollbild. **Alle Links in CouchHUD öffnen hier** (Nachrichten, Plugins, Website-Einträge), nie im Standardbrowser von Windows; es gelten die Regeln der Couch-Sperre (mit Sperre fragt CouchHUD nach dem Code), und nur http und https werden geöffnet. Nach dem Schließen bist du wieder dort, wo du herkamst (z.B. im Artikel). **Ausnahme Streaming mit Kopierschutz:** Netflix, Disney+, Prime Video & Co. spielen im eingebauten Browser nicht (WebView2 bringt kein Widevine mit); für diese Website-Einträge steht „Im Edge öffnen“ (Bearbeiten-Menü des Eintrags), bei bekannten Diensten ist es von Anfang an gesetzt. Nur dafür gilt die Einstellung „Edge im Vollbild öffnen“. Bekannt: Google-Anmeldungen sind in eingebetteten Browsern von Google gesperrt.
- **Bildschirmtastatur** (QWERTZ) und **Datei-Browser** per Controller.
- **Maus**: Bei Bewegung erscheint eine große Comic-Maus als Zeiger (mit wackelndem Schwanz), die nach kurzer Zeit wieder verschwindet. Der Controller-Fokus bleibt führend. Für Logins und Dialoge kann der Controller selbst zur Maus werden.
- **Handy als Tastatur und Fernbedienung**: QR-Code am TV scannen, dann am Handy tippen, diktieren oder einfügen und mit Steuerkreuz und Tasten bedienen. Die Verbindung im WLAN ist unverschlüsselt, deshalb tippt das Handy nie in Passwort- und Key-Felder: CouchHUDs eigene Bildschirmtastatur lehnt es bei geheimen Feldern ab, und vor jedem Tippen in ein fremdes Fenster (Launcher-Login) prüft CouchHUD das Feld mit Windows (Passwortfeld oder Feldname wie „Passwort“, „PIN“, „Key“) und weist es ab. Solche Eingaben gehören an den TV. Ehrlich dazu: Windows kann nicht in jedes Feld hineinsehen. Wo das Feld unbekannt bleibt und das Fenster zu einem Launcher oder Browser gehört (Steam, Epic, Chrome, Edge, auch CouchHUDs eigener Browser), tippt das Handy deshalb gar nicht, sondern bittet um die Eingabe am TV; bei Spielen und anderen Programmen ohne Auskunft bleibt Tippen erlaubt. Geprüft wird kurz vor dem Tippen, jeweils nur ein Prüfvorgang gleichzeitig, und nur wenn dasselbe Fenster noch vorn ist. Die Verbindung nimmt nur Geräte aus dem eigenen Netz an (private und Link-Local-Adressen, nie aus dem Internet; Adressen aus 100.64.0.0/10, also Tailscale und andere CGNAT-Netze, lehnt CouchHUD bewusst ab); gekoppelte Handys laufen nach 180 Tagen ohne Benutzung ab und lassen sich einzeln entkoppeln. Die Firewall-Freigabe gilt nur für den benutzten Port und das lokale Subnetz und verschwindet, wenn Handy-Verbindung und MCP im Netzwerk aus sind.
- **Benachrichtigungen** wie auf der PlayStation: Karten oben rechts, Liste im Guide-Menü. Auf Wunsch auch WhatsApp, Discord & Co. aus Windows.

**System**
- **Zwei Integrationsstufen**: *Light* (Standard, der Einsteiger-Modus) ist eine normale Vollbild-App, auf Wunsch mit ausgeblendeter Taskleiste; Steam & Co. bleiben wie gewohnt, CouchHUD greift bei Clients nicht ein (kein Starten, kein Schließen, kein Wegräumen von Fenstern, keine Steam-Einstellungen). *Voll* schaltet Shell-Ersatz, Couch-Sperre, Autologin, Dienste und Autostart-Übernahme frei; Steam & Co. starten mit, laufen leise im Hintergrund und werden beim Beenden geschlossen. Zurück auf Light macht diese Eingriffe rückgängig. Der Knopf *Einstellungen › Konsole › CouchHUD als Konsole einrichten* erledigt das alles in einem geführten Durchlauf (siehe [Einstellungen](#einstellungen)). Unter *Was CouchHUD an Windows ändert* (Transparenz) steht jeder Eingriff mit Ort, Admin-Bedarf (🛡), Zustand und Rückweg.
- **Läuft ohne Administratorrechte.** Was Admin braucht (Sperr-Richtlinien, Gaming-Dienste pausieren, Autologin, Windows Update, Firewall, Updates), erledigt der Windows-Dienst **CouchHud.Service** mit einer festen Befehlsliste, ohne UAC-Abfrage im Alltag. CouchHud.exe startet sich nie mit Administratorrechten. Was Eltern oder ein Administrator bestätigen müssen, fragt CouchHUD per Eltern-Code oder, als Ausweg, über die Benutzerkontensteuerung von Windows (siehe [CouchHUD-Dienst, Codes und Freigaben](#couchhud-dienst-codes-und-freigaben)).
- **Standardkonto für Kinder**: CouchHUD braucht keinen Administrator-Token im Alltag. Der Dienst arbeitet immer nur für das Konto, das ihn aufruft, deshalb kann auch ein normales Standardkonto die Sperr-Richtlinien setzen, ohne dass dafür ein Administrator-Dialog erscheint; lösen lassen sie sich nur mit Freigabe (Code bzw. Windows-Anmeldung) oder als Administrator. Wer Kinder am Controller hat, richtet CouchHUD am besten für ein solches Standardkonto ein (Details und Grenzen unter *Läuft ohne Administratorrechte* und *Couch- und PC-Modus-Profile*).
- **Shell-Replacement** pro Benutzer (`HKCU\...\Winlogon\Shell`). Als Shell läuft CouchHUD **ohne Windows-Explorer**; der Desktop (Explorer) kommt erst im PC-Modus bzw. nach dem Notfall-Modus. Die Autostart-Programme startet CouchHUD selbst (Run-Keys und Autostart-Ordner, deaktivierte Einträge bleiben aus).
- **PC-Modus**: startet den normalen Desktop. Zurück geht's mit gehaltener Guide-Taste, über das Tray-Icon oder per Neustart von CouchHUD.
- **CouchHUD bleibt vorne** (nur mit voller Einbindung, abschaltbar): Der Vordergrund-Wächter minimiert Fenster von Spiele-Clients (Steam inkl. Big Picture, Epic, EA, Ubisoft, GOG, Battle.net, Xbox-App, Playnite) und bekannter Update-Popups, die sich vor CouchHUD drängeln, und die Windows Game Bar gibt die Guide-Taste ab. Auch Steam hält sich zurück: Es startet nicht im Big-Picture-Modus, die Guide-Taste holt Steam nicht nach vorn, kein Big-Picture-Overlay beim Controller und keine Einblendung beim Verbinden eines Controllers (Steams eigene Einstellungen, nichts am Konto; Steam-Overlay und Steams Guide-Kürzel in Spielen bleiben). Steam schreibt seine Dateien selbst, darum ändert CouchHUD sie nur, wenn Steam nicht läuft: Läuft es, beendet CouchHUD es nur zum Anwenden und nur in einem ruhigen Moment (kein Spiel, kein Steam-Fenster vorn, kein Download, keine Anmeldung) sauber, schreibt und startet es leise neu; sonst wartet es. Das Zurücksetzen startet Steam nie neu, es geschieht, sobald Steam nicht läuft. Ausnahmen: ein Spiel läuft, Controller-als-Maus (z.B. Login im Client), Browser, PC-Modus und alles, was CouchHUD selbst gestartet hat. Im PC-Modus, im Light-Modus und beim Beenden ist alles wieder wie vorher (bei Steam nur, was noch von CouchHUD stammt: hat Steam oder du inzwischen etwas geändert, bleibt es).
- **Spiele-Clients im Hintergrund** (nur volle Integration; im Light-Modus greift CouchHUD nicht ein): Steam, Epic, GOG Galaxy, Ubisoft Connect, EA app und Battle.net haben pro Client einen Modus (*Einstellungen › Spiele-Clients*): *Bereithalten* (Standard: startet im Couch-Modus leise mit, damit Updates und Downloads gleich laufen), *Bei Bedarf* (startet mit dem ersten Spiel oder der ersten Aktion, die ihn braucht) oder *Aus*. Beim Beenden von CouchHUD und beim Wechsel in den PC-Modus schließt CouchHUD das Steam, das es selbst gestartet hat (mit `-shutdown`, nie mit Gewalt); Steam, das schon vorher lief oder das du selbst (neu) gestartet hast, bleibt unangetastet. Lädt Steam gerade oder läuft ein Spiel, bleibt es offen (im PC-Modus mit Hinweis). Die anderen Clients lässt CouchHUD vorläufig offen: Für sie gibt es keine Download-Erkennung und keinen Nachweis, dass Schließen nichts abbricht. Steams Einstellungen für die Couch werden vor dem eigenen Start gesetzt und nach dem eigenen Ende zurückgesetzt; nur ein Steam, das schon vor CouchHUD lief, wird dafür in einem ruhigen Moment kurz neu gestartet. Zum Anmelden holt CouchHUD das Client-Fenster nach vorne, der Controller wird zur Maus und das gekoppelte Handy tippt ins Benutzernamen-Feld, wenn Windows es als normales Eingabefeld meldet (sonst bittet es um die Eingabe am TV); das Passwort kommt über die Bildschirmtastatur am TV (Steam bietet dort auch den QR-Login per Steam-App).
- **Couch- und PC-Modus-Profile**: Für jedes Autostart-Programm einstellen, ob es mit CouchHUD startet oder erst im PC-Modus (mit Empfehlung: Spiele-Tools bleiben, Office und Sync warten). Windows-Dienste wie Suche oder Druckwarteschlange pausieren beim Spielen und kommen im PC-Modus zurück, ohne UAC-Abfrage. Das erledigt der CouchHUD-Dienst (CouchHud.Service, LocalSystem, in `C:\Program Files\CouchHud\service`): eine feste Liste von Operationen mit geprüften Parametern, nie „starte dieses Programm“ oder „öffne diesen Pfad“, und immer nur für das eigene Konto des Aufrufers. Pausiert werden nur Dienste von seiner eigenen festen Liste, fortgesetzt nur die, die er selbst angehalten hat. Die geplanten Aufgaben früherer Versionen ersetzt der Dienst; nur die Reserve-Aufgabe „Autologon aus“ bleibt für die Wiederherstellung. Fehlt der Dienst, bleibt die Couch-Sperre zu (fail-closed); „Dienst reparieren“ richtet ihn neu ein (siehe [Notausgang](#notausgang)).
- **Audio**: Lautstärke, Stumm, **Lautstärke pro App** (laufendes Spiel zuerst, im Guide-Menü), Ausgabegerät wechseln (TV, Kopfhörer, Soundbar). Wird ein Headset angeschlossen, wechselt die Ausgabe automatisch.
- **Anzeige**: Auflösung, Bildwiederholrate und HDR, mit Countdown „Einstellung behalten?“ wie auf Konsolen. Dazu ein **Nachtmodus** (wärmeres Bild, auch in Spielen, auf Wunsch automatisch abends).
- **Spielprofile** wie auf dem Steam Deck: pro Spiel Auflösung/Hz, HDR, Audio-Ausgang und Nachtmodus. Gilt nur, solange das Spiel läuft.
- **Downloads**: laufende Steam-Installationen und Updates mit Fortschritt.
- **Bluetooth**: Kopfhörer, Controller und Lautsprecher koppeln und entfernen.
- **WLAN**: Netzwerke suchen, mit Passwort über die Bildschirmtastatur verbinden, trennen, Status mit IP (Einstellungen › Netzwerk). Braucht den Windows-Standortzugriff für die Suche.
- **Energie**: Standby, Neustart, Neustart direkt in den PC-Modus (einmalig), Herunterfahren, Ausschalt-Timer (schickt den PC nach einer Weile in den Standby).
- **Task-Manager** für den TV: laufende Programme mit Speicherverbrauch, hängende beenden.
- **Updates**: Windows Update (suchen und mit einer Bestätigung installieren) und App-Updates über winget, einzeln oder alle. Gesucht wird automatisch im Hintergrund, nie während eines Spiels.
- **CouchHUD aktualisiert sich selbst**: Alle 12 Stunden (und per „Nach Updates suchen“) fragt es bei GitHub nach einer neuen Version, meldet sie in den Benachrichtigungen und zeigt Version und Änderungen im Abschnitt *CouchHUD* der Updates-Seite. „Installieren“ lädt das Setup (nur von github.com über https), prüft die **Signatur der Prüfsummen-Datei** (ECDSA, derselbe Schlüssel wie beim Katalog) und den SHA-256 des Setups und übergibt es dem CouchHUD-Dienst. Der prüft Signatur, Prüfsumme und Version (nur neuer, kein Downgrade) noch einmal selbst und installiert es still, ohne UAC-Abfrage; danach startet CouchHUD von selbst neu, auch als Shell. Ohne gültige Signatur wird nichts installiert, während eines Spiels auch nicht. Abschalten der Suche: Einstellungen › Updates › *Automatisch nach CouchHUD-Updates suchen* (`"selfUpdateCheck": false`).

**Schutz und Notausgang**
- **Couch-Sperre**: Zum Verlassen von CouchHUD (PC-Modus, Beenden, Client-Fenster) braucht es einen Controller-Code (sechs bis acht Tasten) oder die Windows-Anmeldung (Windows Hello, PIN, Passwort). Nach fünf Fehlversuchen wächst die Wartezeit (30 s, 60 s, 120 s … bis 30 Minuten, auch über einen Neustart hinweg). Ein Wächterprozess sperrt Windows, falls CouchHUD beendet wird oder abstürzt, der Task-Manager ist im Couch-Modus gesperrt. Ideal zusammen mit der automatischen Windows-Anmeldung, die CouchHUD einrichtet (das Passwort wird einmal eingegeben, geprüft und von Windows verschlüsselt abgelegt; CouchHUD speichert es nie). Bei geschlossener Sperre startet CouchHUD nur Spiele (Apps und Webseiten brauchen die Entsperrung; eine so gestartete App bleibt *freigegeben bis zur Rückkehr ins HUD*, CouchHUD blendet das ein), hält Spiele-Clients nach der Startphase zurück (auch wenn CouchHUD sie selbst gestartet hat), minimiert klar erkannte Nicht-Spiele (Einstellungen, Explorer, Konsolen, Browser außerhalb des Spiels), schließt Datei- und Druckdialoge und setzt reversible Windows-Richtlinien (keine Systemsteuerung, kein Ausführen, keine Eingabeaufforderung, Eingabehilfe-Kürzel aus; cmd, PowerShell, regedit, mmc und wt lassen sich gesperrt nicht über die Windows-Shell starten). Die Entsperrung gilt nur kurz und **endet jetzt auch bei jedem Spielstart und bei jeder Rückkehr ins HUD, nicht mehr nur mit Jugendschutz**; im Browser wird sie bei Benutzung verlängert und der Browser schließt sich, sobald wieder gesperrt ist. Die Windows-Anmeldung als Sperre verlangt ein Konto mit Kennwort. Der Controller-Code geht auch ohne Kennwort, CouchHUD warnt dann deutlich; die automatische Anmeldung entfällt ohne Kennwort (Windows meldet dann ohnehin an). **Ehrlich gesagt: Die Sperre ist nur so stark wie das Windows-Kennwort des Kontos** (siehe [Haftungsausschluss](DISCLAIMER.md)). Sie hält Kinder und Gäste am Controller fern, keine Technik-Kenner mit Tastatur, Startmedium oder ausgebautem Laufwerk. **Geräteverschlüsselung bzw. BitLocker wird empfohlen** (die Ein-Knopf-Einrichtung weist darauf hin, schaltet sie aber nicht selbst ein).
- **Jugendschutz** (*Einstellungen › Jugendschutz*): Spielzeit-Regeln für Kinder am Controller.
  - *Eltern-Code*: Das ist der Code der Couch-Sperre oder die Windows-Anmeldung. **Den Jugendschutz gibt es nur auf einem Konto mit Kennwort**, auch mit Controller-Code (sonst käme man am Sperrbildschirm und in der Wiederherstellung einfach vorbei). Der Code gilt immer nur für eine einzige Aktion **an einem Eintrag**, mit eigener Abfrage: Er öffnet die Couch-Sperre nicht, und jede Änderung (Einstellung, Verlängern, gesperrtes Spiel freigeben, Code oder Sperre ändern, Spiel/App umstufen, ein Spiel oder einen Spielordner hinzufügen) fragt neu. Eltern-Code und Couch-Code zählen Fehlversuche getrennt. Bei der Windows-Anmeldung zeigt CouchHUD nach dem Entsperren die vorgemerkte Aktion an und wartet auf A; die Anfrage gilt drei Minuten und nur für eine Sperre, die CouchHUD selbst ausgelöst hat.
  - *Regeln*: tägliches Limit (Mo–Fr und Wochenende getrennt), Spielzeitfenster (auch über Mitternacht, z.B. 22 bis 1 Uhr), Limit pro Spiel, Altersgrenze (ab USK 6/12/16/18 sperren, nach dem Steam-Rating; Spiele ohne Altersangabe starten dann nur mit Eltern-Code, abschaltbar), einzelne Spiele oder Apps nur mit Eltern-Code und „Apps zählen mit“.
  - *Zählung*: Gezählt wird die Laufzeit der Spiele, die CouchHUD laufen sieht, auch solche, die ohne CouchHUD gestartet wurden oder schon beim Start liefen (Bibliothekseinträge mit Steam-ID oder Programmordner), nicht im pausierten Zustand. Einträge desselben Spiels teilen Limit und Sperre. Einträge ohne erkennbaren Prozess (Webseiten, Store-Apps ohne Ordner) lassen sich nicht messen und starten deshalb nur mit Eltern-Code (außer Apps, wenn „Apps zählen mit“ aus ist). Während ein Spiel läuft, startet kein zweites, und Spiele-Clients wie Steam bleiben minimiert (ihre Dateidialoge wären ein Weg aus der Couch).
  - *Ablauf*: 15 und 5 Minuten vorher erscheint eine Einblendung über dem Spiel mit der echten Restzeit (bei Exclusive-Vollbild kann sie fehlen, dann bleibt die Benachrichtigung in CouchHUD). Bei Ablauf wird das Spiel angehalten und CouchHUD kommt mit *Spielzeit vorbei* nach vorn: *Spiel speichern und beenden* oder *Mit Eltern-Code verlängern* (+15/+30/+60 Minuten, gilt für Tageslimit, Fenster und Spiel, bei einem Fenster über Mitternacht verlängert die Zugabe vor Mitternacht nur dieses Fenster, nicht das Limit des Folgetags). Ohne Entscheidung schließt CouchHUD das Spiel nach 60 Sekunden (Fenster schließen, nach 2 Minuten beenden). Verlängern die Eltern, während das Spiel schon geschlossen wird, bricht das Schließen ab. Lässt es sich nicht beenden (erhöhter Prozess), sperrt CouchHUD Windows (nur solange die Zeit wirklich vorbei ist) und fragt weiter nach. Außerhalb des Fensters oder nach dem Tageslimit startet nichts, mit Hinweis. Oben im Header und im Guide-Menü steht die Restzeit.
  - *Manipulationsschutz*: Die Zeit steht pro Tag in `playtime.json` (alle 30 Sekunden gesichert, mit HMAC gegen Bearbeiten; eine veränderte oder gelöschte Datei lässt den Tag als verbraucht gelten, bis die Eltern den Zähler zurücksetzen). Die Zählung folgt der Laufzeituhr und der beim Einschalten gespeicherten Zeitzone, nicht der Systemuhr: Uhr oder Zeitzone zu verstellen bringt keine Spielzeit, auch nicht über einen Neustart. Ein Vorsprung der Systemuhr wird nur nach Bestätigung durch das HTTPS-Datum von www.microsoft.com oder per Eltern-Code übernommen, auch beim Start (offline bleibt es beim zuletzt gesicherten Tag). Entsperrung, Eltern-Freigabe und Wartezeit nach Fehlversuchen folgen ebenfalls der Laufzeituhr. `playtime.json` wird durchgeschrieben und mit Sicherungskopie ersetzt.
  - *Voraussetzung und Grenzen*: Wirkt nur als Shell mit Couch-Sperre; sonst (Light-Modus, Sperre aus) gibt es nur Hinweise, im PC-Modus ruht er. Der Schutz gilt gegen Kinder am Controller, nicht gegen Technik-Kenner: Wer Dateizugriff auf den Benutzerordner hat, kann die Einstellungen ändern, und Spiele ohne erkennbaren Programmordner (nur URL-Start) werden nicht gezählt, sondern brauchen den Eltern-Code. Läuft ein Spiel, das nicht über CouchHUD gestartet wurde (Steam, Launcher), prüft CouchHUD es nachträglich gegen Sperrliste, Altersgrenze und Zeit und behandelt es bei einem Verstoß wie abgelaufene Zeit. Prozesse, die ein Spiel (oder sein Launcher) startet, zählen mit.
- **Notfall-Modus (Notbremse)**: Strg + Umschalt oder LB + RB + View beim Start etwa 1,5 Sekunden halten (oder drei unsaubere Beendigungen in Folge innerhalb weniger Stunden). Das geht in zwei Schritten, weil es jeder am Controller auslösen könnte: Zuerst sperrt CouchHUD nur Windows und wartet. **Erst nach der Windows-Anmeldung mit dem Kennwort des Kontos** entfernt es Shell-Eintrag und (von CouchHUD eingerichteten) Autologin, und der normale Desktop kommt. Wer stattdessen am Sperrbildschirm „Neu starten“ wählt, landet wieder in der Couch. Ohne Couch-Sperre entfällt der erste Schritt. Lässt sich Windows für den ersten Schritt auch nach mehreren Versuchen nicht sperren, ändert CouchHUD nichts, zählt die unsauberen Beendigungen wieder mit (der nächste Start versucht es erneut), zeigt eine Meldung und startet die Couch wie gewohnt hinter ihrer Sperre: Ein Kind am Controller soll nicht ohne Kennwort auf den Desktop kommen. Bleibt ein Autologin bestehen (z.B. per netplwiz eingerichtet) und ist CouchHUD nicht die Shell, sperrt ein Run-Eintrag des Kontos Windows direkt nach einer **automatischen** Anmeldung (erkannt daran, dass die Sitzung innerhalb weniger Sekunden angemeldet wurde); wer sich von Hand anmeldet, wird nicht noch einmal gesperrt. Der Eintrag folgt jeder Änderung von Shell, Autologin und Couch-Sperre und verschwindet bei der Deinstallation.
- **Robust**: Stürzt die UI ab, wird sie neu aufgebaut. Startet CouchHUD gar nicht, holt es den Desktop zurück. Logs liegen unter `%LOCALAPPDATA%\CouchHud\logs`.

**Extras**
- **Wetter** im Header (Open-Meteo, ohne Account), mit Vorhersage im Guide-Menü.
- **Aufnahmen**: Screenshot und „Clip speichern“ direkt aus dem Guide-Menü (über die Game Bar), Galerie für Screenshots und Clips aus Game Bar, Steam und NVIDIA.
- **News**: RSS/Atom-Reader mit großer Schrift für den TV. WinFuture und Caschys Blog sind vorkonfiguriert, weitere Feeds fügst du in den Einstellungen hinzu.
- **Home Assistant**: Szenen, Lichter, Schalter, Mediaplayer und Sensoren als Live-Karten. Einzelne Einträge kannst du ins Guide-Menü heften.
- **CaseHUD**: Die LCDs in Lüftern und Pumpen vom Sofa aus steuern (Einstellungen › CaseHUD). CouchHUD installiert [CaseHUD](https://github.com/kewe87/CaseHUD) auf Wunsch mit, startet es und steuert es über dessen lokale API: Displays als Kacheln mit Live-Vorschau, Design pro Display aus einem Raster (passend zur Auflösung, gefiltert nach Set und Theme), Helligkeit, Drehung, Modus, ein Set für alle Displays, Szenen, Szenenwechsel, Community-Designs, AWTRIX, „Am PC öffnen“ für den Design-Editor. Im Guide-Menü: Displays aus/an und nächste Szene. Installiert und aktualisiert wird nur aus signierten Releases; sind die Downloads gerade nicht erreichbar, steht „Downloads derzeit nicht verfügbar“ und *Erneut prüfen* fragt von Hand nach. **CaseHUD-Schnittstelle:** CaseHUD liefert seine API ausgeschaltet aus. Ist sie aus, zeigen die CaseHUD-Seite, die Beleuchtung, das Guide-Menü, der Einrichtungsassistent und „Was CouchHUD ändert“ den Hinweis *In CaseHUD unter Einstellungen → Schnittstellen die API einschalten*; *Schnittstelle einschalten* erledigt das nach Rückfrage direkt (mit entsperrter Couch), *Am PC öffnen* zeigt den Schalter im CaseHUD-Fenster. Bei Vollintegration bietet CouchHUD auf der Installationsseite und im Assistenten an, die Schnittstelle für dein Konto gleich mit einzuschalten (Experten-Einstellung *Schnittstelle nach der Installation einschalten*); auf der Seite des laufenden CaseHUD lässt sie sich wieder ausschalten. Mehr: [CASEHUD.md](CASEHUD.md)
- **KI-Assistent** mit eigenem API-Key (Anthropic oder OpenAI): „Was soll ich heute spielen?“, „Starte das Spiel, das ich zuletzt gespielt habe“, „Mach das Licht aus“, „Wechsel auf den Cyberpunk-Skin“. Jede Änderung musst du am TV mit A bestätigen.
- **MCP-Server** (optional, Standard aus): Andere KI-Programme, z.B. am Laptop, steuern CouchHUD über das Model Context Protocol, mit denselben Werkzeugen wie der Assistent.
- **Plugins und Skins** aus der Community, direkt als `.zip` oder aus dem **Katalog** installierbar, mit Update-Hinweis. Mitgeliefert: **Wand** (früher WeMod) für Cheats und Mods. Der Standard-Katalog [`kewe87/CouchHUD-Catalog`](https://github.com/kewe87/CouchHUD-Catalog) enthält echte Einträge (Wand, Synthwave) mit signiertem Index und Prüfsummen.

## Einstellungen

Alles lässt sich in den **Einstellungen** per Controller konfigurieren. Die `config.json` musst du nicht anfassen. Die Einstellungen haben zwei Stufen:

- **Einfach** (Standard): nur das Alltägliche (Bibliothek, Darstellung, Anzeige, Audio, Controller, Handy, News, Wetter & Smart Home, Benachrichtigungen, Updates, Energie, Jugendschutz, Info) und ganz oben der Knopf **Konsole**: *CouchHUD als Konsole einrichten* macht aus dem PC mit einem Durchlauf eine Konsole (volle Integration, Autostart-Profile, pausierte Dienste, Guide-Taste für CouchHUD, Couch-Sperre mit Controller-Code, automatische Anmeldung, Shell). Vorher zeigt eine Übersicht, was passiert, nachher steht da, was geklappt hat und was übersprungen wurde (mit Grund). Erneut ausführen repariert. *Zurück zum normalen Windows* macht alles rückgängig. Am Ende bietet CouchHUD den Neustart zum Ausprobieren an.
- **Expertenmodus** (*Einstellungen › Expertenmodus*): zeigt zusätzlich alle Feineinstellungen, in den Kategorien als eigener Abschnitt „Experte“ (z.B. Bildwiederholrate, HDR-Effekte, Klangset, Haltezeit der Guide-Taste) und als eigene Kategorien unter der Trennlinie (Windows-Integration, Autostart & Dienste, Netzwerk mit der Firewall-Freigabe, KI & MCP, Plugins & Skins). Jede Einstellung gibt es nur an einer Stelle. Aktionen wie Bluetooth-Geräte, Benachrichtigungen ansehen, Aufnahmen, laufende Programme und Energie-Schnellzugriffe liegen im Guide-Menü.

## Steuerung

| Controller | Tastatur | Aktion |
|---|---|---|
| D-Pad / linker Stick | Pfeiltasten | Navigation |
| A | Enter / Leertaste | Öffnen / Auswählen |
| B | Esc / Backspace | Zurück |
| X | X | Kontextaktion (z.B. News aktualisieren, Updates suchen; in Spiele und Apps: suchen, sortieren, filtern, Sammlungen) |
| Y | F / Y | Auf Home und Detailseite: Favorit an/aus. In Spiele und Apps: Menü zur Kachel (Favorit, Sammlungen …, Details). Im Smart-Home-Tab: ins Guide-Menü heften |
| LB / RB | Q / E oder Bild↑ / Bild↓ | Tab wechseln |
| Start (≡) | M | Energie-Menü |
| Guide-Taste (mittlere Taste des Pads) | Pos1 (Home) | Guide-Menü. Im Spiel antippen holt CouchHUD nach vorne (einstellbar). **Halten** bringt aus einem Spiel oder dem PC-Modus immer zurück |
| Back + Start | – | Alternative zur Guide-Taste |
| LB + RB + View beim Start ~1,5 s | Strg + Umschalt beim Start ~1,5 s | Notfall-Modus: danach normaler Desktop statt CouchHUD |

Bildschirmtastatur: A tippt, X löscht, Y Leerzeichen, LB Umschalten, RB Sonderzeichen, Start (≡) fertig, B abbrechen. Mit einer echten Tastatur schreibst du direkt hinein; Enter beendet die Eingabe, Esc bricht ab. Steuerst du die Tastatur mit Pfeilen (Fernbedienung), drückt Enter die markierte Taste und **Strg + Enter** beendet die Eingabe.

### Controller weg?

Akku leer, Bluetooth aus Versehen abgeschaltet oder der Empfänger abgezogen: CouchHUD lässt sich immer auch mit **Tastatur** und **Maus** (oder Touch) bedienen, auch die Code-Abfrage der Couch-Sperre und den Eltern-Code.

- **Code ohne Controller:** Unter den Punkten der Code-Abfrage stehen die zehn Code-Tasten (A, B, X, Y, LB, RB, ↑ ↓ ← →) zum Anklicken. Auf der Tastatur: **Enter = A, Esc oder Rücktaste = B, X, Y, Q = LB, E = RB, Pfeiltasten = Steuerkreuz**; **M** bricht ab (Esc ist in der Code-Abfrage die Taste B, nicht „Abbrechen“). Es ist derselbe Code, der CouchHUD-Dienst prüft ihn genauso und zählt Fehlversuche genauso (nach 5 falschen wartet jeder weitere Versuch länger). Ist kein Controller verbunden, sagt die Code-Abfrage das und zeigt, wie es um Bluetooth steht.
- **Bluetooth schaltet sich wieder ein:** Ist ein Bluetooth-Controller gekoppelt und kein anderer Controller verbunden, schaltet CouchHUD Bluetooth beim Start (also auch nach einem Neustart des PCs) und sobald es ausgeht wieder ein. Wurde gerade Tastatur oder Maus benutzt, kommt erst ein Countdown von 10 Sekunden mit *Jetzt einschalten* und *Bluetooth aus lassen*; B oder ein Klick daneben blendet ihn nur aus. Läuft gerade eine Code-Abfrage oder die Zeit-vorbei-Seite, erscheint der Countdown nicht darüber (nur als Hinweis), sondern erst danach. *Aus lassen* gilt, bis Bluetooth wieder an ist oder CouchHUD neu startet. Im **Flugmodus** (alle Funkverbindungen aus) gilt dasselbe auch beim Start: mit Tastatur oder Maus erst der Countdown, ohne jede andere Eingabe schaltet CouchHUD Bluetooth trotzdem ein, damit niemand ausgesperrt ist. CouchHUD schaltet dabei nur das vorhandene Bluetooth ein: es koppelt keine neuen Geräte und macht den PC nicht sichtbar. Ist Bluetooth im Geräte-Manager, per Richtlinie oder Hardware-Schalter deaktiviert oder fehlt der Adapter, steht das nur im Log. Schlägt das Einschalten fehl, versucht CouchHUD es mit wachsendem Abstand (30 s bis 15 min) und hört nach 6 Fehlschlägen bis zum nächsten Start auf; höchstens 3-mal in 10 Minuten. Schaltet etwas anderes Bluetooth immer wieder aus, hört CouchHUD nach zwei vollen 10-Minuten-Fenstern bis zum nächsten Start auf. Abschalten: *Einstellungen › Controller › Bluetooth für Controller automatisch einschalten* (Expertenmodus, braucht bei aktiver Couch-Sperre den Code).
- **Bluetooth in CouchHUD ausschalten:** Ist dein Controller über Bluetooth verbunden, fragt CouchHUD vorher nach. Ist ein anderer Controller verbunden oder wurde gerade Tastatur oder Maus benutzt, bleibt Bluetooth danach aus (wie *Bluetooth aus lassen*); sonst kommt es nach kurzer Zeit von selbst wieder.
- **Wenn gar nichts mehr geht:** `Strg + Alt + Entf` drücken, dann **Abmelden** oder unten rechts **Neu starten**. Nach dem Neustart schaltet CouchHUD Bluetooth von selbst wieder ein (siehe oben), und der Controller verbindet sich wieder, sobald du ihn mit der Guide-Taste einschaltest.

## Setup

Voraussetzungen: Windows 10/11 (WebView2-Runtime ist ab Windows 11 dabei), ein XInput-Controller (Xbox-Pads, oder PlayStation-Pads via Steam Input/DS4Windows).

**Setup (empfohlen):** Unter [Releases](https://github.com/kewe87/CouchHUD/releases) bzw. bei jedem Build unter *Actions → build → Artifacts → CouchHud-Setup* liegt `CouchHud-Setup-<Version>.exe`. Das Setup (Deutsch und Englisch) installiert nach `C:\Program Files\CouchHud` (der geschützte Ordner, den die Admin-Funktionen voraussetzen), bringt bei Bedarf die WebView2-Runtime mit und legt Startmenü-Eintrag, optional Desktop-Symbol und Autostart an. An der Windows-Anmeldung ändert es nichts: CouchHUD startet in der Stufe *Light*. Die Deinstallation setzt alles zurück, was CouchHUD an Windows geändert hat, und fragt, ob die Einstellungen gelöscht werden sollen. Das Setup ist (noch) nicht mit Authenticode signiert, SmartScreen warnt deshalb beim ersten Start („Weitere Informationen › Trotzdem ausführen“). Die Update-Prüfung von CouchHUD selbst ist davon unabhängig signiert (siehe Self-Update).

**Erster Start:** Ein Einrichtungsassistent stellt ein paar Fragen, eine pro Bildschirm: Sprache, Haftungsausschluss und Datenschutz bestätigen, *Light* oder *Voll*, Skin mit Live-Vorschau und Mauszeiger, Ton, bei *Voll* die Spiele-Clients (*Bereithalten* oder *Bei Bedarf*). Die Wahl *Voll* wird nur vorgemerkt: Erst *Los geht’s* in der Zusammenfassung startet die Konsolen-Einrichtung aus *Einstellungen › Konsole* (mit Administrator-Bestätigung, Controller-Code und Neustart-Angebot); bleibt sie unvollständig, sagt CouchHUD es danach kurz. Während des Assistenten und des ersten Hinweises erscheinen keine Benachrichtigungskarten, nur die Glocke zählt. Jeder Schritt lässt sich mit Y überspringen, ≡ beendet den Assistenten; alles bleibt in den Einstellungen änderbar, der Assistent selbst stellt nichts ein, was es dort nicht gibt. Erneut starten: *Einstellungen › Info › Einrichtungsassistent erneut starten* (mit aktiver Couch-Sperre nur nach dem Code). Ältere Installationen bekommen ihn einmal angeboten, nicht aufgezwungen.

**Ohne Setup:** Das ZIP `CouchHud-win-x64` aus den Artifacts nach `C:\Program Files\CouchHud` entpacken und `CouchHud.exe` starten. Der Build bringt .NET mit (self-contained), es braucht nur die WebView2-Runtime, die bei Windows 11 dabei ist.

**Release erstellen** (für Maintainer, Details in [CONTRIBUTING.md](../CONTRIBUTING.md#release)):

1. Version in `Directory.Build.props` setzen, Tag pushen: `git tag v0.4.0 && git push origin v0.4.0`. Die CI baut das Setup mit dieser Version und hängt `CouchHud-Setup-0.4.0.exe` und `CouchHud-Setup-0.4.0.exe.sha256` an ein GitHub-Release. Ein Tag-Build bricht ab, wenn das Tag nicht zur Version in `Directory.Build.props` passt.
2. Lokal prüfen und signieren: `.\scripts\sign-release.ps1 -Tag v0.4.0` (PowerShell 7, GitHub CLI `gh` angemeldet). Das Skript prüft Commit, Version und Hash des Setups, die Build-Attestierung der CI und signiert erst dann die `.sha256` (Kontext `couchhud-release-v1`) mit dem privaten Schlüssel aus dem Benutzerprofil; danach lädt es `.sha256.sig` hoch.

Erst mit Schritt 2 bieten installierte CouchHUDs das Update an. Die Release-Notes (aus den Pull Requests generiert, nach dem Tag editierbar) erscheinen gekürzt auf dem TV.

**Selbst bauen** (.NET 10 SDK, für das Setup zusätzlich [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
dotnet build CouchHud.slnx
dotnet test CouchHud.slnx
dotnet run --project src/CouchHud.Shell -- --dev   # Fenstermodus mit DevTools
dotnet publish src/CouchHud.Shell -c Release -r win-x64 --self-contained true -o publish
iscc /DAppVersion=0.4.0 installer\CouchHud.iss    # optional: Setup nach installer\output
```

Für das Konsolen-Erlebnis danach unter *Einstellungen › Konsole* „CouchHUD als Konsole einrichten“ wählen. Beim nächsten Start erscheint CouchHUD statt des Desktops. Wer lieber einzeln einschaltet, aktiviert den Expertenmodus und findet alles unter *Windows-Integration*.

### Wichtige Windows-Einstellungen

- **Xbox Game Bar und Guide-Taste**: Standardmäßig schnappt sich die Game Bar die Guide-Taste. Mit „CouchHUD als Konsole einrichten“ (oder *Einstellungen › Controller › Guide-Taste im Spiel › CouchHUD öffnen*) gibt die Game Bar die Taste ab, solange „CouchHUD bleibt vorne“ an ist (CouchHUD sichert den alten Wert und stellt ihn im PC-Modus, im Light-Modus und beim Beenden wieder her). Von Hand: Unter *Einstellungen → Spielen → Xbox Game Bar* die Option „Xbox Game Bar mit dieser Taste auf einem Controller öffnen“ deaktivieren. Alternativ funktioniert Back + Start.
- **Auto-Login**: Für echtes Konsolen-Feeling über den Konsolen-Knopf (oder im Expertenmodus unter *Windows-Integration › Automatische Anmeldung einrichten*). Du gibst dein Windows-Passwort einmal an der Bildschirmtastatur am TV ein (nie über das Handy), CouchHUD prüft es mit Windows und lässt es von Windows verschlüsselt als LSA-Secret ablegen (wie bei Sysinternals Autologon). CouchHUD speichert das Passwort nicht, weder in der Konfiguration noch im Log. Wer den PC einschaltet, landet in CouchHUD, Verlassen schützt die Couch-Sperre: bitte zusammen einrichten, und das Laufwerk mit BitLocker/Geräteverschlüsselung schützen. Beim Deinstallieren wird die Anmeldung wieder entfernt, wenn CouchHUD sie eingerichtet hat. `netplwiz` bleibt als Alternative für Experten (dort tippt das Handy nicht mit, das Passwort kommt über die Bildschirmtastatur).

### Notausgang

Falls mal etwas schiefgeht und nur ein schwarzer Bildschirm kommt:
1. Beim Start **Strg + Umschalt** (oder **LB + RB + View**) etwa 1,5 Sekunden halten: Windows sperrt, und **nach der Anmeldung mit dem Windows-Kennwort** kommt der normale Desktop. Das Kennwort kennt der Inhaber (es ist das Konto-Kennwort, nicht der Couch-Code). Ein Konto ohne Kennwort braucht dafür nur einen Klick am Sperrbildschirm, darum gibt es den Jugendschutz nur mit Kennwort.
2. Sonst: `Strg + Alt + Entf` → **Task-Manager** → *Neuen Task ausführen* → `explorer.exe`
3. Shell-Eintrag entfernen: `reg delete "HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon" /v Shell /f`

**Dienst reparieren**: Läuft der CouchHUD-Dienst nicht mehr, richtet „Dienst reparieren“ ihn mit der festen, gehärteten Konfiguration neu ein (nur Windows-Programme: `sc.exe`, `icacls.exe`, `reg.exe`). Vor dem Bestätigen in der UAC-Abfrage *Weitere Details anzeigen* öffnen: Programm muss `C:\Windows\System32\cmd.exe` sein, Befehlszeile `/d /c ""C:\Program Files\CouchHud\repair-service.cmd""`. Steht dort etwas anderes, abbrechen.

### CouchHUD-Dienst, Codes und Freigaben

Alles, was Administratorrechte braucht, erledigt der **CouchHUD-Dienst** (`CouchHud.Service`, läuft als SYSTEM, liegt in `C:\Program Files\CouchHud\service`). CouchHUD selbst läuft nie mit Administratorrechten und spricht den Dienst nur über eine lokale Pipe an; vor jedem Kontakt prüft es, dass die Pipe wirklich dem Dienst gehört. Der Dienst handelt immer nur für das Konto, das ihn aufruft (aus dessen Windows-Anmeldung, nie aus der Anfrage), und nur mit einer festen Liste von Aktionen.

- **Codes und Sperrmodus liegen im Dienst.** Couch-Code und (mit Jugendschutz) Eltern-Code prüft der Dienst selbst, mit eigenem Zähler für Fehlversuche je Code: nach fünf falschen Codes wartet jeder weitere Versuch, erst 30 Sekunden, dann jeweils doppelt so lange, höchstens 15 Minuten. Der Zähler überlebt Neustarts von Dienst und PC. Der Dienst kennt auch den Sperrmodus des Kontos (aus, Windows-Anmeldung, Code): einschalten und verschärfen geht jederzeit, abschwächen oder ausschalten nur mit dem Nachweis des bisherigen Modus (Code bzw. Entsperren) oder mit Eltern bzw. Administrator. Der erste Code ist nur frei, solange keine Sperre an ist, und überlebt das Einschalten einer Sperre nur, wenn er dabei mitgesetzt wird (CouchHUD fragt beim Einschalten der Code-Sperre nach dem Code); der Code-Modus ohne Code bleibt zu. Ausschalten nimmt den Code auch aus dem Dienst, der Wechsel zur Windows-Anmeldung ebenso (dort zählt nur das Entsperren; CouchHUD sagt das vor dem Wechsel). Zurück zum Code legt man gleich einen neuen Code fest und bestätigt ihn einmal mit der Windows-Anmeldung (mit Jugendschutz mit dem Eltern-Code), ohne Administrator; „Jugendschutz aus“ nimmt den Eltern-Code heraus. Die Konfiguration im Benutzerordner hält keinen Code-Hash mehr. Die Daten des Dienstes (`service\data`) dürfen nur SYSTEM und Administratoren lesen. Unter *Einstellungen › Konsole* steht, welche Codes der Dienst für dieses Konto hält.
- **Update von 0.4.0 mit eingeschalteter Couch-Sperre:** Die Sperre bleibt an. Beim ersten Öffnen nach dem Update bestätigt ein Administrator sie einmal über die Windows-Abfrage (UAC; Programm `sc.exe`). CouchHUD sagt vorher, was bestätigt wird und warum: bei der Codesperre „der eben eingegebene Code“, der dann der einzige Code der Sperre ist, bei der Windows-Anmeldung der „Windows-Modus“. Grund: Codes lagen in 0.4.0 in einer Datei, die jedes Programm des Kontos ändern konnte. Alle anderen bisherigen Codes, auch ein Eltern-Code, gelten danach nicht mehr; einen Eltern-Code legt man in den Einstellungen neu fest. Passt der eingegebene Code nicht zum gespeicherten (oder ist keiner gespeichert), fragt CouchHUD zuerst, ob ein Administrator ihn als neuen Code bestätigen soll; UAC kommt erst nach „Als Administrator bestätigen“. Wurde die Sperre mit dem alten Code von 0.4.0 bestätigt, erinnert CouchHUD daran, dass dieser Code lesbar in der Konfiguration lag: Lege danach in den Einstellungen einen neuen Code fest. Bis dahin geben weder der Code noch die Windows-Anmeldung noch ein Eltern-Code die Sperre frei, nur ein Administrator; der Sperrmodus lässt sich bis dahin nur so ändern, einzelne Codes gar nicht. Danach öffnet der Code wie gewohnt.
- **Freigaben.** Ein richtiger Couch-Code ergibt eine Freigabe für 2 Minuten (das Entsperr-Fenster), gebunden an das Konto, die Windows-Anmeldung und die Windows-Sitzung. Sie endet früher, wenn die Sitzung gesperrt, getrennt oder abgemeldet wird, und zählt nicht, solange die Sitzung gesperrt ist. Damit gibt der Dienst die Sperr-Richtlinien frei, öffnet die Firewall-Freigabe oder behält bei einem Update die Sitzung bei. Im Sperrmodus „Windows-Anmeldung“ zählt stattdessen das Entsperren der eigenen Sitzung, das der Dienst selbst gesehen hat; nie im Code-Modus, nie bei einem Konto ohne Kennwort und nie eine bloße Anmeldung.
- **Was Eltern oder ein Administrator bestätigen müssen**: die automatische Anmeldung, den Eltern-Code ändern oder entfernen, den Couch-Code neben einem Eltern-Code ändern oder entfernen, die Sperre abschwächen, solange es einen Eltern-Code gibt, eine Sperre ohne Code öffnen. CouchHUD fragt dafür den **Eltern-Code**; die Freigabe gilt dann nur für genau diese Aktion, einmal, für 30 Sekunden. Geht das nicht (kein Eltern-Code, zu viele Fehlversuche), bestätigt ein **Administrator** dieses PCs über die **Benutzerkontensteuerung (UAC)** von Windows: CouchHUD sagt zuerst, was bestätigt wird; dann zeigt Windows' eigene UAC-Abfrage auf dem geschützten Desktop das Programm `sc.exe` (Dienststeuerung) mit `control CouchHud <Zahl>`. Das Kennwort des Administrators tippt man nur in diese Windows-Abfrage, CouchHUD sieht es nie. Die Freigabe bekommt das Konto an der Couch, nicht der Administrator, nur für die genannte Aktion, einmal; die Anfrage verfällt nach 60 Sekunden. Setzen Eltern oder ein Administrator einen einzelnen Code neu, verschwindet der jeweils andere (ein untergeschobener Code überlebt das nicht); CouchHUD setzt mit Jugendschutz immer beide.
- **Eltern-Code.** Mit Jugendschutz ist der Eltern-Code der Couch-Code. Der Dienst übernimmt ihn nur zusammen mit einem neuen Code (beide in einem Schritt) oder mit Bestätigung durch Eltern bzw. Administrator; der Couch-Code allein macht nie einen Eltern-Code. Beim Einschalten des Jugendschutzes an der entsperrten Couch fragt CouchHUD deshalb gleich nach dem Code und speichert ihn als Couch- und Eltern-Code; ein Administrator bestätigt das einmal per UAC. Wird der Jugendschutz an der gesperrten Couch eingeschaltet, setzt CouchHUD bewusst keinen Code (die gesperrte Couch nimmt keinen neuen Code an); dann den Code an der entsperrten Couch neu festlegen.
- **Übernahme beim Update.** Beim ersten Kontakt übernimmt der Dienst den Code-Hash und den Sperrmodus aus der Konfiguration, einmal je Konto. **Ehrliche Grenze:** Der Dienst kann in diesem Moment nicht wissen, ob ein Programm des Benutzers die Konfiguration vorher geändert hat. Er nimmt nur einen Hash im Format von CouchHUD an, nie über einen Code, den er schon kennt, und einen Modus nie schwächer als den, den er schon kennt; danach ist er die maßgebliche Quelle. Kennt der Dienst Codes, die die Einstellungen nicht kennen, weist der Einrichtungsassistent darauf hin.

**Ehrliche Grenzen des Dienstes**
- Das Kennwort eines Administrators kommt bei CouchHUD nicht vorbei: Windows' UAC-Abfrage nimmt es entgegen. **Ehrliche Grenze:** Die Abfrage zeigt nur `sc.exe control CouchHud <Zahl>`, nicht die Aktion. Je Sitzung ist nur eine Anfrage offen; ein Programm, das schon als dein Konto läuft, könnte aber vor CouchHUD eine eigene stellen (andere Aktion, gleiches Konto), und eine Bestätigung bindet die Aktion, nicht ihre Einzelheiten. Darum nur bestätigen, wenn man die Aktion gerade selbst angestoßen hat und CouchHUD sie eben angekündigt hat. Das Kennwort für die automatische Anmeldung und die Code-Tasten kommen dagegen als .NET-Zeichenketten an, die sich nicht löschen lassen und bis zur Speicherbereinigung im Speicher bleiben. Geloggt wird nichts davon.
- Die **Windows-Anmeldung** gibt auch ohne laufenden Dienst den Weg frei (Notausgang oben): Wer das Windows-Kennwort des Kontos kennt, kommt an den Desktop. Eine Anmeldung ergibt aber keine Freigabe im Dienst: Was ein abgestürzter Lauf an Einschränkungen hinterlassen hat, gibt der Dienst danach nur mit Code, Entsperren im Windows-Modus oder Administrator (UAC) frei.
- Ein Programm, das schon als dein Konto läuft, kann den Controller mitlesen und die Freigaben aus dem Speicher von CouchHUD holen. Der Dienst hebt die Hürde gegen Kinder am Controller und gegen die UAC-Grenze, nicht gegen Schadsoftware im eigenen Konto.
- Die Prüfung „Konto ohne Kennwort“ meldet sich einmal je Konto und Dienststart als fehlgeschlagene Anmeldung im Sicherheitsprotokoll.
- **„Dienst reparieren“** fragt per UAC nach einem Administrator und startet nur `cmd.exe` mit dem festen Reparatur-Skript aus dem Programmordner (siehe oben, Details in der UAC-Abfrage prüfen).

## Skins

Unter *Einstellungen › Darstellung › Skin* umschalten, mit Live-Vorschau beim Durchblättern (B bricht ab).

![Die fünf eingebauten Skins](skins.png)

Ein Skin ist ein Stylesheet plus optionale Button-Glyphen und Sounds. Eigene Skins kommen nach `%APPDATA%\CouchHud\skins\<name>\`:

```
skin.json  { "name": "Mein Skin", "description": "…", "glyphs": { "A": "✕", "LB": "L1" } }
skin.css   :root[data-skin="user:<name>"] { --accent: hotpink; --tile-radius: 0; … }
```

Die wichtigsten Variablen: `--bg`, `--fg`, `--muted`, `--accent`, `--accent-glow`, `--panel`, `--font`, `--tile-radius`, `--shelf-tile`, `--focus-scale`. Für alles andere kannst du die Selektoren aus `ui/styles.css` überschreiben, siehe die eingebauten Skins in `ui/skins/`. Vollständige Anleitung: [SKINS.md](SKINS.md).

## Konfiguration

`%APPDATA%\CouchHud\config.json` wird beim ersten Start angelegt. Alles darin lässt sich auch in den Einstellungen ändern, die Datei ist nur der Speicher. Secrets (API-Keys, Tokens) liegen dort mit DPAPI verschlüsselt. **Ist die Datei beschädigt oder nicht lesbar**, läuft CouchHUD gesperrt (Windows-Anmeldung als Couch-Sperre, Jugendschutz streng: kein Spiel startet) und schreibt nichts in die Datei, bis sie wieder brauchbar ist. Nicht lesbar (z.B. von einem anderen Programm festgehalten): CouchHUD versucht es alle 15 Sekunden erneut. Beschädigt: **Ausweg ist, `config.json` zu reparieren oder zu löschen** (dann beginnt CouchHUD mit Standardwerten; Sperre und Jugendschutz sind danach aus). Daneben liegt eine Kopie `config.json.<Zeit>.bad` (gleicher Inhalt nur einmal, höchstens fünf), die nur eine Kopie ist: bearbeite oder lösche besser die eigentliche `config.json`. Die wenigen Notfall-Felder (Hinweis „Shell entfernt“, Rückkehr in die Couch) hält CouchHUD in dieser Zeit in `config.json.recovery` und führt sie nach der Reparatur zusammen. Ein Auszug:

```json
{
  "killExplorerOnReturn": false,
  "runStartupApps": true,
  "sounds": true,
  "audioMode": "ui",
  "musicVolume": 35,
  "guideTapInGame": true,
  "pauseGameInMenu": false,
  "screensaverMinutes": 5,
  "screensaverStyle": "art",
  "displayOffMinutes": 0,
  "pixelShift": true,
  "reduceMotion": false,
  "language": "auto",
  "integrationMode": "light",
  "closeExplorerInLight": false,
  "browserZoom": 150,
  "couchLock": "off",
  "blockTaskManager": true,
  "returnHoldMs": 1000,
  "skin": "nova",
  "steamGridDbApiKey": "",
  "apps": [
    { "name": "YouTube", "type": "app", "url": "https://www.youtube.com/tv" },
    { "name": "Kodi", "type": "app", "path": "C:\\Program Files\\Kodi\\kodi.exe", "image": "C:\\Bilder\\kodi.png" },
    { "name": "Retro-Emulator", "type": "game", "path": "D:\\RetroArch\\retroarch.exe", "args": ["-f"] }
  ]
}
```

- `killExplorerOnReturn`: Beim Zurückwechseln aus dem PC-Modus `explorer.exe` beenden (sauberer, schließt aber offene Explorer-Fenster).
- `runStartupApps`: Autostart-Einträge beim Boot ausführen, wenn CouchHUD die Shell ist.
- `language`: `auto`, `de` oder `en`.
- `steamGridDbApiKey`: optionaler, kostenloser Key von [SteamGridDB](https://www.steamgriddb.com/profile/preferences/api) für Cover von Nicht-Steam-Spielen und Apps. Ohne Key wird per Name im Steam-Store gesucht.
- `image`: Cover als lokaler Pfad oder URL (Hochformat 2:3 bei Spielen, 16:9 bei Apps).

## KI-Assistent und MCP

Unter *Einstellungen › KI & MCP* (Expertenmodus) wählst du den Anbieter (Anthropic oder OpenAI), hinterlegst deinen API-Key (verschlüsselt mit Windows DPAPI) und suchst das Modell aus. Danach taucht der Tab **Assistent** auf.

Der Assistent arbeitet nur über eine feste Liste von Werkzeugen: Bibliothek durchsuchen, Details lesen, starten, Favoriten, Einträge bearbeiten und anlegen, Bibliothek neu scannen, Einstellungen und Skin ändern, Wetter, News, Updates, Home Assistant, Lautstärke, Audio-Ausgang, Energie. Er führt keine Shell-Befehle aus und greift nicht auf Dateien zu. API-Keys und Tokens sieht er nie.

**MCP-Server**: Einschalten erzeugt ein Token. Die Adresse und das Token stehen in den Einstellungen. Beispiel für einen MCP-Client mit Streamable HTTP:

```json
{
  "mcpServers": {
    "couchhud": {
      "type": "http",
      "url": "http://192.168.178.42:8765/mcp",
      "headers": { "Authorization": "Bearer <Token aus den Einstellungen>" }
    }
  }
}
```

Ohne „Aus dem Netzwerk erreichbar“ hört der Server nur auf `localhost`. Für den Zugriff aus dem LAN legt der CouchHUD-Dienst (mit Freigabe, ohne UAC-Abfrage) eine Firewall-Regel namens **„CouchHUD Remote (CouchHUD)“** an (nur Freigabe, eingehend, TCP); sie gilt nur für die benutzten Ports (Standard 8765) und das lokale Subnetz. Eigene Regeln von Windows werden nicht angefasst, Block-Regeln von Windows nur gemeldet. Der Server nimmt nur Anfragen aus dem eigenen Netz an (private und Link-Local-Adressen). Jede Änderung von außen und das Auslesen der Home-Assistant-Zustände zeigt CouchHUD als Frage auf dem TV an (die Antwort kommt nur vom TV, nie vom Handy); Plugin-Werkzeuge werden über MCP immer bestätigt, mit ihren Argumenten. Bei gesperrter Couch bieten Assistent und MCP nur Nachschlagen und Starten aus der Bibliothek an.

## Plugins und Skins

CouchHUD soll mit der Community wachsen. Beides lässt sich direkt in der Oberfläche als `.zip` installieren (*Einstellungen › Darstellung › Neue Skins installieren* bzw. im Expertenmodus *Plugins & Skins*).

- **Plugins** (C#, SDK v2) bringen Spiele-Quellen, eigene Seiten, Einstellungen, Einträge im Guide-Menü, Werkzeuge für den KI-Assistenten und reagieren auf Spielstart und -ende. CouchHUD rendert Plugin-Oberflächen selbst, deshalb passen sie zu jedem Skin und funktionieren mit dem Controller. Anleitung: [PLUGINS.md](PLUGINS.md)
- **Skins** (CSS) ändern Farben, Schriften, Formen und Layout. Anleitung: [SKINS.md](SKINS.md)
- Mitgeliefert: **Wand** (früher WeMod), Cheats und Mods per Guide-Menü, auf Wunsch automatisch mit jedem Spiel. Gleichzeitig das Beispiel für eigene Plugins.
- **Katalog**: *Plugins & Skins › Katalog* listet Plugins und Skins aus einem JSON-Index auf GitHub (Standard `kewe87/CouchHUD-Catalog`, weitere Quellen in der `config.json`). Der Index des Standard-Katalogs ist signiert, Installation und Updates laufen mit A, nur über https und mit Pflicht-Prüfsumme (SHA-256); vor Plugins warnt CouchHUD, dass sie mit deinen Rechten laufen. Im Hintergrund (nie im Spiel) sucht CouchHUD nach Updates und meldet sie in den Benachrichtigungen und auf der Updates-Seite. Format und Sicherheit: [PLUGINS.md](PLUGINS.md#katalog)

Wie du beitragen kannst: [CONTRIBUTING.md](../CONTRIBUTING.md)

## Architektur

```
ui/                        Web-UI (Vanilla JS/CSS als ES-Module, keine Build-Pipeline), läuft in WebView2
  app.js                   Start, Tabs, globale Tasten
  core/                    Navigation, State, Bibliothek, Artwork, Trailer, Wetter, i18n
  views/                   Home, Grid, Detail, News, Smart Home, Assistent, Updates, Einstellungen
  components/              Overlay-Menüs, Guide-Menü, Bildschirmtastatur, Datei-Browser, Maus
  i18n/de, i18n/en         Texte der Oberfläche (je eine Datei pro Bereich)
  skins/                   Eingebaute Skins
  bridge.js / mock-api.js  Host-Bridge (chrome.webview) bzw. Mock-Daten für die Browser-Vorschau
src/CouchHud.Sdk/          Plugin-API
src/CouchHud.Core/         Plattformneutral und getestet: Config, Scanner (Steam/Epic/Custom),
                           Artwork, Store-Infos, Spielzeit-Tracking, Spielstände, Jugendschutz,
                           Datei-Browser, EXE-Scan, Wetter, News, Home Assistant, winget-Parser,
                           KI-Assistent, MCP-Protokoll, Katalog und Self-Update, Texte (Localization)
src/CouchHud.Shell/        WPF + WebView2 Host
  Hud.cs                   Orchestrierung: Guide-Button, PC-Modus, Tray, Bridge-API
  Input/XInputPoller.cs    XInput inkl. Guide (XInputGetStateEx), Vibration, Akku
  Native/                  Win32 und Core Audio (COM)
  Services/                Shell-Registry, Autostart, Power, Launcher, Audio, Updates, Couch-Sperre,
                           Vordergrund-Wächter, Jugendschutz, Assistent-Werkzeuge, MCP-Server (Kestrel)
  UiHost/                  JSON-Bridge zur UI, lokale Dateien für Artwork/Skins
plugins/                   Mitgeliefertes Plugin (Wand)
installer/                 Inno-Setup-Skript
catalog/                   Beispiel für einen Plugin-Katalog (index.json + Signatur)
scripts/                   check.sh, i18n-check, Vorschau-Server, Signier- und VM-Skripte
tests/                     xUnit-Tests für Core
```

Alles mit Admin-Rechten erledigt der Windows-Dienst **CouchHud.Service** (`src/CouchHud.Service`, LocalSystem) über eine Named Pipe mit fester Befehlsliste; die Shell selbst braucht nie Admin. Protokoll in `src/CouchHud.Service.Contracts`, Admin-Code in `src/CouchHud.Privileged`, Client in `src/CouchHud.ServiceClient`.

## UI-Entwicklung ohne Windows

```bash
node scripts/preview-server.js   # http://localhost:5173, komplette UI mit Mock-Daten im Browser (?lang=en für Englisch)
```

Mehr dazu (Screenshots per `ui-shot.mjs`, VM-Tests) in [CONTRIBUTING.md](../CONTRIBUTING.md).

## Roadmap

Der vollständige Plan mit Reihenfolge, Plugin-SDK und den Hardware-Integrationen (FanControl, SignalRGB, CaseHUD) steht in [ROADMAP.md](ROADMAP.md).

## Haftungsausschluss und Datenschutz

CouchHUD wird ohne Gewährleistung bereitgestellt, die Nutzung erfolgt auf eigenes Risiko: [Haftungsausschluss](DISCLAIMER.md). Keine Telemetrie, kein Konto. Welche Daten lokal liegen und welche Dienste CouchHUD wann kontaktiert, steht im [Datenschutzhinweis](PRIVACY.md). Beides bestätigst du beim ersten Start.

## Lizenz / License

Copyright © 2026 **kewe87**

Dieses Programm ist freie Software unter der **GNU General Public License v3.0**
([LICENSE](../LICENSE)). Du darfst es nutzen, verändern und weitergeben – Forks und
Weiterentwicklungen müssen aber ebenfalls unter der GPL-3.0 offen bleiben und den
Copyright-Hinweis „kewe87“ behalten.

Free software under the GPL-3.0: forks and derivatives must stay open source under
the same license and keep the copyright notice.
