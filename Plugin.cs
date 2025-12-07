using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Networking;

[BepInPlugin("herpderp135.secretflasher.voicepack", "SecretFlasher Voice Pack - 4段防暴涨版", "0.7.5")]
public class VoicePackPlugin : BasePlugin
{
    public static VoicePackPlugin Instance { get; private set; }
    public static ManualLogSource L;

    private GameObject _host;
    private AudioSource _src;
    private readonly Dictionary<string, List<AudioClip>> _clips = new();
    private readonly Queue<(string path, string cat)> _queue = new();
    private readonly List<Pending> _inflight = new();
    private const int MAX_INFLIGHT = 4;
    private struct Pending { public string path, cat; public UnityWebRequest req; }

    private readonly System.Random _rng = new System.Random();

    // ==================== 你的4段配置 ====================
    private struct Level
    {
        public float threshold;
        public string category;
        public float loopInterval;   // 0 = 不循环
        public float entryCooldown;
    }

    private readonly Level[] levels = new Level[]
    {
        new Level { threshold = 0.20f, category = "low",     loopInterval = 1.4f, entryCooldown = 4.0f },
        new Level { threshold = 0.50f, category = "medium",  loopInterval = 1.2f, entryCooldown = 3.0f },
        new Level { threshold = 0.80f, category = "high",    loopInterval = 1.0f, entryCooldown = 2.5f },
        new Level { threshold = 0.80f, category = "climax",  loopInterval = 0.0f, entryCooldown = 2.5f } // climax不循环
    };

    // ==================== 防暴涨核心变量 ====================
    private int _currentLevel = -1;
    private float _nextLoopTime = -999f;
    private float[] _lastEntryTimes;
    private float[] _lastLoopTimes;
    private float _prev = 0f;
    private float _smoothedEc = 0f;
    private readonly float _emaAlpha = 0.22f;
    private readonly float _hysteresis = 0.05f;
    private readonly Queue<int> _catchupQueue = new();
    private float _lastCatchupTime = 0f;
    private const float CATCHUP_DELAY = 0.17f;
    private const float RESET_THR = 0.05f;

    // ==================== 反射获取Ecstasy（完整版） ====================
    private const string GameStateDataTypeName = "ExposureUnnoticed2.Scripts.InGame.GameStateData";
    private const string PreferredMemberName = "PlayerEcstasy";

    private Type _gsdType;
    private object _gsdInstance;
    private Func<object, float> _ecstasyGetter;
    private Func<object, float> _controllerEcstasyGetter;
    private bool _controllerGetterTried;
    private Func<float> _controllerEcstasyGetter2;

    public override void Load()
    {
        Instance = this;
        L = Log;
        L.LogInfo("[VoicePack] 4段防暴涨版 v0.7.5 加载中...");

        _host = new GameObject("VoicePack_Host");
        GameObject.DontDestroyOnLoad(_host);
        _src = _host.AddComponent<AudioSource>();
        _src.playOnAwake = false;
        _src.spatialBlend = 0f;

        foreach (var lvl in levels) _clips[lvl.category] = new List<AudioClip>();
        _lastEntryTimes = new float[levels.Length];
        _lastLoopTimes = new float[levels.Length];
        Array.Fill(_lastEntryTimes, -999f);
        Array.Fill(_lastLoopTimes, -999f);

        var harmony = new Harmony("herpderp135.secretflasher.voicepack");
        harmony.PatchAll();

        EnsurePreloadQueued();
        L.LogInfo("[VoicePack] 加载完成！支持 low/medium/high/climax 四个文件夹");
    }

    internal void Tick(object controller)
    {
        PumpLoads();
        float ec = GetEcstasy(controller);
        if (ec >= 0f) DriveState(ec);
        PumpCatchup();
    }

    private float GetEcstasy(object controller)
    {
        if (controller == null) return -1f;

        // 优先用直接绑定的 controller getter
        if (_controllerEcstasyGetter != null)
            return _controllerEcstasyGetter(controller);

        // 尝试绑定 controller 的 PlayerEcstasy
        if (!_controllerGetterTried)
        {
            _controllerGetterTried = true;
            TryResolveControllerEcstasyGetter(controller.GetType());
        }
        if (_controllerEcstasyGetter != null)
            return _controllerEcstasyGetter(controller);

        // 全局 GameStateData
        if (_ecstasyGetter == null && !_resolverTried)
        {
            _resolverTried = true;
            TryResolveGlobalEcstasyGetter();
        }
        if (_ecstasyGetter != null && _gsdInstance != null)
            return _ecstasyGetter(_gsdInstance);

        return -1f;
    }

    private void TryResolveControllerEcstasyGetter(Type type)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;

        var prop = type.GetProperty(PreferredMemberName, flags);
        if (prop != null && (prop.PropertyType == typeof(float) || prop.PropertyType == typeof(double)))
        {
            var getMethod = prop.GetGetMethod(true);
            if (getMethod != null)
            {
                _controllerEcstasyGetter = obj => Convert.ToSingle(getMethod.Invoke(obj, null));
                L.LogInfo("[VoicePack] 成功绑定 Controller.PlayerEcstasy 属性");
                return;
            }
        }

        var field = type.GetField(PreferredMemberName, flags);
        if (field != null && (field.FieldType == typeof(float) || field.FieldType == typeof(double)))
        {
            _controllerEcstasyGetter = obj => Convert.ToSingle(field.GetValue(obj));
            L.LogInfo("[VoicePack] 成功绑定 Controller.PlayerEcstasy 字段");
        }
    }

    private void TryResolveGlobalEcstasyGetter()
    {
        _gsdType = AccessTools.TypeByName(GameStateDataTypeName);
        if (_gsdType == null) return;

        var instanceField = _gsdType.GetField("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
        if (instanceField != null) _gsdInstance = instanceField.GetValue(null);
        else
        {
            var prop = _gsdType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (prop != null) _gsdInstance = prop.GetValue(null);
        }

        if (_gsdInstance == null) return;

        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var prop2 = _gsdType.GetProperty(PreferredMemberName, flags);
        if (prop2 != null)
        {
            var get = prop2.GetGetMethod(true);
            if (get != null) _ecstasyGetter = obj => Convert.ToSingle(get.Invoke(obj, null));
        }
        else
        {
            var field2 = _gsdType.GetField(PreferredMemberName, flags);
            if (field2 != null) _ecstasyGetter = obj => Convert.ToSingle(field2.GetValue(obj));
        }

        if (_ecstasyGetter != null)
            L.LogInfo("[VoicePack] 成功绑定全局 GameStateData.PlayerEcstasy");
    }

    // ==================== 核心逻辑 ====================
    private void DriveState(float rawEc)
    {
        _smoothedEc = _emaAlpha * rawEc + (1f - _emaAlpha) * _smoothedEc;
        float ec = _smoothedEc;

        if (Mathf.Abs(ec - _prev) < 0.001f) return;
        _prev = ec;

        int newLevel = GetLevel(ec);

        if (newLevel == -1)
        {
            if (_currentLevel >= 0) L.LogInfo("[VoicePack] 重置状态");
            _currentLevel = -1;
            _nextLoopTime = -999f;
            _catchupQueue.Clear();
            return;
        }

        if (newLevel > _currentLevel)
        {
            L.LogInfo($"[VoicePack] 追赶触发：{_currentLevel} → {newLevel}");
            for (int i = _currentLevel + 1; i <= newLevel; i++)
                _catchupQueue.Enqueue(i);
            _currentLevel = newLevel;

            if (levels[newLevel].loopInterval > 0f)
                _nextLoopTime = Time.time + levels[newLevel].loopInterval + UnityEngine.Random.Range(-0.2f, 0.2f);
        }

        if (_currentLevel >= 0 && levels[_currentLevel].loopInterval > 0f && Time.time >= _nextLoopTime)
        {
            PlayFrom(_currentLevel, ref _lastLoopTimes[_currentLevel], 0f);
            _nextLoopTime = Time.time + levels[_currentLevel].loopInterval + UnityEngine.Random.Range(-0.2f, 0.2f);
        }
    }

    private void PumpCatchup()
    {
        if (_catchupQueue.Count == 0 || Time.time - _lastCatchupTime < CATCHUP_DELAY) return;

        int lvl = _catchupQueue.Dequeue();
        L.LogInfo($"[VoicePack] 追赶播放 {levels[lvl].category}");
        PlayFrom(lvl, ref _lastEntryTimes[lvl], 0f);
        _lastCatchupTime = Time.time;
    }

    private int GetLevel(float ec)
    {
        for (int i = levels.Length - 1; i >= 0; i--)
            if (ec >= levels[i].threshold) return i;

        if (_currentLevel >= 0 && ec < levels[_currentLevel].threshold - _hysteresis)
            return -1;

        return _currentLevel;
    }

    private void PlayFrom(int lvlIdx, ref float lastAt, float cooldown)
    {
        if (Time.time - lastAt < cooldown) return;

        string cat = levels[lvlIdx].category;
        var list = _clips[cat];
        if (list.Count == 0) return;

        list.RemoveAll(c => c == null);
        if (list.Count == 0) return;

        var clip = list[_rng.Next(list.Count)];
        _src.PlayOneShot(clip);
        lastAt = Time.time;
    }

    // ==================== 音频加载 ====================
    private void EnsurePreloadQueued()
    {
        if (_queue.Count > 0) return;
        TryQueueAudioLoads();
    }

    private void TryQueueAudioLoads()
    {
        var baseDir = Path.Combine(Paths.PluginPath, "SecretFlasher.VoicePack", "audio");
        if (!Directory.Exists(baseDir))
        {
            L.LogWarning($"[VoicePack] 未找到 audio 文件夹: {baseDir}");
            return;
        }

        foreach (var lvl in levels)
        {
            var dir = Path.Combine(baseDir, lvl.category);
            if (!Directory.Exists(dir)) continue;

            var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase));

            foreach (var f in files)
                _queue.Enqueue((f, lvl.category));
        }
        L.LogInfo($"[VoicePack] 发现 {_queue.Count} 个语音文件，准备加载...");
    }

    private void PumpLoads()
    {
        EnsurePreloadQueued();

        while (_inflight.Count < MAX_INFLIGHT && _queue.Count > 0)
        {
            var item = _queue.Dequeue();
            var req = UnityWebRequestMultimedia.GetAudioClip("file://" + item.path, AudioType.UNKNOWN);
            req.SendWebRequest();
            _inflight.Add(new Pending { path = item.path, cat = item.cat, req = req });
        }

        for (int i = _inflight.Count - 1; i >= 0; i--)
        {
            var p = _inflight[i];
            if (!p.req.isDone) continue;

            if (p.req.result == UnityWebRequest.Result.Success)
            {
                var clip = DownloadHandlerAudioClip.GetContent(p.req);
                if (clip) _clips[p.cat].Add(clip);
            }
            _inflight.RemoveAt(i);
        }
    }

    // ==================== Harmony补丁 ====================
    [HarmonyPatch]
    internal static class Patch_PlayerEcstasyController_OnUpdate
    {
        static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("ExposureUnnoticed2.Object3D.Player.Scripts.PlayerEcstasyController");
            return type?.GetMethod("OnUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        static void Postfix(object __instance)
        {
            VoicePackPlugin.Instance?.Tick(__instance);
        }
    }
}
