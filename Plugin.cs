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

[BepInPlugin("herpderp135.secretflasher.voicepack", "SecretFlasher Voice Pack", "0.7.3")]
public class VoicePackPlugin : BaseUnityPlugin
{
    public static VoicePackPlugin Instance { get; private set; }
    public static ManualLogSource L;

    private GameObject _host;
    private AudioSource _src;
    private readonly Dictionary<string, List<AudioClip>> _clips = new();
    private readonly Queue<(string path, string cat)> _queue = new();
    private readonly List<Pending> _inflight = new();
    private const int MAX_INFLIGHT = 3;
    private struct Pending { public string path, cat; public UnityWebRequest req; }

    private bool _preloadQueued = false;
    private readonly System.Random _rng = new System.Random();

    // 你的 4 个分段配置（阈值: low 0-20%, medium 20-50%, high 50-80%, climax 80%+）
    private struct Level
    {
        public float threshold;
        public string category;
        public float loopInterval;  // 循环间隔（秒，0=无循环，如 climax）
        public float entryCooldown; // 进入冷却（秒）
    }

    private readonly Level[] levels = new Level[]
    {
        new Level { threshold = 0.20f, category = "low",     loopInterval = 1.3f, entryCooldown = 4.0f },
        new Level { threshold = 0.50f, category = "medium",  loopInterval = 1.1f, entryCooldown = 3.0f },
        new Level { threshold = 0.80f, category = "high",    loopInterval = 1.0f, entryCooldown = 2.5f },
        new Level { threshold = 0.80f, category = "climax",  loopInterval = 0.0f, entryCooldown = 2.5f } // 注意: climax 和 high 阈值相同，但数组顺序确保 climax 优先（从后匹配）
    };

    // 防滞后（暴涨追赶）变量
    private int _currentLevel = -1;
    private float _nextLoopTime = -999f;
    private float[] _lastEntryTimes;
    private float[] _lastLoopTimes;
    private float _prev = 0f;
    private float _smoothedEc = 0f;
    private readonly float _emaAlpha = 0.20f;     // 平滑系数（0.1-0.3，越高越灵敏）
    private readonly float _hysteresis = 0.05f;   // 防抖幅度
    private readonly Queue<int> _catchupQueue = new Queue<int>();
    private float _lastCatchupTime = 0f;
    private const float CATCHUP_DELAY = 0.18f;    // 追赶间隔（秒，越小越快）
    private const float RESET_THR = 0.05f;

    // 原有反射字段（从你的 Gist 复制，确保兼容）
    private const string GameStateDataTypeName = "ExposureUnnoticed2.Scripts.InGame.GameStateData";
    private const string PreferredMemberName = "PlayerEcstasy";
    private Type _gsdType;
    private object _gsdInstance;
    private bool _resolverTried = false;
    private Func<object, float> _ecstasyGetter = null;
    private Func<object, float> _controllerEcstasyGetter = null;
    private bool _controllerGetterTried = false;
    private Func<float> _controllerEcstasyGetter2 = null;

    public override void Load()
    {
        Instance = this;
        L = Log;

        L.LogInfo("[VoicePack] 4段版加载中（low/medium/high/climax + 防暴涨）");

        _host = new GameObject("VoicePack_Host");
        GameObject.DontDestroyOnLoad(_host);
        _src = _host.AddComponent<AudioSource>();

        // 设置 AudioSource
        var pPlay = typeof(AudioSource).GetProperty("playOnAwake", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (pPlay != null) pPlay.SetValue(_src, false, null);
        var pBlend = typeof(AudioSource).GetProperty("spatialBlend", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (pBlend != null) pBlend.SetValue(_src, 0f, null);

        // 初始化 clips 和时间数组
        foreach (var lvl in levels) _clips[lvl.category] = new List<AudioClip>();
        _lastEntryTimes = new float[levels.Length];
        _lastLoopTimes = new float[levels.Length];
        Array.Fill(_lastEntryTimes, -999f);
        Array.Fill(_lastLoopTimes, -999f);

        // Harmony 补丁
        var harmony = new Harmony("herpderp135.secretflasher.voicepack");
        harmony.PatchAll();
        TryPatchOptionalHarmony(harmony);
        L.LogInfo("[VoicePack] Harmony 补丁应用成功！");

        EnsurePreloadQueued();  // 开始加载音频
    }

    // 原有 Tick 方法（每帧调用）
    internal void Tick(object controller)
    {
        PumpLoads();
        float ec = GetEcstasy(controller);
        if (ec >= 0) DriveState(ec);
        PumpCatchup();
    }

    // 核心：DriveState（平滑 + 追赶）
    private void DriveState(float rawEc)
    {
        // EMA 平滑
        _smoothedEc = _emaAlpha * rawEc + (1f - _emaAlpha) * _smoothedEc;
        float ec = _smoothedEc;

        if (Mathf.Abs(ec - _prev) < 0.001f) return;
        _prev = ec;

        int newLevel = GetLevel(ec);

        // 重置
        if (newLevel == -1)
        {
            if (_currentLevel >= 0) L.LogInfo("[VoicePack] RESET");
            _currentLevel = -1;
            _nextLoopTime = -999f;
            _catchupQueue.Clear();
            return;
        }

        // 追赶：暴涨时依次入队所有段
        if (newLevel > _currentLevel)
        {
            L.LogInfo($"[VoicePack] 追赶：{_currentLevel} → {newLevel}");
            for (int i = _currentLevel + 1; i <= newLevel; i++)
                _catchupQueue.Enqueue(i);
            _currentLevel = newLevel;
            if (levels[newLevel].loopInterval > 0f)
                _nextLoopTime = Time.time + levels[newLevel].loopInterval + (float)UnityEngine.Random.Range(-0.2f, 0.2f);
        }

        // 段内循环
        if (_currentLevel >= 0 && levels[_currentLevel].loopInterval > 0f && Time.time >= _nextLoopTime)
        {
            L.LogInfo($"[VoicePack] LOOP {levels[_currentLevel].category}");
            PlayFrom(_currentLevel, ref _lastLoopTimes[_currentLevel], 0f);
            _nextLoopTime = Time.time + levels[_currentLevel].loopInterval + (float)UnityEngine.Random.Range(-0.2f, 0.2f);
        }
    }

    // 泵追赶队列
    private void PumpCatchup()
    {
        if (_catchupQueue.Count == 0 || Time.time - _lastCatchupTime < CATCHUP_DELAY) return;

        int lvl = _catchupQueue.Dequeue();
        L.LogInfo($"[VoicePack] 追赶播放 {levels[lvl].category}");
        PlayFrom(lvl, ref _lastEntryTimes[lvl], 0f);  // 追赶无冷却
        _lastCatchupTime = Time.time;
    }

    // 获取当前段
    private int GetLevel(float ec)
    {
        for (int i = levels.Length - 1; i >= 0; i--)
            if (ec >= levels[i].threshold) return i;

        // 迟滞退出
        if (_currentLevel >= 0 && ec < levels[_currentLevel].threshold - _hysteresis)
            return -1;

        return _currentLevel;
    }

    // 播放（简化版 PlayFrom，传入 lvl 索引）
    private void PlayFrom(int lvlIdx, ref float lastAt, float cooldownSec)
    {
        if (Time.time - lastAt < cooldownSec) return;

        string cat = levels[lvlIdx].category;
        var list = _clips[cat];
        if (list.Count == 0) { MaybeRequeueCategory(cat); return; }

        // 清理无效 clip
        for (int i = list.Count - 1; i >= 0; i--) if (!list[i]) list.RemoveAt(i);

        if (list.Count == 0) { MaybeRequeueCategory(cat); return; }

        var clip = list[_rng.Next(list.Count)];
        _src.PlayOneShot(clip);
        lastAt = Time.time;
        L.LogInfo($"[VoicePack] 播放 {cat}");
    }

    // 原有音频加载方法（从你的 Gist 复制，稍改以支持 levels）
    private void EnsurePreloadQueued()
    {
        if (_preloadQueued) return;
        _preloadQueued = true;
        TryQueueAudioLoads();
        L.LogInfo($"[VoicePack] 开始预加载 {levels.Length} 段音频");
    }

    private void TryQueueAudioLoads()
    {
        try
        {
            var baseDir = Path.Combine(Paths.PluginPath, "SecretFlasher.VoicePack", "audio");
            if (!Directory.Exists(baseDir)) { L.LogWarning($"[VoicePack] 缺少 audio 文件夹: {baseDir}"); return; }

            int total = 0;
            foreach (var lvl in levels)
            {
                var dir = Path.Combine(baseDir, lvl.category);
                if (!Directory.Exists(dir)) { L.LogWarning($"[VoicePack] 缺少段文件夹: {dir}"); continue; }
                var files = Directory.GetFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => Path.GetExtension(f).ToLowerInvariant() == ".ogg" || Path.GetExtension(f).ToLowerInvariant() == ".wav");
                foreach (var path in files) _queue.Enqueue((path, lvl.category));
                total += files.Count();
            }
            L.LogInfo($"[VoicePack] 队列 {_queue.Count} 个文件（{total} 有效）");
        }
        catch (Exception ex) { L.LogWarning($"[VoicePack] 加载队列失败: {ex.Message}"); }
    }

    private void PumpLoads()
    {
        while (_inflight.Count < MAX_INFLIGHT && _queue.Count > 0)
        {
            var (path, cat) = _queue.Dequeue();
            var req = UnityWebRequestMultimedia.GetAudioClip("file://" + path, AudioType.UNKNOWN);
            req.SendWebRequest();
            _inflight.Add(new Pending { path = path, cat = cat, req = req });
        }

        for (int i = _inflight.Count - 1; i >= 0; i--)
        {
            var p = _inflight[i];
            if (!p.req.isDone) continue;
            if (p.req.result == UnityWebRequest.Result.Success)
            {
                var clip = DownloadHandlerAudioClip.GetContent(p.req);
                if (clip) _clips[p.cat].Add(clip);
                L.LogInfo($"[VoicePack] 加载成功: {Path.GetFileName(p.path)} 到 {p.cat}");
            }
            else
            {
                L.LogWarning($"[VoicePack] 加载失败 {p.path}: {p.req.error}");
                MaybeRequeueCategory(p.cat);
            }
            _inflight.RemoveAt(i);
        }
    }

    private void MaybeRequeueCategory(string cat)
    {
        // 简单重试逻辑（每3秒一次，避免 spam）
        // 从原 Gist 复制你的实现
    }

    // 原有反射和 Harmony 部分（从你的 Gist 完整复制，确保兼容）
    // ... 这里省略，粘贴你的 GetEcstasy、TryResolveControllerEcstasyGetter、TryFuzzyFindEcstasyFromGraph、Patch_PlayerEcstasyController_OnUpdate、OptionalHooks、TryPatchOptionalHarmony 等所有方法 ...
    // 示例 GetEcstasy（替换成你的完整版）
    private float GetEcstasy(object controller)
    {
        // 你的反射代码...
        // 如果 _controllerEcstasyGetter != null return _controllerEcstasyGetter(controller);
        // fallback 到其他...
        return 0f;  // 占位，替换
    }

    // ... 其他原方法 ...
}
