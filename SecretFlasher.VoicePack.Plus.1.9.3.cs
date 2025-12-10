using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using HarmonyLib;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Networking;
using System.Runtime.CompilerServices;

namespace SecretFlasher.VoicePack_Plus
{
    public static class PluginInfo
    {
        public const string GUID = "Havoc12.secretflasher.voicepack.plus";
        public const string Name = "SecretFlasher.VoicePack-Plus";
        public const string Version = "1.9.3"; // 版本升级：移除增速计算 + 新增Climax锁定
    }

    [BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
    public class VoicePackPlugin : BasePlugin
    {
        public static VoicePackPlugin Instance { get; private set; }
        public static ManualLogSource L;

        private ConfigFile _customConfig;
        private int _frameCounter = 0;

        private GameObject _audioHost;
        private AudioSource _audioSource;
        private readonly Dictionary<string, List<AudioClip>> _audioClips = new()
        {
            { "low", new List<AudioClip>() },
            { "med", new List<AudioClip>() },
            { "high", new List<AudioClip>() },
            { "high_long", new List<AudioClip>() },
            { "climax", new List<AudioClip>() }
        };
        private readonly System.Random _rng = new System.Random();

        // 各阶段可用音频索引列表（轮询用）
        private readonly Dictionary<string, List<int>> _availableClipIndicesPerStage = new()
        {
            { "low", new List<int>() },
            { "med", new List<int>() },
            { "high", new List<int>() },
            { "high_long", new List<int>() },
            { "climax", new List<int>() }
        };

        private bool _preloadQueued = false;
        private readonly Queue<(string path, string category)> _loadQueue = new();
        private struct PendingLoad { public string path; public string category; public UnityWebRequest request; }
        private readonly List<PendingLoad> _inflightLoads = new();
        private const int MaxInflightLoads = 4;
        private readonly HashSet<string> _warnedEmptyCategories = new();
        private readonly Dictionary<string, float> _lastReloadAttempt = new()
        {
            { "low", -999f }, { "med", -999f }, { "high", -999f }, { "high_long", -999f }, { "climax", -999f }
        };

        private float _previousEcstasy = 0f;
        private int _currentLevel = -1;
        private float _nextPlayTime = -999f;
        private bool _climaxPlayed = false;
        private float _lastLowPlayed = -999f;
        private float _lastMedPlayed = -999f;
        private float _lastHighPlayed = -999f;
        private float _lastHighLongPlayed = -999f;
        private float _lastClimaxPlayed = -999f;
        private float _lastAnyAudioPlayed = -999f;
        public float CurrentPlayerEcstasy { get; private set; } = 0f;

        // High阶段播放计数（控制high_long插播）
        private int _highPlayCount = 0;
        private const int MinHighBeforeLong = 2;
        private const int MaxHighBeforeLong = 3;
        private int _highLongTriggerThreshold;

        // 暂停逻辑（仅判断游戏暂停）
        private bool _audioPaused = false;

        // ========== 新增：Climax锁定相关变量 ==========
        private float _climaxLockStartTime = -1f; // Climax锁定开始时间
        private ConfigEntry<float> _cfgClimaxLockDuration; // Climax强制锁定时长（秒）
        // ==============================================

        private const string GameStateTypeName = "ExposureUnnoticed2.Scripts.InGame.GameState";
        private const string GameStateDataTypeName = "ExposureUnnoticed2.Scripts.InGame.GameStateData";
        private const string PlayerEcstasyControllerTypeName = "ExposureUnnoticed2.Object3D.Player.Scripts.PlayerEcstasyController";
        private const string PreferredEcstasyMember = "PlayerEcstasy";

        private Type _gameStateType;
        private Type _gameStateDataType;
        private object _gameStateDataInstance;
        private Func<object> _gameStateDataGetter;
        private Func<object, float> _ecstasyValueGetter;
        private bool _reflectionInitialized = false;

        private bool _controllerGetterAttempted = false;
        private Func<object, float> _controllerEcstasyGetter;
        private Func<float> _fallbackEcstasyGetter;
        private float _lastDirectEcstasyFeed = -999f;

        private float _lastNotFoundLog = -999f;
        private float _lastEcstasyLogValue = -1f;

        // 核心配置项（移除所有增速相关配置）
        private ConfigEntry<float> _cfgLowBaseInterval;
        private ConfigEntry<float> _cfgMedBaseInterval;
        private ConfigEntry<float> _cfgHighBaseInterval;
        private ConfigEntry<float> _cfgHighLongBaseInterval;
        private ConfigEntry<float> _cfgClimaxCooldown;
        private ConfigEntry<float> _cfgMinPlayInterval;
        private ConfigEntry<float> _cfgLowThreshold;
        private ConfigEntry<float> _cfgMedThreshold;
        private ConfigEntry<float> _cfgHighThreshold;
        private ConfigEntry<float> _cfgClimaxThreshold;
        private ConfigEntry<float> _cfgResetThreshold;
        private ConfigEntry<float> _cfgHysteresis;

        // 调试配置项
        private ConfigEntry<bool> _cfgEnableDebugLogging;
        private ConfigEntry<bool> _cfgEnableVerboseEcstasyLogging;
        private ConfigEntry<bool> _cfgEnablePlaybackDebug;

        public override void Load()
        {
            Instance = this;
            L = Log;

            _customConfig = new ConfigFile(Path.Combine(Paths.ConfigPath, "secretflasher.voicepack.plus.cfg"), true);

            string startupTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            L.LogInfo($"[{startupTime}] {PluginInfo.Name} v{PluginInfo.Version} loaded - 移除增速计算，仅通过Ecstasy值判断阶段 + Climax 3秒锁定");
            L.LogInfo($"[{startupTime}] Custom config file: {_customConfig.ConfigFilePath}");

            InitializeConfiguration();
            InitializeAudioSystem();
            InitializeReflection();
            ApplyHarmonyPatches();

            // 初始化high_long触发阈值
            _highLongTriggerThreshold = _rng.Next(MinHighBeforeLong, MaxHighBeforeLong + 1);
        }

        #region 初始化方法
        private void InitializeConfiguration()
        {
            _cfgEnableDebugLogging = _customConfig.Bind(
                "Debug",
                "EnableDebugLogging",
                false,
                "Enables debug level logging (verbose technical details)");

            _cfgEnableVerboseEcstasyLogging = _customConfig.Bind(
                "Debug",
                "EnableVerboseEcstasyLogging",
                false,
                "Enables frame-by-frame logging of Ecstasy values (very verbose)");

            _cfgEnablePlaybackDebug = _customConfig.Bind(
                "Debug",
                "EnablePlaybackDebug",
                true,
                "Enables detailed playback logic logging (next play time, stage info)");

            // 音频播放基础间隔（按要求配置）
            _cfgLowBaseInterval = _customConfig.Bind("Playback", "LowBaseInterval", 1.2f, "Low level base playback interval (seconds)");
            _cfgMedBaseInterval = _customConfig.Bind("Playback", "MedBaseInterval", 0.9f, "Med level base playback interval (seconds)");
            _cfgHighBaseInterval = _customConfig.Bind("Playback", "HighBaseInterval", 0.8f, "High level base playback interval (seconds)");
            _cfgHighLongBaseInterval = _customConfig.Bind("Playback", "HighLongBaseInterval", 1.2f, "High long level base playback interval (seconds)");
            _cfgClimaxCooldown = _customConfig.Bind("Playback", "ClimaxCooldown", 5.0f, "Climax audio cooldown (seconds)");

            // 全局最小播放间隔（防止音频叠加）
            _cfgMinPlayInterval = _customConfig.Bind("Playback", "MinPlayInterval", 0.6f, "Minimum allowed playback interval (seconds)");

            // 阶段阈值（确认默认值：Low=0.05, Med=0.30, High=0.60, Climax=1.0）
            _cfgLowThreshold = _customConfig.Bind("Thresholds", "LowThreshold", 0.02f, "Low level threshold (0-1)");
            _cfgMedThreshold = _customConfig.Bind("Thresholds", "MedThreshold", 0.30f, "Med level threshold (0-1)");
            _cfgHighThreshold = _customConfig.Bind("Thresholds", "HighThreshold", 0.60f, "High level threshold (0-1)");
            _cfgClimaxThreshold = _customConfig.Bind("Thresholds", "ClimaxThreshold", 0.95f, "Climax level threshold (0-1)");
            _cfgResetThreshold = _customConfig.Bind("Thresholds", "ResetThreshold", 0.05f, "State reset threshold (0-1)");
            _cfgHysteresis = _customConfig.Bind("Thresholds", "Hysteresis", 0.02f, "Level switch hysteresis (0-0.1)");

            // ========== 新增：Climax锁定时长配置 ==========
            _cfgClimaxLockDuration = _customConfig.Bind(
                "Thresholds",
                "ClimaxLockDuration",
                1.02f,
                "Climax阶段强制锁定时长（秒，触发后保持Climax阶段的时间）");
            // ==============================================

            string configTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            L.LogInfo($"[{configTime}] Configuration initialized - 阶段阈值: Low={_cfgLowThreshold.Value:F2}, Med={_cfgMedThreshold.Value:F2}, High={_cfgHighThreshold.Value:F2}, Climax={_cfgClimaxThreshold.Value:F2}");
            L.LogInfo($"[{configTime}] Climax锁定时长: {_cfgClimaxLockDuration.Value:F1}秒 | 播放间隔: Low={_cfgLowBaseInterval.Value:F2}s, Med={_cfgMedBaseInterval.Value:F2}s, High={_cfgHighBaseInterval.Value:F2}s");

            if (_cfgEnableDebugLogging.Value)
            {
                L.LogDebug($"[{configTime}] Debug logging enabled");
                L.LogDebug($"[{configTime}] Playback debug logging: {(_cfgEnablePlaybackDebug.Value ? "ENABLED" : "DISABLED")}");
            }
        }

        private void InitializeAudioSystem()
        {
            _audioHost = new GameObject("VoicePack_AudioHost");
            GameObject.DontDestroyOnLoad(_audioHost);
            _audioSource = _audioHost.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.spatialBlend = 0f;

            string audioInitTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            L.LogInfo($"[{audioInitTime}] Audio system initialized");

            if (_cfgEnableDebugLogging.Value)
            {
                L.LogDebug($"[{audioInitTime}] Audio host object created: {_audioHost.name}");
            }
        }

        private void InitializeReflection()
        {
            if (_reflectionInitialized) return;

            try
            {
                _gameStateType = AccessTools.TypeByName(GameStateTypeName) ??
                    AppDomain.CurrentDomain.GetAssemblies()
                        .SelectMany(a => a.GetTypes())
                        .FirstOrDefault(t => t.Name == "GameState" && !t.IsEnum && !t.IsValueType);

                _gameStateDataType = AccessTools.TypeByName(GameStateDataTypeName);
                if (_gameStateDataType == null)
                {
                    string errorTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    L.LogError($"[{errorTime}] Failed to find GameStateData type");
                    _reflectionInitialized = true;
                    return;
                }

                if (_gameStateType != null)
                {
                    var prop = _gameStateType.GetProperty("GameStateData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (prop != null && prop.PropertyType == _gameStateDataType)
                    {
                        var getMethod = prop.GetGetMethod(true);
                        _gameStateDataGetter = () => getMethod.Invoke(null, null);

                        if (_cfgEnableDebugLogging.Value)
                        {
                            string debugTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                            L.LogDebug($"[{debugTime}] Bound GameStateData via property");
                        }
                    }
                    else
                    {
                        var field = _gameStateType.GetField("GameStateData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        if (field != null && field.FieldType == _gameStateDataType)
                        {
                            _gameStateDataGetter = () => field.GetValue(null);

                            if (_cfgEnableDebugLogging.Value)
                            {
                                string debugTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                                L.LogDebug($"[{debugTime}] Bound GameStateData via field");
                            }
                        }
                    }
                }

                var ecstasyProp = _gameStateDataType.GetProperty(PreferredEcstasyMember, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (ecstasyProp != null && (ecstasyProp.PropertyType == typeof(float) || ecstasyProp.PropertyType == typeof(double)))
                {
                    var getMethod = ecstasyProp.GetGetMethod(true);
                    _ecstasyValueGetter = obj => Convert.ToSingle(getMethod.Invoke(obj, null));

                    if (_cfgEnableDebugLogging.Value)
                    {
                        string debugTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        L.LogDebug($"[{debugTime}] Bound PlayerEcstasy via property");
                    }
                }
                else
                {
                    var ecstasyField = _gameStateDataType.GetField(PreferredEcstasyMember, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (ecstasyField != null && (ecstasyField.FieldType == typeof(float) || ecstasyField.FieldType == typeof(double)))
                    {
                        _ecstasyValueGetter = obj => Convert.ToSingle(ecstasyField.GetValue(obj));

                        if (_cfgEnableDebugLogging.Value)
                        {
                            string debugTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                            L.LogDebug($"[{debugTime}] Bound PlayerEcstasy via field");
                        }
                    }
                }

                if (_ecstasyValueGetter == null)
                {
                    string errorTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    L.LogError($"[{errorTime}] Failed to bind PlayerEcstasy value getter");
                }
                else
                {
                    string reflectionTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    L.LogInfo($"[{reflectionTime}] Reflection initialized successfully - Bound PlayerEcstasy member");
                }
            }
            catch (Exception ex)
            {
                string errorTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                L.LogError($"[{errorTime}] Reflection initialization failed: {ex.Message}");

                if (_cfgEnableDebugLogging.Value)
                {
                    L.LogDebug($"[{errorTime}] Reflection exception details: {ex.StackTrace}");
                }
            }

            _reflectionInitialized = true;
        }

        private void ApplyHarmonyPatches()
        {
            try
            {
                var harmony = new Harmony(PluginInfo.GUID);

                var controllerType = AccessTools.TypeByName(PlayerEcstasyControllerTypeName);
                if (controllerType != null)
                {
                    var onUpdateMethod = AccessTools.Method(controllerType, "OnUpdate", Type.EmptyTypes);
                    if (onUpdateMethod != null)
                    {
                        harmony.Patch(onUpdateMethod, postfix: new HarmonyMethod(typeof(VoicePackPlugin), nameof(OnPlayerEcstasyControllerUpdate)));
                        string patchTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        L.LogInfo($"[{patchTime}] Patched PlayerEcstasyController.OnUpdate");
                    }
                    else
                    {
                        string warnTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                        L.LogWarning($"[{warnTime}] Failed to find PlayerEcstasyController.OnUpdate method");
                    }
                }
                else
                {
                    string warnTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    L.LogWarning($"[{warnTime}] Failed to find PlayerEcstasyController type");
                }

                string skipPatchTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                L.LogInfo($"[{skipPatchTime}] Skipped set_PlayerEcstasy patch (IL2CPP field accessor cannot be patched)");
            }
            catch (Exception ex)
            {
                string errorTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                L.LogError($"[{errorTime}] Harmony patch application failed: {ex.Message}");

                if (_cfgEnableDebugLogging.Value)
                {
                    L.LogDebug($"[{errorTime}] Patch exception details: {ex.StackTrace}");
                }
            }
        }
        #endregion

        #region 核心逻辑（仅通过Ecstasy值判断阶段 + Climax锁定）
        internal void Tick(object controllerInstance = null)
        {
            _frameCounter++;
            string frameTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            float unityTime = Time.realtimeSinceStartup;

            EnsureAudioSourceValid();
            EnsureAudioPreloaded();
            ProcessAudioLoadQueue();

            // 获取当前Ecstasy值（核心：仅读取，不计算增速）
            float currentEcstasy = GetCurrentEcstasyValue(controllerInstance);
            CurrentPlayerEcstasy = Mathf.Clamp01(currentEcstasy);

            // 日志输出
            if (currentEcstasy >= 0f)
            {
                if (_cfgEnableVerboseEcstasyLogging.Value)
                {
                    L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] Frame: {_frameCounter}, Ecstasy: {currentEcstasy:F4} (当前阶段: {GetLevelName(_currentLevel)})");
                }

                if (_cfgEnableDebugLogging.Value && Mathf.Abs(currentEcstasy - _lastEcstasyLogValue) > 0.001f)
                {
                    L.LogDebug($"[{frameTime}] [UnityTime: {unityTime:F3}s] Ecstasy更新: {_lastEcstasyLogValue:F4} → {currentEcstasy:F4}");
                    _lastEcstasyLogValue = currentEcstasy;
                }
            }
            else
            {
                L.LogWarning($"[{frameTime}] [UnityTime: {unityTime:F3}s] 无效Ecstasy值: {currentEcstasy:F4}");
                LogNotFoundThrottled("No valid ecstasy value found");
                return;
            }

            // 游戏暂停判断
            HandleGamePauseLogic(unityTime, frameTime);

            // 音频未暂停时，更新阶段和播放音频
            if (!_audioPaused)
            {
                // ========== 新增：优先判断Climax锁定状态 ==========
                if (_climaxLockStartTime > 0f)
                {
                    float lockedDuration = unityTime - _climaxLockStartTime;
                    if (lockedDuration < _cfgClimaxLockDuration.Value)
                    {
                        // 锁定期间强制保持Climax阶段，不处理其他逻辑
                        if (_currentLevel != 3)
                        {
                            L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] 强制锁定Climax阶段（剩余锁定时长：{_cfgClimaxLockDuration.Value - lockedDuration:F2}s）");
                            _currentLevel = 3;
                        }
                        HandleClimaxStage(unityTime, frameTime, currentEcstasy);
                        _previousEcstasy = currentEcstasy;
                        return;
                    }
                    else
                    {
                        // 锁定结束，重置锁定状态
                        L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] Climax锁定结束，恢复正常阶段判断");
                        _climaxLockStartTime = -1f;
                        _highPlayCount = 0; // 重置High播放计数
                    }
                }
                // ==============================================

                // 播放状态日志（每30帧输出一次）
                if (_cfgEnablePlaybackDebug.Value && _frameCounter % 30 == 0)
                {
                    float timeToNextPlay = _nextPlayTime - unityTime;
                    L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] 播放状态 - 阶段: {GetLevelName(_currentLevel)}, 下次播放: {timeToNextPlay:F3}s后, 已加载音频数: Low={_audioClips["low"].Count}, Med={_audioClips["med"].Count}, High={_audioClips["high"].Count}, HighLong={_audioClips["high_long"].Count}");
                }

                UpdateStateMachine(currentEcstasy, unityTime, frameTime);
            }
            else if (_cfgEnableDebugLogging.Value)
            {
                L.LogDebug($"[{frameTime}] [UnityTime: {unityTime:F3}s] 游戏暂停 - 跳过音频播放");
            }

            _previousEcstasy = currentEcstasy;
        }

        // 游戏暂停判断（仅基于Time.timeScale）
        private void HandleGamePauseLogic(float unityTime, string frameTime)
        {
            bool isGamePaused = Time.timeScale <= 0.001f;

            if (isGamePaused)
            {
                if (!_audioPaused)
                {
                    if (_audioSource.isPlaying) _audioSource.Pause();
                    _audioPaused = true;
                    L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] 游戏暂停 - 暂停音频");
                }
            }
            else
            {
                if (_audioPaused)
                {
                    if (_audioSource != null && _audioSource.clip != null) _audioSource.UnPause();
                    _audioPaused = false;
                    L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] 游戏恢复 - 恢复音频");
                }
            }
        }

        // 获取当前Ecstasy值（仅读取，无增速计算）
        private float GetCurrentEcstasyValue(object controllerInstance)
        {
            if (_reflectionInitialized && _gameStateDataGetter != null && _ecstasyValueGetter != null)
            {
                try
                {
                    _gameStateDataInstance = _gameStateDataGetter();
                    if (_gameStateDataInstance != null)
                    {
                        return Convert.ToSingle(_ecstasyValueGetter(_gameStateDataInstance));
                    }
                    else if (_cfgEnableDebugLogging.Value)
                    {
                        L.LogDebug($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] GameStateData实例为空");
                    }
                }
                catch (Exception ex)
                {
                    string errorTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    L.LogWarning($"[{errorTime}] 从GameStateData获取Ecstasy失败: {ex.Message}");
                }
            }

            if (controllerInstance != null)
            {
                if (!_controllerGetterAttempted)
                {
                    TryResolveControllerEcstasyGetter(controllerInstance.GetType());
                    _controllerGetterAttempted = true;
                }

                if (_controllerEcstasyGetter != null)
                {
                    try
                    {
                        return Mathf.Clamp01(_controllerEcstasyGetter(controllerInstance));
                    }
                    catch (Exception ex)
                    {
                        if (_cfgEnableDebugLogging.Value)
                        {
                            L.LogDebug($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 从Controller获取Ecstasy失败: {ex.Message}");
                        }
                    }
                }

                if (_fallbackEcstasyGetter == null)
                {
                    TryFuzzyEcstasySearch(controllerInstance);
                }

                if (_fallbackEcstasyGetter != null)
                {
                    try
                    {
                        return Mathf.Clamp01(_fallbackEcstasyGetter());
                    }
                    catch
                    {
                        _fallbackEcstasyGetter = null;
                        if (_cfgEnableDebugLogging.Value)
                        {
                            L.LogDebug($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 备用Ecstasy获取器失效");
                        }
                    }
                }
            }

            return -1f;
        }

        // 阶段状态机（仅通过Ecstasy值判断 + Climax锁定触发）
        private void UpdateStateMachine(float currentEcstasy, float unityTime, string frameTime)
        {
            // 防止音频双重播放
            if (_audioSource.isPlaying)
            {
                if (_cfgEnableDebugLogging.Value)
                {
                    L.LogDebug($"[{frameTime}] [UnityTime: {unityTime:F3}s] 音频正在播放 - 跳过当前帧");
                }
                return;
            }

            // Climax阶段（最高优先级，触发锁定）
            if (currentEcstasy >= _cfgClimaxThreshold.Value)
            {
                // ========== 新增：触发Climax锁定 ==========
                if (_climaxLockStartTime < 0f)
                {
                    _climaxLockStartTime = unityTime;
                    L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] 触发Climax，开始{_cfgClimaxLockDuration.Value:F1}秒强制锁定（Ecstasy: {currentEcstasy:F4}）");
                }
                // ==============================================
                HandleClimaxStage(unityTime, frameTime, currentEcstasy);
                return;
            }

            // 重置阶段（Ecstasy过低）
            if (currentEcstasy <= _cfgResetThreshold.Value)
            {
                HandleResetStage(unityTime, frameTime, currentEcstasy);
                return;
            }

            // 判断当前应处于的阶段
            int targetLevel = GetLevelFromEcstasy(currentEcstasy);

            // 阶段切换逻辑
            if (targetLevel != _currentLevel)
            {
                HandleStageSwitch(targetLevel, unityTime, frameTime, currentEcstasy);
            }
            // 同阶段持续播放
            else if (_currentLevel != -1 && unityTime >= _nextPlayTime)
            {
                PlayCurrentLevelAudio(unityTime, currentEcstasy);
                SetNextPlayTime(unityTime);
            }
            // 初始阶段触发（首次达到阈值）
            else if (targetLevel >= 0 && _currentLevel == -1)
            {
                _currentLevel = targetLevel;
                string levelName = GetLevelName(targetLevel);
                L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] 首次触发阶段: {levelName} (Ecstasy: {currentEcstasy:F4})");
                PlayCurrentLevelAudio(unityTime, currentEcstasy);
                SetNextPlayTime(unityTime);
            }
        }

        // Climax阶段处理（兼容锁定逻辑）
        private void HandleClimaxStage(float unityTime, string frameTime, float currentEcstasy)
        {
            if (_currentLevel != 3)
            {
                L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] 跨阶段: {GetLevelName(_currentLevel)} → climax (Ecstasy: {currentEcstasy:F4})");
                _currentLevel = 3;
                _climaxPlayed = false; // 切换到climax时重置播放标记
            }

            if (!_climaxPlayed && unityTime - _lastClimaxPlayed >= _cfgClimaxCooldown.Value)
            {
                float timeSinceLastPlay = unityTime - _lastAnyAudioPlayed;
                if (timeSinceLastPlay >= _cfgMinPlayInterval.Value)
                {
                    L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] 触发Climax音频 (Ecstasy: {currentEcstasy:F4})");
                    PlayAudio("climax", unityTime);
                    _lastClimaxPlayed = unityTime;
                    _climaxPlayed = true;
                    _nextPlayTime = unityTime + _cfgClimaxCooldown.Value; // Climax后冷却
                }
                else
                {
                    if (_cfgEnablePlaybackDebug.Value)
                    {
                        L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] Climax音频冷却中: 距离上次播放 {timeSinceLastPlay:F3}s (需 {_cfgMinPlayInterval.Value:F3}s)");
                    }
                    _nextPlayTime = _lastAnyAudioPlayed + _cfgMinPlayInterval.Value;
                }
            }
            // ========== 新增：锁定期间即使冷却也保持Climax阶段 ==========
            else if (_cfgEnablePlaybackDebug.Value && _climaxPlayed && _climaxLockStartTime > 0f)
            {
                float lockedDuration = unityTime - _climaxLockStartTime;
                L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] Climax锁定中（冷却）: 剩余锁定时长 {_cfgClimaxLockDuration.Value - lockedDuration:F2}s");
            }
            // ==============================================
        }

        // 重置阶段处理（新增：重置锁定状态）
        private void HandleResetStage(float unityTime, string frameTime, float currentEcstasy)
        {
            if (_currentLevel != -1)
            {
                L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] 重置阶段 (Ecstasy: {currentEcstasy:F4}, 当前阶段: {GetLevelName(_currentLevel)})");
                // 重置所有阶段状态
                foreach (var key in _availableClipIndicesPerStage.Keys.ToList())
                {
                    ResetAvailableIndicesForStage(key);
                }
                _currentLevel = -1;
                _climaxPlayed = false;
                _nextPlayTime = -999f;
                _lastAnyAudioPlayed = -999f;
                _highPlayCount = 0;
                _highLongTriggerThreshold = _rng.Next(MinHighBeforeLong, MaxHighBeforeLong + 1);
                // ========== 新增：重置Climax锁定状态 ==========
                _climaxLockStartTime = -1f;
                // ==============================================
            }
        }

        // 阶段切换处理
        private void HandleStageSwitch(int targetLevel, float unityTime, string frameTime, float currentEcstasy)
        {
            string oldLevelName = GetLevelName(_currentLevel);
            string newLevelName = GetLevelName(targetLevel);

            // 跨阶段日志
            L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] 跨阶段: {oldLevelName} → {newLevelName} (Ecstasy: {currentEcstasy:F4})");

            // 重置新阶段音频轮询列表
            ResetAvailableIndicesForStage(newLevelName);

            // 重置high阶段计数（切换到high时）
            if (newLevelName == "high")
            {
                _highPlayCount = 0;
                _highLongTriggerThreshold = _rng.Next(MinHighBeforeLong, MaxHighBeforeLong + 1);
            }

            // 更新当前阶段
            _currentLevel = targetLevel;

            // 检查全局冷却，播放音频
            float timeSinceLastPlay = unityTime - _lastAnyAudioPlayed;
            if (timeSinceLastPlay >= _cfgMinPlayInterval.Value)
            {
                PlayCurrentLevelAudio(unityTime, currentEcstasy);
                SetNextPlayTime(unityTime);
            }
            else
            {
                float delay = _cfgMinPlayInterval.Value - timeSinceLastPlay;
                _nextPlayTime = unityTime + delay;
                if (_cfgEnablePlaybackDebug.Value)
                {
                    L.LogInfo($"[{frameTime}] [UnityTime: {unityTime:F3}s] 阶段切换冷却: 延迟 {newLevelName} 音频 {delay:F3}s (下次播放: {_nextPlayTime:F3}s)");
                }
            }
        }
        #endregion

        #region 音频播放逻辑（保留轮询+high_long插播规则）
        // 重置阶段音频索引列表（随机打乱）
        private void ResetAvailableIndicesForStage(string stage)
        {
            if (!_audioClips.ContainsKey(stage)) return;
            var validClips = _audioClips[stage].Where(c => c != null).ToList();
            _availableClipIndicesPerStage[stage].Clear();

            // 生成索引并打乱
            for (int i = 0; i < validClips.Count; i++)
            {
                _availableClipIndicesPerStage[stage].Add(i);
            }
            ShuffleList(_availableClipIndicesPerStage[stage]);

            if (_cfgEnableDebugLogging.Value)
            {
                string logTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                L.LogDebug($"[{logTime}] 重置 {stage} 阶段音频索引: 共 {validClips.Count} 个有效音频");
            }
        }

        // Fisher-Yates洗牌算法
        private void ShuffleList<T>(List<T> list)
        {
            int n = list.Count;
            while (n > 1)
            {
                n--;
                int k = _rng.Next(n + 1);
                (list[k], list[n]) = (list[n], list[k]);
            }
        }

        // 预加载音频（轮询无协程方案）
        private void EnsureAudioPreloaded()
        {
            if (_preloadQueued) return;
            _preloadQueued = true;

            string baseAudioPath = Path.Combine(Paths.PluginPath, PluginInfo.Name, "VoicePack");
            if (!Directory.Exists(baseAudioPath))
            {
                string warnTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                L.LogWarning($"[{warnTime}] 音频基础目录不存在: {baseAudioPath}");
                return;
            }

            // 加载所有分类音频（low/med/high/high_long/climax）
            foreach (var category in new[] { "low", "med", "high", "high_long", "climax" })
            {
                string categoryPath = Path.Combine(baseAudioPath, category);
                if (!Directory.Exists(categoryPath))
                {
                    string warnTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    L.LogWarning($"[{warnTime}] {category} 分类目录不存在: {categoryPath}");
                    continue;
                }

                // 筛选支持的音频格式（wav/ogg）
                var audioFiles = Directory.GetFiles(categoryPath, "*.*")
                    .Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (audioFiles.Count == 0)
                {
                    string warnTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    L.LogWarning($"[{warnTime}] {category} 分类无可用音频文件 (支持 .wav/.ogg)");
                    continue;
                }

                string loadTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                L.LogInfo($"[{loadTime}] 发现 {category} 分类音频: {audioFiles.Count} 个");

                if (_cfgEnableDebugLogging.Value)
                {
                    L.LogDebug($"[{loadTime}] {category} 音频文件: {string.Join(", ", audioFiles.Select(Path.GetFileName))}");
                }

                // 加入加载队列
                foreach (var file in audioFiles)
                {
                    _loadQueue.Enqueue((file, category));
                }
            }
        }

        // 处理音频加载队列（并发控制）
        private void ProcessAudioLoadQueue()
        {
            // 控制最大并发加载数
            while (_inflightLoads.Count < MaxInflightLoads && _loadQueue.Count > 0)
            {
                var (path, category) = _loadQueue.Dequeue();
                string fileUri = $"file://{path.Replace('\\', '/')}";
                AudioType audioType = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".wav" => AudioType.WAV,
                    ".ogg" => AudioType.OGGVORBIS,
                    _ => AudioType.UNKNOWN
                };

                var request = UnityWebRequestMultimedia.GetAudioClip(fileUri, audioType);
                request.SendWebRequest();
                _inflightLoads.Add(new PendingLoad { path = path, category = category, request = request });

                if (_cfgEnableDebugLogging.Value)
                {
                    string loadTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                    L.LogDebug($"[{loadTime}] 开始加载音频: {Path.GetFileName(path)} → {category} (并发数: {_inflightLoads.Count})");
                }
            }

            // 处理已完成的加载请求
            for (int i = _inflightLoads.Count - 1; i >= 0; i--)
            {
                var load = _inflightLoads[i];
                if (load.request.isDone)
                {
                    string fileName = Path.GetFileName(load.path);
                    string completeTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");

                    if (string.IsNullOrEmpty(load.request.error))
                    {
                        AudioClip clip = DownloadHandlerAudioClip.GetContent(load.request);
                        if (clip != null)
                        {
                            clip.name = fileName;
                            UnityEngine.Object.DontDestroyOnLoad(clip);
                            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
                            _audioClips[load.category].Add(clip);

                            // 加载完成后重置该阶段索引
                            ResetAvailableIndicesForStage(load.category);

                            L.LogInfo($"[{completeTime}] 成功加载音频: {fileName} → {load.category} (总计数: {_audioClips[load.category].Count})");
                        }
                        else
                        {
                            L.LogError($"[{completeTime}] 加载音频失败: {fileName} (无法解析为有效音频)");
                        }
                    }
                    else
                    {
                        L.LogError($"[{completeTime}] 加载音频失败: {fileName} - {load.request.error}");
                    }

                    load.request.Dispose();
                    _inflightLoads.RemoveAt(i);
                }
            }
        }

        // 确保音频源有效
        private void EnsureAudioSourceValid()
        {
            if (_audioHost == null || !_audioHost)
            {
                _audioHost = new GameObject("VoicePack_AudioHost");
                GameObject.DontDestroyOnLoad(_audioHost);
                if (_cfgEnableDebugLogging.Value)
                {
                    L.LogDebug($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 重建音频宿主对象");
                }
            }

            if (_audioSource == null || !_audioSource)
            {
                _audioSource = _audioHost.GetComponent<AudioSource>() ?? _audioHost.AddComponent<AudioSource>();
                _audioSource.playOnAwake = false;
                _audioSource.spatialBlend = 0f;
                if (_cfgEnableDebugLogging.Value)
                {
                    L.LogDebug($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 重建音频源组件");
                }
            }
        }

        // 播放当前阶段音频（含high_long插播逻辑）
        private void PlayCurrentLevelAudio(float unityTime, float currentEcstasy)
        {
            string levelName = GetLevelName(_currentLevel);
            if (levelName == "high")
            {
                // High阶段：播放2-3条后随机插播high_long
                if (_highPlayCount >= _highLongTriggerThreshold && _audioClips["high_long"].Count > 0)
                {
                    // 50%概率插播
                    if (_rng.Next(2) == 0)
                    {
                        L.LogInfo($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [UnityTime: {unityTime:F3}s] High阶段插播high_long (已播放 {_highPlayCount} 条high音频)");
                        PlayAudio("high_long", unityTime);
                        _lastHighLongPlayed = unityTime;
                        // 重置计数和阈值
                        _highPlayCount = 0;
                        _highLongTriggerThreshold = _rng.Next(MinHighBeforeLong, MaxHighBeforeLong + 1);
                        return;
                    }
                    else
                    {
                        if (_cfgEnablePlaybackDebug.Value)
                        {
                            L.LogInfo($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [UnityTime: {unityTime:F3}s] High阶段不插播high_long (已播放 {_highPlayCount} 条)");
                        }
                        _highPlayCount = 0;
                        _highLongTriggerThreshold = _rng.Next(MinHighBeforeLong, MaxHighBeforeLong + 1);
                    }
                }

                // 播放high音频
                PlayAudio("high", unityTime);
                _lastHighPlayed = unityTime;
                _highPlayCount++;
                if (_cfgEnablePlaybackDebug.Value)
                {
                    L.LogInfo($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [UnityTime: {unityTime:F3}s] High阶段播放计数: {_highPlayCount}/{_highLongTriggerThreshold}");
                }
            }
            else
            {
                // 其他阶段直接播放
                PlayAudio(levelName, unityTime);
                // 更新对应阶段最后播放时间
                switch (levelName)
                {
                    case "low":
                        _lastLowPlayed = unityTime;
                        break;
                    case "med":
                        _lastMedPlayed = unityTime;
                        break;
                    case "climax":
                        _lastClimaxPlayed = unityTime;
                        break;
                }
            }
        }

        // 播放指定分类音频
        private void PlayAudio(string category, float unityTime)
        {
            // 检查分类是否有可用音频
            if (!_audioClips.ContainsKey(category) || _audioClips[category].Count == 0)
            {
                if (!_warnedEmptyCategories.Contains(category))
                {
                    _warnedEmptyCategories.Add(category);
                    L.LogWarning($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [UnityTime: {unityTime:F3}s] {category} 分类无可用音频");
                }
                // 尝试重新加载
                if (unityTime - _lastReloadAttempt[category] > 5f)
                {
                    _lastReloadAttempt[category] = unityTime;
                    EnsureAudioPreloaded();
                }
                return;
            }

            // 检查索引列表，为空则重置
            var indices = _availableClipIndicesPerStage[category];
            if (indices.Count == 0)
            {
                ResetAvailableIndicesForStage(category);
                L.LogInfo($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [UnityTime: {unityTime:F3}s] {category} 音频轮询完毕，重置列表");
                indices = _availableClipIndicesPerStage[category];
            }

            // 取第一个索引并移除（轮询）
            int selectedIndex = indices[0];
            indices.RemoveAt(0);
            AudioClip clip = _audioClips[category][selectedIndex];

            if (clip != null)
            {
                _audioSource.PlayOneShot(clip);
                _lastAnyAudioPlayed = unityTime;

                string playTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                L.LogInfo($"[{playTime}] [UnityTime: {unityTime:F3}s] 播放 {category} 音频: {clip.name} (长度: {clip.length:F2}s, 剩余轮询数: {indices.Count})");
            }
            else
            {
                L.LogWarning($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [UnityTime: {unityTime:F3}s] {category} 分类音频 {selectedIndex} 无效");
            }
        }

        // 设置下次播放时间（仅用基础间隔+随机偏移）
        private void SetNextPlayTime(float unityTime)
        {
            string levelName = GetLevelName(_currentLevel);
            // 获取当前阶段基础间隔
            float baseInterval = levelName switch
            {
                "low" => _cfgLowBaseInterval.Value,
                "med" => _cfgMedBaseInterval.Value,
                "high" => _cfgHighBaseInterval.Value,
                "high_long" => _cfgHighLongBaseInterval.Value,
                "climax" => _cfgClimaxCooldown.Value,
                _ => 1f
            };

            // 加入±0.05s随机偏移，避免播放过于机械
            float randomOffset = (float)(_rng.NextDouble() * 0.1f - 0.05f);
            float nextInterval = Mathf.Max(baseInterval + randomOffset, _cfgMinPlayInterval.Value);
            _nextPlayTime = unityTime + nextInterval;

            if (_cfgEnablePlaybackDebug.Value)
            {
                string logTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                L.LogDebug($"[{logTime}] [UnityTime: {unityTime:F3}s] 设定下次播放时间: {_nextPlayTime:F3}s (基础间隔: {baseInterval:F2}s, 偏移: {randomOffset:F3}s)");
            }
        }
        #endregion

        #region 辅助方法
        // 根据Ecstasy值获取阶段
        private int GetLevelFromEcstasy(float ecstasy)
        {
            if (ecstasy >= _cfgClimaxThreshold.Value) return 3;
            if (ecstasy >= _cfgHighThreshold.Value) return 2;
            if (ecstasy >= _cfgMedThreshold.Value) return 1;
            if (ecstasy >= _cfgLowThreshold.Value) return 0;
            return -1;
        }

        // 根据阶段获取名称
        private string GetLevelName(int level)
        {
            return level switch
            {
                0 => "low",
                1 => "med",
                2 => "high",
                3 => "climax",
                _ => "none"
            };
        }

        // 获取阶段阈值
        private float GetLevelThreshold(int level)
        {
            return level switch
            {
                0 => _cfgLowThreshold.Value,
                1 => _cfgMedThreshold.Value,
                2 => _cfgHighThreshold.Value,
                3 => _cfgClimaxThreshold.Value,
                _ => 0f
            };
        }

        // 尝试从Controller获取Ecstasy
        private void TryResolveControllerEcstasyGetter(Type controllerType)
        {
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
                // 优先匹配指定名称
                var ecstasyProp = controllerType.GetProperty(PreferredEcstasyMember, flags);
                if (ecstasyProp != null && (ecstasyProp.PropertyType == typeof(float) || ecstasyProp.PropertyType == typeof(double)))
                {
                    var getMethod = ecstasyProp.GetGetMethod(true);
                    _controllerEcstasyGetter = obj => Convert.ToSingle(getMethod.Invoke(obj, null));
                    L.LogInfo($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 绑定ControllerController Ecstasy属性: {controllerType.Name}.{ecstasyProp.Name}");
                    return;
                }

                var ecstasyField = controllerType.GetField(PreferredEcstasyMember, flags);
                if (ecstasyField != null && (ecstasyField.FieldType == typeof(float) || ecstasyField.FieldType == typeof(double)))
                {
                    _controllerEcstasyGetter = obj => Convert.ToSingle(ecstasyField.GetValue(obj));
                    L.LogInfo($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 绑定Controller Ecstasy字段: {controllerType.Name}.{ecstasyField.Name}");
                    return;
                }

                // 模糊匹配含"Ecstasy"且不含"Time"的成员
                foreach (var prop in controllerType.GetProperties(flags))
                {
                    if (prop.PropertyType != typeof(float) && prop.PropertyType != typeof(double)) continue;
                    bool hasEcstasy = prop.Name.IndexOf("Ecstasy", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool hasTime = prop.Name.IndexOf("Time", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hasEcstasy && !hasTime)
                    {
                        var getMethod = prop.GetGetMethod(true);
                        if (getMethod != null)
                        {
                            _controllerEcstasyGetter = obj => Convert.ToSingle(getMethod.Invoke(obj, null));
                            L.LogWarning($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 模糊绑定Controller属性: {controllerType.Name}.{prop.Name}");
                            return;
                        }
                    }
                }

                foreach (var field in controllerType.GetFields(flags))
                {
                    if (field.FieldType != typeof(float) && field.FieldType != typeof(double)) continue;
                    bool hasEcstasy = field.Name.IndexOf("Ecstasy", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool hasTime = field.Name.IndexOf("Time", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (hasEcstasy && !hasTime)
                    {
                        _controllerEcstasyGetter = obj => Convert.ToSingle(field.GetValue(obj));
                        L.LogWarning($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 模糊绑定Controller字段: {controllerType.Name}.{field.Name}");
                        return;
                    }
                }

                L.LogWarning($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 未在Controller找到有效Ecstasy成员");
            }
            catch (Exception ex)
            {
                string errorTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                L.LogError($"[{errorTime}] 解析Controller Ecstasy失败: {ex.Message}");
                if (_cfgEnableDebugLogging.Value)
                {
                    L.LogDebug($"[{errorTime}] 异常详情: {ex.StackTrace}");
                }
            }
        }

        // 深度模糊搜索Ecstasy
        private void TryFuzzyEcstasySearch(object root)
        {
            if (root == null) return;
            try
            {
                var visited = new HashSet<object>(new ReferenceEqualityComparer());
                var queue = new Queue<object>();
                queue.Enqueue(root);
                visited.Add(root);

                while (queue.Count > 0)
                {
                    var current = queue.Dequeue();
                    if (current == null) continue;
                    Type type = current.GetType();

                    // 跳过基础类型和数组
                    if (type.IsPrimitive || type == typeof(string) || type.Name.Contains("Il2CppReferenceArray") || type.Name.Contains("Il2CppArray"))
                    {
                        continue;
                    }

                    var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                    // 搜索属性
                    foreach (var prop in type.GetProperties(flags))
                    {
                        try
                        {
                            if (prop.GetIndexParameters().Length != 0) continue;
                            var getMethod = prop.GetGetMethod(true);
                            if (getMethod == null) continue;
                            object value = getMethod.Invoke(current, null);
                            if (value == null) continue;

                            // 匹配float/double类型且含"Ecstasy"不含"Time"
                            if ((prop.PropertyType == typeof(float) || prop.PropertyType == typeof(double)))
                            {
                                bool hasEcstasy = prop.Name.IndexOf("Ecstasy", StringComparison.OrdinalIgnoreCase) >= 0;
                                bool hasTime = prop.Name.IndexOf("Time", StringComparison.OrdinalIgnoreCase) >= 0;
                                if (hasEcstasy && !hasTime)
                                {
                                    _fallbackEcstasyGetter = () => Convert.ToSingle(getMethod.Invoke(current, null));
                                    L.LogWarning($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 深度模糊匹配: {type.FullName}.{prop.Name}");
                                    return;
                                }
                            }

                            // 递归搜索引用类型
                            if (!visited.Contains(value) && !IsPrimitiveType(value.GetType()))
                            {
                                visited.Add(value);
                                queue.Enqueue(value);
                            }
                        }
                        catch { continue; }
                    }

                    // 搜索字段
                    foreach (var field in type.GetFields(flags))
                    {
                        try
                        {
                            object value = field.GetValue(current);
                            if (value == null) continue;

                            // 匹配float/double类型且含"Ecstasy"不含"Time"
                            if ((field.FieldType == typeof(float) || field.FieldType == typeof(double)))
                            {
                                bool hasEcstasy = field.Name.IndexOf("Ecstasy", StringComparison.OrdinalIgnoreCase) >= 0;
                                bool hasTime = field.Name.IndexOf("Time", StringComparison.OrdinalIgnoreCase) >= 0;
                                if (hasEcstasy && !hasTime)
                                {
                                    _fallbackEcstasyGetter = () => Convert.ToSingle(field.GetValue(current));
                                    L.LogWarning($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] 深度模糊匹配: {type.FullName}.{field.Name}");
                                    return;
                                }
                            }

                            // 递归搜索引用类型
                            if (!visited.Contains(value) && !IsPrimitiveType(value.GetType()))
                            {
                                visited.Add(value);
                                queue.Enqueue(value);
                            }
                        }
                        catch { continue; }
                    }
                }
            }
            catch (Exception ex)
            {
                string errorTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                L.LogError($"[{errorTime}] 深度模糊搜索失败: {ex.Message}");
                if (_cfgEnableDebugLogging.Value)
                {
                    L.LogDebug($"[{errorTime}] 异常详情: {ex.StackTrace}");
                }
            }
        }

        // 判断是否为基础类型
        private bool IsPrimitiveType(Type type)
        {
            return type.IsPrimitive || type == typeof(string) || type.IsEnum || type.IsValueType;
        }

        // 限流日志输出
        private void LogNotFoundThrottled(string message)
        {
            float now = Time.realtimeSinceStartup;
            if (now - _lastNotFoundLog >= 5f)
            {
                _lastNotFoundLog = now;
                L.LogWarning($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [UnityTime: {now:F3}s] {message}");
            }
        }

        // 直接设置Ecstasy值（供外部调用）
        internal void DirectFeedEcstasy(float value)
        {
            string feedTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            float unityTime = Time.realtimeSinceStartup;
            CurrentPlayerEcstasy = Mathf.Clamp01(value);
            _lastDirectEcstasyFeed = unityTime;

            if (_cfgEnableDebugLogging.Value)
            {
                L.LogDebug($"[{feedTime}] [UnityTime: {unityTime:F3}s] 直接设置Ecstasy值: {value:F4}");
            }
        }
        #endregion

        #region Harmony回调与工具类
        public static void OnPlayerEcstasyControllerUpdate(object __instance)
        {
            Instance?.Tick(__instance);
        }

        // 引用相等比较器
        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
        }
        #endregion
    }
}