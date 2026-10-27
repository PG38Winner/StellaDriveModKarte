# StellarDrive Live-Karte

Eine MelonLoader-Mod (`mod/StellarMap.cs`) und eine Webseite (`index.html`), die Schiffe, Stationen und Spieler einer StellarDrive-Welt live in 3D zeigen.

## So funktioniert es

1. Die Mod nimmt alle 10 s den Weltzustand des Spiels, also dieselben Daten wie in `world.json`, und schreibt daraus `UserData/StellarMap/map.json`. Steam-IDs werden nicht übernommen.
2. Lokal ist die Karte unter <http://localhost:8787/> erreichbar, solange das Spiel läuft. Der Server ist nur auf diesem PC erreichbar.
3. Mit `Publish = true` pusht die Mod `map.json` alle 60 s in den Branch `live-data` dieses Repos, und zwar immer als einen einzigen Commit, damit der Branch nicht wächst. Die Seite auf GitHub Pages liest die Datei von dort.

Das Spiel verlangt, dass alle Mitspieler dieselben Mods haben. Deshalb läuft StellarMap auch bei ihnen, veröffentlicht wird aber nur vom PC, der die Welt hostet (FishNet `IsServerStarted`; Einzelspieler zählt als Host). Bei Mitspielern bleibt die lokale Karte unter localhost aktiv.

Auf der Seite kann man außerdem eine `world.json` per Knopf oder Drag & Drop laden. Dafür muss das Spiel nicht laufen.

## Bauen und installieren

Voraussetzung: Visual Studio Build Tools mit dem Roslyn-Compiler `csc.exe` und ein Spiel mit installiertem MelonLoader 0.7.3. Das Spiel muss beim Installieren geschlossen sein.

```powershell
.\mod\build.ps1
```

Das Skript kopiert `StellarMap.dll` nach `Mods\` und `index.html` nach `UserData\StellarMap\www\`.

## Einstellungen

Die Einstellungen stehen in `UserData\MelonPreferences.cfg` im Abschnitt `[StellarMap]`:

| Eintrag | Standard | Bedeutung |
|---|---|---|
| `ExportSeconds` | 10 | Abstand zwischen zwei Schnappschüssen |
| `ServeLocal` / `LocalPort` | true / 8787 | lokale Seite |
| `Publish` | false | nach GitHub pushen (nutzt den gespeicherten git-Login) |
| `PublishSeconds` | 60 | Abstand zwischen zwei Pushes, mindestens 30 |
| `RepoUrl` / `Branch` | dieses Repo / `live-data` | Ziel der Daten |
| `IncludePlayerNames` | true | aus: „Spieler 1“, „Spieler 2“ … |

## GitHub Pages

Das Repo muss öffentlich sein. Unter *Settings → Pages* wählst du *Deploy from a branch*, dann `main` und `/ (root)`. Die Karte ist danach unter `https://pg38winner.github.io/StellaDriveModKarte/` erreichbar.
