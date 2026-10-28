# CaseHUD in CouchHUD

[CaseHUD](https://github.com/kewe87/CaseHUD) zeigt Temperaturen, Last und mehr auf den LCDs in Lüftern und Pumpen. Es ist ein **eigenes Programm** mit eigener lokaler API.

**Seit CouchHUD 0.6 ist die Anbindung ein Plugin**, kein Teil von CouchHUD mehr: das CouchHUD-Plugin „CaseHUD“ (Repository `CouchHud.Plugin.CaseHud`, kostenlos, GPL-3.0). CaseHUD liefert es nicht mit; du installierst es aus dem Katalog (*Einstellungen › Plugins › Katalog*). Wer CaseHUD installiert hat, bekommt es einmal vorgeschlagen. Ohne das Plugin weiß CouchHUD nichts von CaseHUD.

- **Was das Plugin kann:** schalten und bedienen. Szenen und Stile wählen, Displays ein und aus, Rotation und Automatik, nächste Szene, Helligkeit, Hell/Dunkel und Modus je Display, Beleuchtung (an, Helligkeit, Effekt), Displays finden, Einträge im Guide-Menü, Spiele an CaseHUD melden, bei Bedarf die Schnittstelle von CaseHUD ein- oder ausschalten und CaseHUD beim ersten Mal installieren. Designs, Effekt-Parameter, Geräte, Regeln und Community-Designs bleiben im CaseHUD-Fenster am PC („Am PC öffnen“).
- **Wo:** *Einstellungen › Plugins › CaseHUD* (kein eigener Reiter). Die Einstellung *Spiele an CaseHUD melden* steht auf der Seite des Plugins.
- **Couch-Sperre:** ohne Code gehen Ansehen, Szene wählen oder zur nächsten wechseln, Displays ein/aus und Displays finden; alles andere braucht die Entsperrung. CaseHUDs Fenster wird auf der gesperrten Couch minimiert, solange das Plugin geladen ist (es nennt dem Programm dafür CaseHUDs Programmdatei).
- **Aktualisieren** erledigt CaseHUD selbst („Am PC öffnen“); das Plugin zeigt nur „Update verfügbar“. Das frühere Aktualisieren ohne Windows-Abfrage über den CouchHUD-Dienst gibt es nicht mehr (siehe [SECURITY.md](../SECURITY.md)).
- **Einstellungen aus 0.5:** Übernommen wird beim ersten Start des Plugins nur *Spiele an CaseHUD melden*. Start mit Windows, FPS-Erkennung und Schnittstelle nach der Installation fragt das CaseHUD-Setup jetzt selbst.
- **Entwickler:** Die Schnittstelle von CaseHUD steht in dessen eigener Dokumentation (`docs/API.md` im CaseHUD-Repository); der Quelltext des Plugins im Plugin-Repository. (English: see the CaseHUD paragraph in [GUIDE.en.md](GUIDE.en.md); the plugin is installed from the catalog.)

Die Bedienung des Plugins im Einzelnen (Zustände, Kacheln, Beleuchtung, Hinweise zur Schnittstelle) steht in der Beschreibung des Plugins im Katalog.
