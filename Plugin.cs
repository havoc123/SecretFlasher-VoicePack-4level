// Plugin.cs — IL2CPP-friendly, robust member resolution, no Il2CppInterop, no Resources.*, deterministic timing
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using BepInEx;
using BepInEx.Unity.IL2CPP;   // BasePlugin (BepInEx 6 IL2CPP)
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;
using System.Runtime.CompilerServices;
using System.Linq;

[BepInPlugin("herpderp135.secretflasher.voicepack", "SecretFlasher Voice Pack", "0.6.0")]
public class VoicePackPlugin : BasePlugin
{
    public static VoicePackPlugin Instance { get; private set; }
    public static ManualLogSource L;

    // ---------- unity bits ----------
    private GameObject _host;
    private AudioSource _src;

    // ---------- audio cache & loading ----------
    private readonly Dictionary<string, List<AudioClip>> _clips = new Dictionary<string, List<AudioClip>> {
        { "start",  new List<AudioClip>() },
        { "loop",   new List<AudioClip>() },
        { "climax", new List<AudioClip>() },
    };

    private bool _preloadQueued = false;
    private readonly Queue<(string path, string cat)> _queue = new Queue<(string, string)>();

    private struct Pending
    {
        public string path, cat;
        public UnityWebRequest req;
    }
    private readonly List<Pending> _inflight = new List<Pending>();
    private const int MAX_INFLIGHT = 3;

    private readonly System.Random _rng = new System.Random();
    private readonly HashSet<string> _warnedEmptyCats = new HashSet<string>();
    private readonly Dictionary<string, float> _lastReloadAttemptAt = new Dictionary<string, float> {
        {"start", -999f}, {"loop", -999f}, {"climax", -999f}
    };

    // ---------- ecstasy state machine ----------
    private const float START_THR   = 0.20f;
    private const float CLIMAX_THR  = 0.95f;
    private const float RESET_THR   = 0.05f;
    private const float LOOP_EVERY  = 1.20f;

    private float _prev = 0f;
    private bool  _active = false;
    private bool  _firedClimax = false;
    private float _nextLoopAt = -999f;

    private float _lastStartAt  = -999f;
    private float _lastLoopAt   = -999f;
    private float _lastClimaxAt = -999f;

    public float CurrentPlayerEcstasy { get; private set; } = 0f;
    private bool _isEcstasyMotion = false;

    // ---------- reflection handles ----------
    private const string GameStateDataTypeName  = "ExposureUnnoticed2.Scripts.InGame.GameStateData";
    private const string PreferredMemberName    = "PlayerEcstasy"; // what ILSpy showed

    private Type _gsdType;
    private object _gsdInstance;

    // We resolve a getter delegate once, then just call it
    private bool _resolverTried = false;
    private Func<object, float> _ecstasyGetter = null;
    private Func<object, float> _controllerEcstasyGetter = null;
    private bool _controllerGetterTried = false;
    private float _lastDirectFeedAt = -999f;
    private bool _loggedControllerInfo = false;
    private Func<float> _controllerEcstasyGetter2 = null;

    // throttled warning (so no spam if object not in scene yet)
    private float _lastNotFoundLogAt = -999f;
    private void WarnNotFoundThrottled(string msg)
    {
        var now = Time.realtimeSinceStartup;
        if (now - _lastNotFoundLogAt >= 2.0f) { L.LogWarning(msg); _lastNotFoundLogAt = now; }
    }

    public override void Load()
    {
        Instance = this;
        L = Log;

        L.LogInfo("[VoicePack] Load()");

        _host = new GameObject("VoicePack_Host");
        GameObject.DontDestroyOnLoad(_host);

        _src = _host.AddComponent<AudioSource>();
        var pPlay = typeof(AudioSource).GetProperty("playOnAwake", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (pPlay != null) pPlay.SetValue(_src, false, null);
        var pBlend = typeof(AudioSource).GetProperty("spatialBlend", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (pBlend != null) pBlend.SetValue(_src, 0f, null);

        // harmony
        var harmony = new Harmony("herpderp135.secretflasher.voicepack");
        harmony.PatchAll();
        TryPatchOptionalHarmony(harmony);
        L.LogInfo("[VoicePack] Harmony patches applied.");
    }

    private void EnsureAudioSource()
    {
        try
        {
            if (_host == null || !_host)
            {
                _host = new GameObject("VoicePack_Host");
                GameObject.DontDestroyOnLoad(_host);
            }
            if (_src == null || !_src)
            {
                _src = _host.GetComponent<AudioSource>();
                if (_src == null) _src = _host.AddComponent<AudioSource>();
                var pPlay = typeof(AudioSource).GetProperty("playOnAwake", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pPlay != null) pPlay.SetValue(_src, false, null);
                var pBlend = typeof(AudioSource).GetProperty("spatialBlend", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pBlend != null) pBlend.SetValue(_src, 0f, null);
            }
        }
        catch { }
    }

    // ========================= TICK (called from OnUpdate postfix) =========================
    internal void Tick(object controller = null)
    {
        // Always service audio preload/loads first
        EnsurePreloadQueued();
        PumpLoads();
        EnsureAudioSource();

        if (controller != null)
        {
            if (!_loggedControllerInfo)
            {
                _loggedControllerInfo = true;
                try { L.LogInfo("[VoicePack] Controller: " + controller.GetType().FullName); } catch { }
            }
            if (_controllerEcstasyGetter == null && _controllerEcstasyGetter2 == null && !_controllerGetterTried)
                TryResolveControllerEcstasyGetter(controller.GetType());

            if (_controllerEcstasyGetter != null)
            {
                try
                {
                    var ecCtrl = _controllerEcstasyGetter(controller);
                    CurrentPlayerEcstasy = ecCtrl;
                    DriveState(ecCtrl);
                    return; // ok to return now; audio preload already serviced above
                }
                catch { /* fall through to other methods */ }
            }

            if (_controllerEcstasyGetter2 == null)
                TryFuzzyFindEcstasyFromGraph(controller);

            if (_controllerEcstasyGetter2 != null)
            {
                try
                {
                    var ecCtrl2 = _controllerEcstasyGetter2();
                    CurrentPlayerEcstasy = ecCtrl2;
                    DriveState(ecCtrl2);
                    return; // ok to return now; audio preload already serviced above
                }
                catch { _controllerEcstasyGetter2 = null; }
            }

            if (_gsdInstance == null)
            {
                if (!TryResolveFromController(controller))
                    TryResolveFromGraph(controller);
            }
        }

        // Use direct feed if recently updated
        if (_lastDirectFeedAt > 0 && (Time.realtimeSinceStartup - _lastDirectFeedAt) < 2.0f)
        {
            DriveState(CurrentPlayerEcstasy);
            return;
        }

        if (!EnsureGsdInstance()) return;

        if (_ecstasyGetter == null && !_resolverTried)
            TryResolveEcstasyGetter();

        if (_ecstasyGetter == null)
            return; // still waiting on a resolvable member

        float ec;
        try { ec = _ecstasyGetter(_gsdInstance); }
        catch (Exception e)
        {
            L.LogWarning("[VoicePack] Reading ecstasy failed: " + e.Message);
            return;
        }

        CurrentPlayerEcstasy = ec;
        DriveState(ec);
    }

    private void TryResolveControllerEcstasyGetter(Type controllerType)
    {
        _controllerGetterTried = true;
        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

            // Preferred: property PlayerEcstasy
            var p = controllerType.GetProperty(PreferredMemberName, flags);
            if (p != null && (p.PropertyType == typeof(float) || p.PropertyType == typeof(double)))
            {
                var gm = p.GetGetMethod(true);
                if (gm != null)
                {
                    _controllerEcstasyGetter = (obj) => Convert.ToSingle(gm.Invoke(obj, null));
                    L.LogInfo("[VoicePack] Bound controller PROPERTY: " + controllerType.FullName + "." + PreferredMemberName);
                    return;
                }
            }

            // Field PlayerEcstasy
            var f = controllerType.GetField(PreferredMemberName, flags);
            if (f != null && (f.FieldType == typeof(float) || f.FieldType == typeof(double)))
            {
                _controllerEcstasyGetter = (obj) => Convert.ToSingle(f.GetValue(obj));
                L.LogInfo("[VoicePack] Bound controller FIELD: " + controllerType.FullName + "." + PreferredMemberName);
                return;
            }

            // Fuzzy: any property containing Ecstasy
            foreach (var prop in controllerType.GetProperties(flags))
            {
                if (prop.PropertyType != typeof(float) && prop.PropertyType != typeof(double)) continue;
                if (prop.Name.IndexOf("Ecstasy", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var gm = prop.GetGetMethod(true);
                if (gm == null) continue;
                _controllerEcstasyGetter = (obj) => Convert.ToSingle(gm.Invoke(obj, null));
                L.LogWarning("[VoicePack] Fuzzy-bound controller PROPERTY: " + controllerType.FullName + "." + prop.Name);
                return;
            }

            // Fuzzy: any field containing Ecstasy
            foreach (var fld in controllerType.GetFields(flags))
            {
                if (fld.FieldType != typeof(float) && fld.FieldType != typeof(double)) continue;
                if (fld.Name.IndexOf("Ecstasy", StringComparison.OrdinalIgnoreCase) < 0) continue;
                _controllerEcstasyGetter = (obj) => Convert.ToSingle(fld.GetValue(obj));
                L.LogWarning("[VoicePack] Fuzzy-bound controller FIELD: " + controllerType.FullName + "." + fld.Name);
                return;
            }
        }
        catch { }
    }

    private void TryFuzzyFindEcstasyFromGraph(object root)
    {
        try
        {
            var visited = new HashSet<object>(new RefEq());
            var q = new Queue<object>();
            q.Enqueue(root);
            int steps = 0;

            while (q.Count > 0 && steps < 1024 && _controllerEcstasyGetter2 == null)
            {
                steps++;
                var cur = q.Dequeue();
                if (cur == null) continue;
                if (visited.Contains(cur)) continue;
                visited.Add(cur);

                var t = cur.GetType();
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                foreach (var p in t.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    var gm = p.GetGetMethod(true);
                    if (gm == null) continue;
                    object val = null;
                    try { val = gm.Invoke(cur, null); } catch { }
                    var pt = p.PropertyType;
                    if (pt == typeof(float) || pt == typeof(double))
                    {
                        if (p.Name.IndexOf("Ecstasy", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (_controllerEcstasyGetter2 == null)
                                _controllerEcstasyGetter2 = () => Convert.ToSingle(gm.Invoke(cur, null));
                            // log only once to avoid spam
                            if (_controllerEcstasyGetter2 != null && !_loggedControllerInfo)
                            {
                                // don't set _loggedControllerInfo here; keep it specific to controller info line
                            }
                            L.LogWarning("[VoicePack] Fuzzy-bound controller PATH PROPERTY: " + t.FullName + "." + p.Name);
                            return;
                        }
                    }
                    if (val != null && !IsPrimitiveLike(pt)) q.Enqueue(val);
                }

                foreach (var f in t.GetFields(flags))
                {
                    object val = null;
                    try { val = f.GetValue(cur); } catch { }
                    var ft = f.FieldType;
                    if (ft == typeof(float) || ft == typeof(double))
                    {
                        if (f.Name.IndexOf("Ecstasy", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (_controllerEcstasyGetter2 == null)
                                _controllerEcstasyGetter2 = () => Convert.ToSingle(f.GetValue(cur));
                            L.LogWarning("[VoicePack] Fuzzy-bound controller PATH FIELD: " + t.FullName + "." + f.Name);
                            return;
                        }
                    }
                    if (val != null && !IsPrimitiveLike(ft)) q.Enqueue(val);
                }
            }
        }
        catch { }
    }

    // ========================= AUDIO PRELOAD (no coroutines) =========================
    private void EnsurePreloadQueued()
    {
        if (_preloadQueued) return;
        _preloadQueued = true;

        TryQueueAudioLoads();

        L.LogInfo($"[VoicePack] Preload queued: start={CountQueued("start")}, loop={CountQueued("loop")}, climax={CountQueued("climax")} ");
    }

    private int CountQueued(string cat)
    {
        int n = 0;
        foreach (var q in _queue) if (q.cat == cat) n++;
        return n;
    }

    private void TryQueueAudioLoads()
    {
        try
        {
            var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx", "plugins", "SecretFlasher.VoicePack", "audio");
            if (!Directory.Exists(baseDir)) { L.LogWarning($"[VoicePack] Missing folder: {baseDir}"); return; }
            foreach (var cat in new[] { "start", "loop", "climax" })
            {
                var dir = Path.Combine(baseDir, cat);
                if (!Directory.Exists(dir)) { L.LogWarning($"[VoicePack] Missing folder: {dir}"); continue; }
                var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly);
                foreach (var path in files)
                {
                    var ext = Path.GetExtension(path)?.ToLowerInvariant();
                    if (ext == ".ogg" || ext == ".wav")
                        _queue.Enqueue((path, cat));
                }
            }
        }
        catch (Exception ex)
        {
            L.LogWarning($"[VoicePack] Audio load queue failed: {ex.Message}");
        }
    }

    private void PumpLoads()
    {
        // start new requests
        while (_inflight.Count < MAX_INFLIGHT && _queue.Count > 0)
        {
            var job = _queue.Dequeue();
            var fullPath = Path.IsPathRooted(job.path) ? job.path : Path.GetFullPath(job.path);
            var url = "file:///" + fullPath.Replace('\\', '/');
            var audioType = GetAudioTypeForExtension(Path.GetExtension(fullPath));
            var req = UnityWebRequestMultimedia.GetAudioClip(url, audioType);
            req.SendWebRequest(); // we'll poll isDone below
            _inflight.Add(new Pending { path = fullPath, cat = job.cat, req = req });
        }

        // poll inflight
        for (int i = _inflight.Count - 1; i >= 0; i--)
        {
            var p = _inflight[i];
            if (!p.req.isDone) continue;

            if (p.req.result != UnityWebRequest.Result.Success)
            {
                L.LogWarning($"[VoicePack] Load fail {p.cat}/{Path.GetFileName(p.path)} :: {p.req.error}");
            }
            else
            {
                var clip = DownloadHandlerAudioClip.GetContent(p.req);
                if (clip != null)
                {
                    clip.name = Path.GetFileName(p.path);
                    try { UnityEngine.Object.DontDestroyOnLoad(clip); } catch { }
                    try { clip.hideFlags = HideFlags.DontUnloadUnusedAsset; } catch { }
                    _clips[p.cat].Add(clip);
                    L.LogInfo($"[VoicePack] Loaded audio: {p.cat}/{clip.name} len={clip.length:F2}s ch={clip.channels} hz={clip.frequency}");
                }
            }

            p.req.Dispose();
            _inflight.RemoveAt(i);
        }
    }

    private static AudioType GetAudioTypeForExtension(string ext)
    {
        var e = (ext ?? string.Empty).ToLowerInvariant();
        if (e == ".ogg") return AudioType.OGGVORBIS;
        if (e == ".wav") return AudioType.WAV;
        return AudioType.UNKNOWN;
    }

    // ========================= FIND GameStateData safely (no Resources.*) =========================
    private static UnityEngine.Object FindFirstInstanceOfType(Type t)
    {
        // 1) Object.FindObjectOfType(Type, bool includeInactive)
        var m = typeof(UnityEngine.Object).GetMethod("FindObjectOfType", new Type[] { typeof(Type), typeof(bool) });
        if (m != null)
        {
            try {
                var inst = (UnityEngine.Object)m.Invoke(null, new object[] { t, true });
                if (inst) return inst;
            } catch { }
        }

        // 2) Object.FindObjectOfType(Type)
        m = typeof(UnityEngine.Object).GetMethod("FindObjectOfType", new Type[] { typeof(Type) });
        if (m != null)
        {
            try {
                var inst = (UnityEngine.Object)m.Invoke(null, new object[] { t });
                if (inst) return inst;
            } catch { }
        }

        // 3) Object.FindObjectsOfType(Type, bool includeInactive) -> Object[]
        m = typeof(UnityEngine.Object).GetMethod("FindObjectsOfType", new Type[] { typeof(Type), typeof(bool) });
        if (m != null)
        {
            try {
                var arr = m.Invoke(null, new object[] { t, true }) as UnityEngine.Object[];
                if (arr != null && arr.Length > 0 && arr[0]) return arr[0];
            } catch { }
        }

        // 4) Object.FindObjectsOfType(Type) -> Object[]
        m = typeof(UnityEngine.Object).GetMethod("FindObjectsOfType", new Type[] { typeof(Type) });
        if (m != null)
        {
            try {
                var arr = m.Invoke(null, new object[] { t }) as UnityEngine.Object[];
                if (arr != null && arr.Length > 0 && arr[0]) return arr[0];
            } catch { }
        }

        return null;
    }

    private bool EnsureGsdInstance()
    {
        if (_gsdType == null)
        {
            _gsdType = ResolveGsdType();
            if (_gsdType == null) { WarnNotFoundThrottled("[VoicePack] Type not found: " + GameStateDataTypeName); return false; }
        }

        if (_gsdInstance == null)
        {
            // 1) Direct scene object
            var found = FindFirstInstanceOfType(_gsdType);
            _gsdInstance = found;

            // 2) Static singleton on type
            if (_gsdInstance == null)
                TryResolveSingletonOnType();

            // 3) Scene traversal (all loaded scenes, roots and components)
            if (_gsdInstance == null)
                TryResolveFromSceneGraph();

            // 3) Global static singletons (scan app domain)
            if (_gsdInstance == null)
                TryResolveSingletonGlobal();

            if (_gsdInstance == null)
            {
                WarnNotFoundThrottled("[VoicePack] Waiting for GameStateData instance...");
                return false;
            }
            else
            {
                L.LogInfo("[VoicePack] Found GameStateData instance.");
            }
        }

        return true;
    }

    private bool TryResolveFromController(object controller)
    {
        try
        {
            var ct = controller.GetType();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            // 1) Exact property name
            var p = ct.GetProperty("GameStateData", flags);
            if (p != null)
            {
                var val = p.GetValue(controller, null);
                if (val != null)
                {
                    _gsdInstance = val;
                    if (_gsdType == null) _gsdType = val.GetType();
                    L.LogInfo("[VoicePack] Found GameStateData instance.");
                    return true;
                }
            }

            // 2) Any property whose type/name mentions GameStateData
            foreach (var prop in ct.GetProperties(flags))
            {
                var pt = prop.PropertyType;
                if (pt == null) continue;
                if (pt.Name.IndexOf("GameStateData", StringComparison.OrdinalIgnoreCase) >= 0 || prop.Name.IndexOf("GameStateData", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    object val = null;
                    try { val = prop.GetValue(controller, null); } catch { }
                    if (val != null)
                    {
                        _gsdInstance = val;
                        if (_gsdType == null) _gsdType = val.GetType();
                        L.LogInfo("[VoicePack] Found GameStateData instance.");
                        return true;
                    }
                }
            }

            // 3) Any field whose type/name mentions GameStateData
            foreach (var fld in ct.GetFields(flags))
            {
                var ft = fld.FieldType;
                if (ft == null) continue;
                if (ft.Name.IndexOf("GameStateData", StringComparison.OrdinalIgnoreCase) >= 0 || fld.Name.IndexOf("GameStateData", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    object val = null;
                    try { val = fld.GetValue(controller); } catch { }
                    if (val != null)
                    {
                        _gsdInstance = val;
                        if (_gsdType == null) _gsdType = val.GetType();
                        L.LogInfo("[VoicePack] Found GameStateData instance.");
                        return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }

    private void TryResolveFromGraph(object root)
    {
        try
        {
            var visited = new HashSet<object>(new RefEq());
            var q = new Queue<object>();
            q.Enqueue(root);
            int steps = 0;

            while (q.Count > 0 && steps < 512 && _gsdInstance == null)
            {
                steps++;
                var cur = q.Dequeue();
                if (cur == null) continue;
                if (visited.Contains(cur)) continue;
                visited.Add(cur);

                var t = cur.GetType();
                if (t.FullName != null && t.FullName.IndexOf("GameStateData", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _gsdInstance = cur;
                    if (_gsdType == null) _gsdType = t;
                    L.LogInfo("[VoicePack] Found GameStateData instance.");
                    break;
                }

                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                // properties
                foreach (var p in t.GetProperties(flags))
                {
                    if (p.GetIndexParameters().Length != 0) continue;
                    var gm = p.GetGetMethod(true);
                    if (gm == null) continue;
                    object val = null;
                    try { val = gm.Invoke(cur, null); } catch { }
                    if (val == null) continue;
                    var pt = p.PropertyType;
                    if (pt != null && (pt.FullName?.IndexOf("GameStateData", StringComparison.OrdinalIgnoreCase) >= 0 || p.Name.IndexOf("GameStateData", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        _gsdInstance = val;
                        if (_gsdType == null) _gsdType = val.GetType();
                        L.LogInfo("[VoicePack] Found GameStateData instance.");
                        return;
                    }
                    if (!IsPrimitiveLike(pt)) q.Enqueue(val);
                }

                // fields
                foreach (var f in t.GetFields(flags))
                {
                    object val = null;
                    try { val = f.GetValue(cur); } catch { }
                    if (val == null) continue;
                    var ft = f.FieldType;
                    if (ft != null && (ft.FullName?.IndexOf("GameStateData", StringComparison.OrdinalIgnoreCase) >= 0 || f.Name.IndexOf("GameStateData", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        _gsdInstance = val;
                        if (_gsdType == null) _gsdType = val.GetType();
                        L.LogInfo("[VoicePack] Found GameStateData instance.");
                        return;
                    }
                    if (!IsPrimitiveLike(ft)) q.Enqueue(val);
                }
            }
        }
        catch { }
    }

    private static bool IsPrimitiveLike(Type t)
    {
        if (t == null) return true;
        if (t.IsPrimitive) return true;
        if (t == typeof(string)) return true;
        return false;
    }

    private Type ResolveGsdType()
    {
        var t = AccessTools.TypeByName(GameStateDataTypeName);
        if (t != null) return t;
        // try assembly-qualified name
        var aqn = GameStateDataTypeName + ", Assembly-CSharp";
        try { t = Type.GetType(aqn, false); } catch { }
        if (t != null) return t;
        return FuzzyFindGsdTypeInAppDomain();
    }

    private void TryResolveSingletonOnType()
    {
        if (_gsdType == null) return;
        try
        {
            var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            // Common singleton patterns
            var names = new string[] { "Instance", "instance", "I", "s_instance", "_instance" };
            foreach (var n in names)
            {
                var p = _gsdType.GetProperty(n, flags);
                if (p != null)
                {
                    var gm = p.GetGetMethod(true);
                    if (gm != null)
                    {
                        var val = gm.Invoke(null, null);
                        if (val != null) { _gsdInstance = val; return; }
                    }
                }
                var f = _gsdType.GetField(n, flags);
                if (f != null)
                {
                    var val = f.GetValue(null);
                    if (val != null) { _gsdInstance = val; return; }
                }
            }
        }
        catch { }
    }

    private void TryResolveFromSceneGraph()
    {
        try
        {
            var smType = AccessTools.TypeByName("UnityEngine.SceneManagement.SceneManager");
            if (smType == null) return;
            var pCount = smType.GetProperty("sceneCount", BindingFlags.Static | BindingFlags.Public);
            var mGetAt = smType.GetMethod("GetSceneAt", new Type[] { typeof(int) });
            if (pCount == null || mGetAt == null) return;
            var countObj = pCount.GetGetMethod().Invoke(null, null);
            int count = 0;
            try { count = Convert.ToInt32(countObj); } catch { }
            if (count <= 0) return;

            var sceneType = mGetAt.ReturnType; // UnityEngine.SceneManagement.Scene
            var mGetRoots = sceneType.GetMethod("GetRootGameObjects", BindingFlags.Instance | BindingFlags.Public);
            if (mGetRoots == null) return;

            for (int i = 0; i < count && _gsdInstance == null; i++)
            {
                var scene = mGetAt.Invoke(null, new object[] { i });
                var roots = mGetRoots.Invoke(scene, null) as GameObject[];
                if (roots == null) continue;
                foreach (var go in roots)
                {
                    if (go == null) continue;
                    if (TryFindGsdOnGameObject(go)) return;
                }
            }
        }
        catch { }
    }

    private bool TryFindGsdOnGameObject(GameObject go)
    {
        try
        {
            var comps = go.GetComponentsInChildren(typeof(Component), true) as Component[];
            if (comps == null) return false;
            foreach (var c in comps)
            {
                if (c == null) continue;
                var ct = c.GetType();
                var name = ct.FullName ?? ct.Name;
                if (name != null && name.IndexOf("GameStateData", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _gsdInstance = c;
                    if (_gsdType == null) _gsdType = ct;
                    L.LogInfo("[VoicePack] Found GameStateData instance.");
                    return true;
                }
            }
        }
        catch { }
        return false;
    }

    private Type FuzzyFindGsdTypeInAppDomain()
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types = null;
                try { types = asm.GetTypes(); } catch { }
                if (types == null) continue;
                foreach (var tp in types)
                {
                    var name = tp.FullName ?? tp.Name;
                    if (name == null) continue;
                    if (name.IndexOf("GameStateData", StringComparison.OrdinalIgnoreCase) >= 0)
                        return tp;
                }
            }
        }
        catch { }
        return null;
    }

    private void TryResolveSingletonGlobal()
    {
        if (_gsdType == null) return;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types = null;
                try { types = asm.GetTypes(); } catch { }
                if (types == null) continue;
                foreach (var tp in types)
                {
                    var flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                    foreach (var p in tp.GetProperties(flags))
                    {
                        if (p.PropertyType != _gsdType) continue;
                        var gm = p.GetGetMethod(true);
                        if (gm == null) continue;
                        object val = null;
                        try { val = gm.Invoke(null, null); } catch { }
                        if (val != null) { _gsdInstance = val; return; }
                    }
                    foreach (var f in tp.GetFields(flags))
                    {
                        if (f.FieldType != _gsdType) continue;
                        object val = null;
                        try { val = f.GetValue(null); } catch { }
                        if (val != null) { _gsdInstance = val; return; }
                    }
                }
            }
        }
        catch { }
    }

    // ========================= robust member resolution =========================
    private void TryResolveEcstasyGetter()
    {
        _resolverTried = true;
        if (_gsdType == null) return;

        // 1) Preferred property getter: PlayerEcstasy { get; }
        var prop = _gsdType.GetProperty(PreferredMemberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null && (prop.PropertyType == typeof(float) || prop.PropertyType == typeof(double)))
        {
            var getM = prop.GetGetMethod(true);
            if (getM != null)
            {
                _ecstasyGetter = (obj) => Convert.ToSingle(getM.Invoke(obj, null));
                L.LogInfo("[VoicePack] Bound to PROPERTY: " + _gsdType.FullName + "." + PreferredMemberName);
                return;
            }
        }

        // 2) Preferred field: public float PlayerEcstasy;
        var fld = _gsdType.GetField(PreferredMemberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (fld != null && (fld.FieldType == typeof(float) || fld.FieldType == typeof(double)))
        {
            _ecstasyGetter = (obj) => Convert.ToSingle(fld.GetValue(obj));
            L.LogInfo("[VoicePack] Bound to FIELD: " + _gsdType.FullName + "." + PreferredMemberName);
            return;
        }

        // 3) Fuzzy search: any float/double property containing "ecstasy"
        var bestProp = FindFuzzyProp(_gsdType, "ecstasy");
        if (bestProp != null)
        {
            var gm = bestProp.GetGetMethod(true);
            if (gm != null)
            {
                _ecstasyGetter = (obj) => Convert.ToSingle(gm.Invoke(obj, null));
                L.LogWarning("[VoicePack] Fuzzy-bound to PROPERTY: " + _gsdType.FullName + "." + bestProp.Name);
                return;
            }
        }

        // 4) Fuzzy search: any float/double field containing "ecstasy"
        var bestField = FindFuzzyField(_gsdType, "ecstasy");
        if (bestField != null)
        {
            _ecstasyGetter = (obj) => Convert.ToSingle(bestField.GetValue(obj));
            L.LogWarning("[VoicePack] Fuzzy-bound to FIELD: " + _gsdType.FullName + "." + bestField.Name);
            return;
        }

        // 5) Nothing resolved — list candidates once to help you adjust names
        DumpMembersOnce(_gsdType);
        WarnNotFoundThrottled("[VoicePack] Could not resolve an ecstasy member. Update PreferredMemberName or use the fuzzy-suggested one from the dump above.");
    }

    private static PropertyInfo FindFuzzyProp(Type t, string contains)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        foreach (var p in t.GetProperties(flags))
        {
            if (p.PropertyType != typeof(float) && p.PropertyType != typeof(double)) continue;
            if (p.GetGetMethod(true) == null) continue;
            if (p.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                return p;
        }
        return null;
    }

    private static FieldInfo FindFuzzyField(Type t, string contains)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        foreach (var f in t.GetFields(flags))
        {
            if (f.FieldType != typeof(float) && f.FieldType != typeof(double)) continue;
            if (f.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                return f;
        }
        return null;
    }

    private bool _dumpedOnce = false;
    private void DumpMembersOnce(Type t)
    {
        if (_dumpedOnce) return;
        _dumpedOnce = true;

        try
        {
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

            L.LogWarning($"[VoicePack] ---- {t.FullName} FLOAT-like members ----");

            foreach (var p in t.GetProperties(flags))
            {
                if (p.PropertyType == typeof(float) || p.PropertyType == typeof(double))
                {
                    L.LogWarning($"[VoicePack] PROP: {p.PropertyType.Name} {p.Name} (getter? {(p.GetGetMethod(true)!=null)})");
                }
            }
            foreach (var f in t.GetFields(flags))
            {
                if (f.FieldType == typeof(float) || f.FieldType == typeof(double))
                {
                    L.LogWarning($"[VoicePack] FIELD: {f.FieldType.Name} {f.Name}");
                }
            }
            L.LogWarning($"[VoicePack] ---- end dump ----");
        }
        catch (Exception ex)
        {
            L.LogWarning("[VoicePack] Dump failed: " + ex.Message);
        }
    }

    // ========================= state machine =========================
    private void DriveState(float ec)
    {
        var now = Time.time;

        // START: first crossing
        if (_prev < START_THR && ec >= START_THR && !_active)
        {
            _active = true;
            _firedClimax = false;
            _nextLoopAt = now + (LOOP_EVERY * 0.5f) + UnityEngine.Random.Range(-0.2f, 0.2f);

            L.LogInfo($"[VoicePack] START (ec={ec:F2})");
            PlayFrom("start", ref _lastStartAt, 0.75f);
        }

        // LOOP: cadence while rising
        if (_active && ec > _prev && now >= _nextLoopAt)
        {
            PlayFrom("loop", ref _lastLoopAt, 0.90f);
            _nextLoopAt = now + LOOP_EVERY + UnityEngine.Random.Range(-0.2f, 0.2f);
        }

        // CLIMAX: once on rising edge
        if (!_firedClimax && _prev < CLIMAX_THR && ec >= CLIMAX_THR)
        {
            L.LogInfo($"[VoicePack] CLIMAX (ec={ec:F2})");
            PlayFrom("climax", ref _lastClimaxAt, 2.50f);
            _firedClimax = true;
        }

        // RESET
        if (ec <= RESET_THR)
        {
            if (_active || _firedClimax) L.LogInfo("[VoicePack] RESET");
            _active = false;
            _firedClimax = false;
            _nextLoopAt = -999f;
        }

        _prev = ec;
    }

    // ========================= audio play =========================
    private void PlayFrom(string cat, ref float lastAt, float cooldownSec)
    {
        var list = _clips[cat];
        if (list == null || list.Count == 0)
        {
            if (!_warnedEmptyCats.Contains(cat))
            {
                L.LogWarning($"[VoicePack] No clips in '{cat}'");
                _warnedEmptyCats.Add(cat);
            }
            MaybeRequeueCategory(cat);
            return;
        }

        // Filter out destroyed clips
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i]) list.RemoveAt(i);
        }

        if (list.Count == 0)
        {
            if (!_warnedEmptyCats.Contains(cat))
            {
                L.LogWarning($"[VoicePack] All clips destroyed in '{cat}'");
                _warnedEmptyCats.Add(cat);
            }
            MaybeRequeueCategory(cat);
            return;
        }

        // (cooldown cadence handled by DriveState; we just stamp time)
        var idx = _rng.Next(0, list.Count);
        var clip = list[idx];
        if (clip == null)
        {
            if (!_warnedEmptyCats.Contains(cat)) L.LogWarning($"[VoicePack] Null clip in '{cat}'");
            MaybeRequeueCategory(cat);
            return;
        }
        EnsureAudioSource();
        if (_src == null || !_src)
        {
            L.LogWarning("[VoicePack] AudioSource missing; skipping play");
            return;
        }
        try { _src.PlayOneShot(clip); } catch (Exception ex) { L.LogWarning("[VoicePack] PlayOneShot failed: " + ex.Message); }
        lastAt = Time.time;
    }

    private void MaybeRequeueCategory(string cat)
    {
        try
        {
            var now = Time.realtimeSinceStartup;
            if (!_lastReloadAttemptAt.ContainsKey(cat)) _lastReloadAttemptAt[cat] = -999f;
            if (now - _lastReloadAttemptAt[cat] < 3.0f) return;
            _lastReloadAttemptAt[cat] = now;

            var baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BepInEx", "plugins", "SecretFlasher.VoicePack", "audio", cat);
            if (!Directory.Exists(baseDir)) return;
            var files = Directory.GetFiles(baseDir, "*.*", SearchOption.TopDirectoryOnly);
            foreach (var path in files)
            {
                var ext = Path.GetExtension(path)?.ToLowerInvariant();
                if (ext == ".ogg" || ext == ".wav")
                    _queue.Enqueue((path, cat));
            }
            L.LogInfo($"[VoicePack] Re-queued audio for '{cat}'");
        }
        catch { }
    }

    private void TryPatchOptionalHarmony(Harmony harmony)
    {
        try
        {
            // Optional: PlayerAnimationManager.IsEcstasyMotion setter
            var pamType = AccessTools.TypeByName("ExposureUnnoticed2.Object3D.Player.Scripts.PlayerAnimationManager");
            if (pamType != null)
            {
                var prop = pamType.GetProperty("IsEcstasyMotion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var setM = prop != null ? prop.GetSetMethod(true) : null;
                if (setM != null)
                {
                    var post = new HarmonyMethod(typeof(OptionalHooks), nameof(OptionalHooks.PlayerAnimationManager_IsEcstasyMotion_Setter_Postfix));
                    harmony.Patch(setM, postfix: post);
                }
            }

            // Optional: GameStateData.set_PlayerEcstasy(float/double)
            var gsdType = _gsdType ?? AccessTools.TypeByName(GameStateDataTypeName);
            if (gsdType != null)
            {
                var prop = gsdType.GetProperty(PreferredMemberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var setM = prop != null ? prop.GetSetMethod(true) : null;
                if (setM != null)
                {
                    if (prop.PropertyType == typeof(float))
                    {
                        var post = new HarmonyMethod(typeof(OptionalHooks), nameof(OptionalHooks.GameStateData_PlayerEcstasy_Setter_PostfixFloat));
                        harmony.Patch(setM, postfix: post);
                    }
                    else if (prop.PropertyType == typeof(double))
                    {
                        var post = new HarmonyMethod(typeof(OptionalHooks), nameof(OptionalHooks.GameStateData_PlayerEcstasy_Setter_PostfixDouble));
                        harmony.Patch(setM, postfix: post);
                    }
                }
            }
        }
        catch (Exception)
        {
            // non-fatal: optional hooks
        }
    }

    internal void SetIsEcstasyMotion(bool on)
    {
        _isEcstasyMotion = on;
    }

    internal void SetEcstasy(float value)
    {
        CurrentPlayerEcstasy = value;
        _lastDirectFeedAt = Time.realtimeSinceStartup;
    }

    private sealed class RefEq : IEqualityComparer<object>
    {
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
    }
}

// ========================= harmony patch =========================
[HarmonyPatch]
internal static class Patch_PlayerEcstasyController_OnUpdate
{
    // ExposureUnnoticed2.Object3D.Player.Scripts.PlayerEcstasyController.OnUpdate()
    static MethodBase TargetMethod()
    {
        var t = AccessTools.TypeByName("ExposureUnnoticed2.Object3D.Player.Scripts.PlayerEcstasyController");
        return t != null ? AccessTools.Method(t, "OnUpdate", Array.Empty<Type>()) : null;
    }

    static void Postfix(object __instance)
    {
        VoicePackPlugin.Instance?.Tick(__instance);
    }
}

// ========================= optional hook receivers =========================
internal static class OptionalHooks
{
    // PlayerAnimationManager.IsEcstasyMotion setter postfix
    public static void PlayerAnimationManager_IsEcstasyMotion_Setter_Postfix(object __instance, bool __0)
    {
        VoicePackPlugin.Instance?.SetIsEcstasyMotion(__0);
    }

    // GameStateData.set_PlayerEcstasy(float)
    public static void GameStateData_PlayerEcstasy_Setter_PostfixFloat(object __instance, float __0)
    {
        if (VoicePackPlugin.Instance != null)
        {
            VoicePackPlugin.Instance.SetEcstasy(__0);
        }
    }

    // GameStateData.set_PlayerEcstasy(double)
    public static void GameStateData_PlayerEcstasy_Setter_PostfixDouble(object __instance, double __0)
    {
        if (VoicePackPlugin.Instance != null)
        {
            VoicePackPlugin.Instance.SetEcstasy((float)__0);
        }
    }
}