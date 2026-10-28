# Haftungsausschluss

**Deutsch** | [English](DISCLAIMER.en.md)

*Stand: Oktober 2026, gilt für CouchHUD ab Version 0.3*

CouchHUD ist freie Software unter der [GNU General Public License v3.0](../LICENSE). Sie wird kostenlos und **ohne jede Gewährleistung** bereitgestellt, im gesetzlich zulässigen Umfang (Abschnitte 15 und 16 der GPL-3.0). Die Nutzung erfolgt auf eigenes Risiko.

## Eingriffe in Windows

CouchHUD kann, je nach gewähltem Modus und den Funktionen, die du einschaltest, Einstellungen von Windows verändern. Im **App-Modus** verhält sich CouchHUD wie eine normale Anwendung. Im **Shell-Modus** kann CouchHUD unter anderem:

- sich für dein Benutzerkonto als Windows-Shell eintragen (statt des Desktops),
- die Anmeldung beeinflussen (Couch-Sperre, Wächterprozess, automatische Windows-Anmeldung: das Passwort wird einmal eingegeben, geprüft und von Windows verschlüsselt als LSA-Secret abgelegt),
- den Task-Manager im Couch-Modus sperren,
- ausgewählte Windows-Dienste pausieren und Autostart-Programme selbst starten,
- auf Wunsch über das CaseHUD-Plugin **CaseHUD** (ein eigenes Programm für die Gehäuse-Displays) herunterladen, prüfen und mit Administrator-Bestätigung installieren (das Setup richtet einen Sensor-Dienst und einen Treiber ein) und es im Hintergrund starten,
- Fenster von Spiele-Clients und Update-Popups minimieren und der Xbox Game Bar die Guide-Taste abnehmen („CouchHUD bleibt vorne“, im PC-Modus und beim Beenden wieder zurückgesetzt).

Jeder Eingriff ist einzeln beschrieben, mit Ort, Admin-Bedarf und Weg zum Rückgängigmachen, in CouchHUD unter **Einstellungen › HUD › Was CouchHUD an Windows ändert**. Für den Notfall gibt es den Wiederherstellungsmodus: **Strg + Umschalt** oder **LB + RB + View** beim Start etwa 1,5 Sekunden halten.

## Wichtige Hinweise

- **Automatische Anmeldung** bedeutet, dass jeder mit Zugang zum PC an dein Windows-Konto kommt, wenn keine Couch-Sperre aktiv ist.
- **Die Couch-Sperre ist nur so stark wie das Windows-Kennwort deines Kontos.** Sie hält Kinder und Gäste am Controller vom Desktop fern, ist aber keine Sicherheitslösung: Wer Tastatur und Zeit hat, den PC von einem anderen Medium startet oder die Festplatte ausbaut, kommt an deine Daten. Bei automatischer Anmeldung liegt das Kennwort (von Windows verschlüsselt) auf dem PC. Aktiviere deshalb die **Geräteverschlüsselung bzw. BitLocker** und nimm ein ordentliches Kennwort. Zudem schützt die Sperre nicht alles, was aus CouchHUD heraus gestartet wird (z.B. Spiele-Clients mit eigenen Browsern oder Datei-Dialogen); CouchHUD hält diese Clients im Couch-Modus im Hintergrund, kann aber nicht jeden Weg schließen. Die Windows-Anmeldung als Sperre verlangt ein Konto mit Kennwort; der Controller-Code geht auch ohne, mit deutlicher Warnung (ohne Kennwort kommt jeder mit Tastatur ans Konto). Die automatische Anmeldung braucht ein Kennwort und entfällt sonst. Bei geschlossener Sperre startet CouchHUD nur Spiele, minimiert klar erkannte Nicht-Spiele (Einstellungen, Explorer, Konsolen, Browser), schließt Datei- und Druckdialoge und setzt reversible Windows-Richtlinien (Details unter *Was CouchHUD an Windows ändert*). Das ist ein Kiosk-Schutz gegen Kinder und Gäste am Controller, keine Absicherung gegen Angriffe mit Tastatur, Startmedium, ausgebautem Laufwerk oder Administratorrechten; dafür sind ein gutes Kennwort und BitLocker bzw. die Geräteverschlüsselung nötig. CouchHUD aktiviert diese nicht selbst.
- **Der Jugendschutz hat Grenzen.** Er schützt gegen Kinder am Controller, nicht gegen jemanden, der sich im Windows auskennt: Wer Dateizugriff auf den Benutzerordner hat (Einstellungen, Zähler), kann sie verändern oder löschen (eine veränderte Zeitdatei lässt den Tag zwar als verbraucht gelten, die Regeln selbst lassen sich aber ändern). Er wirkt nur als Windows-Shell mit eingeschalteter Couch-Sperre, nicht im App-Modus und nicht im PC-Modus. Spiele, die nicht über CouchHUD gestartet wurden, werden nur gezählt, solange CouchHUD sie als laufendes Spiel erkennt; Einträge ohne erkennbaren Programmordner (Webseiten, manche Store-Apps) lassen sich nicht messen und starten deshalb nur mit Eltern-Code. Altersgrenzen beruhen auf den Angaben des Steam-Stores und sind keine Altersfreigabe. Der Eltern-Code ist der Code der Couch-Sperre oder das Windows-Kennwort: Wer ihn kennt, kann alles ändern.
- **Spiel pausieren** hält Prozesse an. Online-Spiele können die Verbindung verlieren, Anti-Cheat-Systeme können darauf reagieren. Die Funktion ist standardmäßig aus.
- **Plugins** stammen von Dritten. Sie laufen mit denselben Rechten wie CouchHUD. Installiere nur Plugins aus Quellen, denen du vertraust. Für Plugins und deren Folgen sind die jeweiligen Autoren verantwortlich.
- **Cheats und Mods** (z.B. über das Wand-Plugin) können gegen die Nutzungsbedingungen von Spielen verstoßen und zu Sperren führen, besonders in Online-Spielen.
- **Markennamen** wie Xbox, PlayStation, Steam, Epic Games, GOG, Ubisoft, EA, Battle.net, Windows, Discord, WhatsApp und andere gehören ihren Inhabern. CouchHUD steht in keiner Verbindung zu ihnen. Die Skins, die CouchHUD mitbringt und im Katalog anbietet, tragen eigene Namen, verwenden keine Originalgrafiken oder Logos und beschreiben nur einen Stil.

Mit dem Bestätigen beim ersten Start erklärst du, diese Hinweise gelesen zu haben.
