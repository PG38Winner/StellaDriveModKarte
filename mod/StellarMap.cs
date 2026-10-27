using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEngine;

[assembly: MelonInfo(typeof(StellarMap.StellarMapMod), "StellarMap", "1.3.0", "paulg")]
[assembly: MelonGame("CuriousOwlGames", "StellarDrive")]

namespace StellarMap
{
    // Live map: takes the game's own world state (the same data the save file holds) every few
    // seconds, strips it down to what a map needs and writes UserData/StellarMap/map.json.
    // The file is served on localhost and, if enabled, pushed to a GitHub branch for GitHub Pages.
    public class StellarMapMod : MelonMod
    {
        internal static MelonLogger.Instance Log;

        MelonPreferences_Entry<float> _exportSeconds;
        MelonPreferences_Entry<bool> _serveLocal;
        MelonPreferences_Entry<int> _localPort;
        MelonPreferences_Entry<bool> _publish;
        MelonPreferences_Entry<float> _publishSeconds;
        MelonPreferences_Entry<string> _repoUrl;
        MelonPreferences_Entry<string> _branch;
        MelonPreferences_Entry<string> _gitPath;
        MelonPreferences_Entry<bool> _includePlayerNames;

        string _dir, _mapFile, _wwwDir, _publishDir;
        float _nextExport;
        long _nextPublish;
        volatile bool _busy;
        volatile bool _publishing;
        volatile string _lastJson;
        string _lastPublishedJson;
        int _failCount;
        volatile bool _publishBlocked;
        volatile bool _accessOk;
        long _nextAccessCheck;
        bool _loginOpened, _inviteOpened;
        volatile string _openUrl, _pendingNotice;
        string _notice;
        float _noticeUntil;

        // Game objects, found by name so the mod does not break when a type moves between versions.
        UnityEngine.Object _saver;
        Type _saverType;
        FieldInfo _fStateProvider, _fConverter, _fSaveFile;
        IContractResolver _resolver;

        HttpListener _http;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;
            var cat = MelonPreferences.CreateCategory("StellarMap");
            _exportSeconds = cat.CreateEntry("ExportSeconds", 10f,
                description: "Seconds between two map snapshots (map.json). Lower = more live, each snapshot costs a few milliseconds on large worlds.");
            _serveLocal = cat.CreateEntry("ServeLocal", true,
                description: "Serve the map on this PC at http://localhost:LocalPort/ (only reachable from this PC).");
            _localPort = cat.CreateEntry("LocalPort", 8787,
                description: "Port of the local map page.");
            // New name so that players who ran 1.1.0 (default off) also publish when they host.
            _publish = cat.CreateEntry("PublishWhenHost", true,
                description: "When this PC hosts the world, push map.json to the GitHub branch below so the GitHub Pages map shows it. Needs git and a GitHub login with write access to the repository (collaborator).");
            _publishSeconds = cat.CreateEntry("PublishSeconds", 60f,
                description: "Seconds between two pushes to GitHub (at least 30). Only pushed when something changed.");
            _repoUrl = cat.CreateEntry("RepoUrl", "https://github.com/PG38Winner/StellaDriveModKarte.git",
                description: "GitHub repository that hosts the map page.");
            _branch = cat.CreateEntry("Branch", "live-data",
                description: "Branch that only holds map.json. It is force-pushed with a single commit, so it never grows.");
            _gitPath = cat.CreateEntry("GitPath", "git",
                description: "git executable.");
            _includePlayerNames = cat.CreateEntry("IncludePlayerNames", true,
                description: "Show player names on the map. Off: players are shown as \"Spieler 1\", \"Spieler 2\"... Steam ids are never exported.");

            _dir = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "StellarMap");
            _wwwDir = Path.Combine(_dir, "www");
            _publishDir = Path.Combine(_dir, "publish");
            _mapFile = Path.Combine(_dir, "map.json");
            Directory.CreateDirectory(_dir);

            if (_serveLocal.Value) StartServer();
            Log.Msg("Map file: " + _mapFile + (_publish.Value ? " | publishing to " + _repoUrl.Value + " (" + _branch.Value + ")" : " | publishing off"));
        }

        public override void OnDeinitializeMelon()
        {
            try { _http?.Stop(); } catch { }
        }

        public override void OnUpdate()
        {
            // Unity calls from the publish thread are handed over to the main thread here.
            var url = _openUrl;
            if (url != null) { _openUrl = null; Application.OpenURL(url); }
            var notice = _pendingNotice;
            if (notice != null) { _pendingNotice = null; _notice = notice; _noticeUntil = Time.unscaledTime + 30f; }
            if (Time.unscaledTime < _nextExport || _busy) return;
            _nextExport = Time.unscaledTime + Mathf.Max(2f, _exportSeconds.Value);
            try { Snapshot(); }
            catch (Exception e) { Fail("Snapshot failed: " + e.Message); }
        }

        GUIStyle _noticeStyle;

        public override void OnGUI()
        {
            if (_notice == null || Time.unscaledTime > _noticeUntil) return;
            if (_noticeStyle == null)
            {
                var bg = new Texture2D(1, 1);
                bg.SetPixel(0, 0, new Color(0.04f, 0.06f, 0.11f, 0.94f));
                bg.Apply();
                _noticeStyle = new GUIStyle(GUI.skin.box) { wordWrap = true, alignment = TextAnchor.MiddleCenter };
                _noticeStyle.normal.background = bg;
                _noticeStyle.normal.textColor = Color.white;
            }
            // Scale with the screen so the text stays readable at 1440p and 4K.
            float k = Mathf.Max(1f, Screen.height / 1080f);
            _noticeStyle.fontSize = Mathf.RoundToInt(22 * k);
            _noticeStyle.padding = new RectOffset((int)(20 * k), (int)(20 * k), (int)(14 * k), (int)(14 * k));
            float w = Mathf.Min(900f * k, Screen.width - 40f);
            float h = _noticeStyle.CalcHeight(new GUIContent(_notice), w);
            GUI.Box(new Rect((Screen.width - w) / 2f, 30f * k, w, h), _notice, _noticeStyle);
        }

        // Shown in game: the host sits in a fullscreen game and would not see a log line.
        void Notice(string text) { _pendingNotice = text; Log.Msg(text); }

        void Fail(string msg)
        {
            // Keep the log readable when something keeps failing every few seconds.
            if (_failCount++ < 5 || _failCount % 100 == 0) Log.Warning(msg);
        }

        bool FindSaver()
        {
            if (_saver != null) return true;
            if (_saverType == null)
            {
                _saverType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Saving.Services.GameSaver", false)).FirstOrDefault(t => t != null);
                if (_saverType == null) return false;
                const BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
                _fStateProvider = _saverType.GetField("_worldStateProvider", bf);
                _fConverter = _saverType.GetField("_gameWorldStateConverter", bf);
                _fSaveFile = _saverType.GetField("_saveFile", bf);
                var resolverType = _saverType.Assembly.GetType("Saving.Settings.WritableOnlyContractResolver", false);
                _resolver = resolverType != null ? (IContractResolver)Activator.CreateInstance(resolverType) : new DefaultContractResolver();
                if (_fStateProvider == null || _fConverter == null)
                    Log.Error("GameSaver layout not recognised; map export disabled.");
            }
            if (_fStateProvider == null || _fConverter == null) return false;
            _saver = UnityEngine.Object.FindAnyObjectByType(_saverType);
            return _saver != null;
        }

        void Snapshot()
        {
            if (!FindSaver()) return;
            var provider = _fStateProvider.GetValue(_saver);
            var converter = _fConverter.GetValue(_saver);
            if (provider == null || converter == null) return;
            var state = provider.GetType().GetMethod("GetWorldState")?.Invoke(provider, null);
            if (state == null) return;
            var serializable = converter.GetType().GetMethod("ConvertToSerializable")?.Invoke(converter, new[] { state });
            if (serializable == null) return;

            string worldName = null;
            try
            {
                var file = _fSaveFile?.GetValue(_saver);
                var info = file?.GetType().GetProperty("SaveFileInfo")?.GetValue(file, null) as FileInfo;
                worldName = info?.Directory?.Name;
                // Only known after the first save; until then the newest world folder is the one just loaded.
                if (worldName == null)
                    worldName = new DirectoryInfo(Path.Combine(Application.persistentDataPath, "WorldSaves")).GetDirectories()
                        .OrderByDescending(d => d.LastWriteTimeUtc).FirstOrDefault()?.Name;
            }
            catch { }
            var gameVersion = Application.version;

            // The serializable world is a snapshot (arrays of values), so turning it into JSON can
            // run off the main thread.
            _busy = true;
            bool names = _includePlayerNames.Value;
            bool host = IsHost();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var ser = JsonSerializer.Create(new JsonSerializerSettings
                    {
                        ContractResolver = _resolver,
                        ReferenceLoopHandling = ReferenceLoopHandling.Ignore
                    });
                    var world = JObject.FromObject(serializable, ser);
                    var map = MapData.Build(world, worldName, names);
                    if (map["gameVersion"] == null || map["gameVersion"].Type == JTokenType.Null) map["gameVersion"] = gameVersion;
                    var json = map.ToString(Formatting.None);
                    WriteAtomic(_mapFile, json);
                    _lastJson = json;
                    _failCount = 0;
                }
                catch (Exception e) { Fail("Export failed: " + e.Message); }
                finally { _busy = false; }
                MaybePublish(host);
            });
        }

        static void WriteAtomic(string path, string text)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        // ---------- GitHub publishing ----------

        PropertyInfo _isServerStarted;
        bool _hostLookupDone, _loggedNotHost;

        // Every player needs the mod (the game checks the mod list on join), but only the PC that
        // hosts the world may publish; otherwise each client would overwrite the map. Singleplayer
        // counts as host. If the check is not available, nothing is published.
        bool IsHost()
        {
            if (!_hostLookupDone)
            {
                _hostLookupDone = true;
                var finder = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("FishNet.InstanceFinder", false)).FirstOrDefault(t => t != null);
                _isServerStarted = finder?.GetProperty("IsServerStarted", BindingFlags.Public | BindingFlags.Static);
                if (_isServerStarted == null) Log.Warning("FishNet host check not found; publishing disabled.");
            }
            try { return _isServerStarted != null && (bool)_isServerStarted.GetValue(null, null); }
            catch { return false; }
        }

        void MaybePublish(bool host)
        {
            if (!_publish.Value || _publishing || _publishBlocked) return;
            if (!host)
            {
                if (!_loggedNotHost) { _loggedNotHost = true; Log.Msg("Not the host of this world; the host publishes the map."); }
                return;
            }
            _loggedNotHost = false;
            var json = _lastJson;
            if (json == null || json == _lastPublishedJson) return;
            // Only the time stamp changed? Then there is nothing new to show.
            if (_lastPublishedJson != null && MapData.SameContent(json, _lastPublishedJson)) return;
            var now = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
            if (!_accessOk && now < _nextAccessCheck) return;
            if (now < _nextPublish) return;
            _nextPublish = now + (long)Math.Max(30f, _publishSeconds.Value);
            _publishing = true;
            try
            {
                Directory.CreateDirectory(_publishDir);
                if (!Directory.Exists(Path.Combine(_publishDir, ".git")))
                {
                    Git("init -q");
                    Git("checkout -q --orphan " + _branch.Value);
                }
                Git("remote remove origin", ignoreErrors: true);
                Git("remote add origin " + _repoUrl.Value);
                WriteAtomic(Path.Combine(_publishDir, "map.json"), json);
                Git("add map.json");
                bool hasHead = Git("rev-parse -q --verify HEAD", ignoreErrors: true) == 0;
                // Always a single commit: the branch only ever holds the latest map.
                // Fixed author: hosts need no git identity and their e-mail stays out of the public repo.
                Git("-c user.name=StellarMap -c user.email=stellarmap@users.noreply.github.com "
                    + (hasHead ? "commit -q --amend --reset-author" : "commit -q") + " -m \"Live-Karte aktualisiert\"");
                string err;
                // Before the first upload: is this PC logged in to GitHub and allowed to push?
                if (!_accessOk)
                {
                    if (Git("push -q --dry-run -f origin HEAD:refs/heads/" + _branch.Value, out err, ignoreErrors: true) != 0)
                    {
                        NoAccess(err, now);
                        return;
                    }
                    _accessOk = true;
                    if (_loginOpened || _inviteOpened) Notice("Live-Karte: GitHub-Zugang klappt, die Karte wird jetzt hochgeladen.");
                    else Log.Msg("GitHub access OK.");
                }
                if (Git("push -q -f origin HEAD:refs/heads/" + _branch.Value, out err, ignoreErrors: true) == 0)
                {
                    _lastPublishedJson = json;
                    Log.Msg("Map published to GitHub.");
                }
                else if (IsLoginError(err) || IsNoAccessError(err)) { _accessOk = false; NoAccess(err, now); }
                else Fail("git push -> " + err.Trim());
            }
            catch (System.ComponentModel.Win32Exception)
            {
                _publishBlocked = true;
                Log.Warning("Map not published: git not found (" + _gitPath.Value + "). Install Git for Windows or set GitPath.");
            }
            catch (Exception e) { Fail("Publish failed: " + e.Message); }
            finally { _publishing = false; }
        }

        // Called on the publish thread. Each page opens at most once per game session; after that
        // the check just runs again every two minutes, so accepting an invitation needs no restart.
        void NoAccess(string err, long now)
        {
            _nextAccessCheck = now + 120;
            var repo = Regex.Replace(_repoUrl.Value, @"\.git$", "");
            if (IsNoAccessError(err))
            {
                if (_inviteOpened) return;
                _inviteOpened = true;
                var user = Regex.Match(err, @"denied to ([^\s.]+)").Groups[1].Value;
                Log.Warning("No write access to " + repo + ": " + err.Trim());
                Notice("Live-Karte: Das GitHub-Konto " + (user == "" ? "" : user + " ") + "darf die Karte nicht hochladen. "
                    + "Der Besitzer muss dich als Mitarbeiter einladen. Die Einladung kannst du auf der eben geöffneten Seite annehmen.");
                _openUrl = repo + "/invitations";
            }
            else if (IsLoginError(err))
            {
                if (_loginOpened) return;
                _loginOpened = true;
                Notice("Live-Karte: Du hostest, bist aber nicht bei GitHub angemeldet. "
                    + "Melde dich im GitHub-Fenster an, das gerade aufgegangen ist (Alt+Tab).");
                // Git Credential Manager shows its own sign-in window (browser, code or token).
                int code = Git("credential-manager github login", out err, ignoreErrors: true, interactive: true, timeoutMs: 15 * 60000);
                if (code == 0) _nextAccessCheck = 0;
                else Notice("Live-Karte: GitHub-Anmeldung abgebrochen. Ohne Anmeldung wird die Karte nicht hochgeladen.");
            }
            else Fail("git push --dry-run -> " + err.Trim());
        }

        static bool IsNoAccessError(string err)
        {
            var e = err.ToLowerInvariant();
            return e.Contains("403") || e.Contains("denied to");
        }

        static bool IsLoginError(string err)
        {
            var e = err.ToLowerInvariant();
            return e.Contains("could not read username") || e.Contains("could not read password")
                || e.Contains("terminal prompts disabled") || e.Contains("authentication failed") || e.Contains("401");
        }

        int Git(string args, bool ignoreErrors = false) => Git(args, out _, ignoreErrors);

        int Git(string args, out string err, bool ignoreErrors = false, bool interactive = false, int timeoutMs = 60000)
        {
            var psi = new ProcessStartInfo(_gitPath.Value, args)
            {
                WorkingDirectory = _publishDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            // Never wait for a login prompt that nobody can see; only the explicit login may show a window.
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            if (!interactive) psi.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
            using (var p = Process.Start(psi))
            {
                // Read in the background so the timeout also applies to a login window left open.
                var errText = new StringBuilder();
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (errText) errText.AppendLine(e.Data); };
                p.OutputDataReceived += (s, e) => { };
                p.BeginErrorReadLine();
                p.BeginOutputReadLine();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } throw new Exception("git " + args + " timed out"); }
                p.WaitForExit();
                lock (errText) err = errText.ToString();
                if (p.ExitCode != 0 && !ignoreErrors) Fail("git " + args + " -> " + err.Trim());
                return p.ExitCode;
            }
        }

        // ---------- local web server ----------

        void StartServer()
        {
            try
            {
                _http = new HttpListener();
                _http.Prefixes.Add("http://localhost:" + _localPort.Value + "/");
                _http.Start();
                new Thread(ServeLoop) { IsBackground = true, Name = "StellarMap HTTP" }.Start();
                Log.Msg("Local map: http://localhost:" + _localPort.Value + "/");
            }
            catch (Exception e) { Log.Warning("Local map server not started: " + e.Message); _http = null; }
        }

        void ServeLoop()
        {
            while (_http != null && _http.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _http.GetContext(); } catch { return; }
                try { Serve(ctx); }
                catch { }
                finally { try { ctx.Response.Close(); } catch { } }
            }
        }

        void Serve(HttpListenerContext ctx)
        {
            var res = ctx.Response;
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["Access-Control-Allow-Origin"] = "*";
            var path = ctx.Request.Url.AbsolutePath.TrimStart('/');
            if (path == "") path = "index.html";
            string file = path == "map.json" ? _mapFile : Path.GetFullPath(Path.Combine(_wwwDir, path));
            if (path != "map.json" && !file.StartsWith(Path.GetFullPath(_wwwDir), StringComparison.OrdinalIgnoreCase)) file = null;
            if (file == null || !File.Exists(file)) { res.StatusCode = 404; return; }
            res.ContentType = file.EndsWith(".json") ? "application/json; charset=utf-8"
                : file.EndsWith(".html") ? "text/html; charset=utf-8"
                : file.EndsWith(".js") ? "text/javascript" : "application/octet-stream";
            byte[] data;
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream()) { fs.CopyTo(ms); data = ms.ToArray(); }
            res.ContentLength64 = data.Length;
            res.OutputStream.Write(data, 0, data.Length);
        }
    }

    // Strips the save-file world down to the map format. The page understands both this format
    // and a raw world.json, so field names stay the same as in the save file.
    static class MapData
    {
        public static JObject Build(JObject world, string worldName, bool names)
        {
            var map = new JObject
            {
                ["format"] = "stellarmap/1",
                ["worldName"] = worldName,
                ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["worldTime"] = world["worldTime"],
                ["gameVersion"] = world["migrationMetadata"]?["gameVersion"],
                ["spaces"] = world["spaces"]
            };

            var players = new JArray();
            int n = 0;
            foreach (var p in world["players"] as JArray ?? new JArray())
            {
                n++;
                players.Add(new JObject
                {
                    ["id"] = p["id"],
                    ["name"] = names ? p["name"] : "Spieler " + n,
                    ["spaceId"] = p["spaceId"],
                    ["position"] = Vec(p["position"]),
                    ["rotation"] = Quat(p["rotation"]),
                    ["health"] = p["health"],
                    ["oxygen"] = p["oxygen"]
                });
            }
            map["players"] = players;

            var ships = new JArray();
            foreach (var s in world["ships"] as JArray ?? new JArray())
            {
                // Hull as flat ints: x, y, z, voxel type, colour (0xRRGGBB) per part.
                var hull = new JArray();
                foreach (var h in s["hullParts"] as JArray ?? new JArray())
                {
                    var c = h["FrontColor"];
                    int rgb = c == null ? 0xD9D9D9 : ((int)c["x"] << 16) | ((int)c["y"] << 8) | (int)c["z"];
                    hull.Add((int)h["x"]); hull.Add((int)h["y"]); hull.Add((int)h["z"]);
                    hull.Add((int)h["voxelPartTypeId"]); hull.Add(rgb);
                }
                var parts = 0;
                foreach (var prop in ((JObject)s).Properties())
                    if (prop.Name == "statefulFloatingParts" || prop.Name == "statefulHullParts")
                        parts += (prop.Value as JArray)?.Count ?? 0;
                // A ship has no name of its own; players name it through its beacon.
                var beacon = (s["beaconStates"] as JArray)?.Select(b => (string)b["name"]).FirstOrDefault(x => !string.IsNullOrEmpty(x));
                ships.Add(new JObject
                {
                    ["id"] = s["id"],
                    ["name"] = beacon,
                    ["spaceId"] = s["spaceId"],
                    ["relativeSpaceId"] = s["relativeSpaceId"],
                    ["position"] = Vec(s["position"]),
                    ["rotation"] = Quat(s["rotation"]),
                    ["velocity"] = Vec(s["velocity"]),
                    ["rooms"] = (s["rooms"] as JArray)?.Count ?? 0,
                    ["parts"] = parts,
                    ["hull"] = hull
                });
            }
            map["ships"] = ships;
            return map;
        }

        static JToken Vec(JToken v) => v == null ? null : new JObject { ["x"] = v["x"], ["y"] = v["y"], ["z"] = v["z"] };
        static JToken Quat(JToken q) => q == null ? null : new JObject { ["x"] = q["x"], ["y"] = q["y"], ["z"] = q["z"], ["w"] = q["w"] };

        public static bool SameContent(string a, string b)
        {
            // generatedUtc and worldTime always change; everything after them is the content.
            int ia = a.IndexOf("\"spaces\"", StringComparison.Ordinal), ib = b.IndexOf("\"spaces\"", StringComparison.Ordinal);
            return ia > 0 && ib > 0 && string.CompareOrdinal(a, ia, b, ib, Math.Max(a.Length - ia, b.Length - ib)) == 0;
        }
    }
}
