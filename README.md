# StellarDrive Live-Karte

Eine MelonLoader-Mod (`mod/StellarMap.cs`) und eine Webseite (`index.html`), die Schiffe, Stationen und Spieler einer StellarDrive-Welt live in 3D zeigen.

## So funktioniert es

1. Die Mod nimmt den Weltzustand des Spiels, also dieselben Daten wie in `world.json`, und schreibt daraus `UserData/StellarMap/map.json`. Steam-IDs werden nicht übernommen. Das passiert nur auf dem Host: alle 10 s, solange die lokale Seite offen ist, sonst nur vor jedem Upload (alle 60 s).
2. Lokal ist die Karte unter <http://localhost:8787/> erreichbar, solange das Spiel läuft. Der Server ist nur auf diesem PC erreichbar.
3. Hostet der PC die Welt, pusht die Mod `map.json` alle 60 s in den Branch `live-data` dieses Repos, und zwar immer als einen einzigen Commit, damit der Branch nicht wächst. Die Seite auf GitHub Pages liest die Datei von dort.

Das Spiel verlangt, dass alle Mitspieler dieselben Mods haben. Deshalb läuft StellarMap auch bei ihnen, veröffentlicht wird aber nur vom PC, der die Welt hostet (FishNet `IsServerStarted`; Einzelspieler zählt als Host). Mitspieler haben die Weltdaten nicht, ihre lokale Karte bleibt leer; sie nutzen die öffentliche Seite.

## Andere Hosts

Jeder, der eine Welt hostet, kann die öffentliche Karte füllen. Dafür braucht er:

1. die `StellarMap.dll` in `Mods\` (`build.ps1` ist nicht nötig),
2. [Git für Windows](https://git-scm.com/download/win),
3. Schreibrechte im Repo: Der Besitzer lädt ihn unter *Settings → Collaborators* ein, er nimmt die Einladung an,
4. einmal einen GitHub-Login auf seinem PC: `git credential-manager github login`.

Fehlt etwas davon, schreibt die Mod einmal eine Warnung ins MelonLoader-Log und versucht es bis zum Neustart nicht mehr. Es gibt nur eine öffentliche Karte: Hosten zwei Leute gleichzeitig, zeigt sie die Welt, die zuletzt hochgeladen hat.

Auf der Seite kann man außerdem eine `world.json` per Knopf oder Drag & Drop laden. Dafür muss das Spiel nicht laufen.

## Bauen und installieren

Voraussetzung: Visual Studio Build Tools mit dem Roslyn-Compiler `csc.exe` und ein Spiel mit installiertem MelonLoader 0.7.3. Das Spiel muss beim Installieren geschlossen sein.

```powershell
.\mod\build.ps1
```

Das Skript kopiert `StellarMap.dll` nach `Mods\` und alle Seiten (`*.html`, also Live-Karte und Stationsmodell) nach `UserData\StellarMap\www\`.

## Einstellungen

Die Einstellungen stehen in `UserData\MelonPreferences.cfg` im Abschnitt `[StellarMap]`:

| Eintrag | Standard | Bedeutung |
|---|---|---|
| `ExportSeconds` | 10 | Abstand zwischen zwei Schnappschüssen, solange die lokale Seite offen ist |
| `ServeLocal` / `LocalPort` | true / 8787 | lokale Seite |
| `PublishWhenHost` | true | als Host nach GitHub pushen (nutzt den gespeicherten git-Login) |
| `PublishSeconds` | 60 | Abstand zwischen zwei Pushes, mindestens 30 |
| `RepoUrl` / `Branch` | dieses Repo / `live-data` | Ziel der Daten |
| `IncludePlayerNames` | true | aus: „Spieler 1“, „Spieler 2“ … |

## GitHub Pages

Das Repo muss öffentlich sein. Unter *Settings → Pages* wählst du *Deploy from a branch*, dann `main` und `/ (root)`. Die Karte ist danach unter `https://pg38winner.github.io/StellaDriveModKarte/` erreichbar.
