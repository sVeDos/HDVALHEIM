using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HDValheimUpdated
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "lidia.hdvalheimupdated";
        public const string PluginName = "HD Valheim Updated";
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log = null!;

        private ConfigEntry<bool> _enabled = null!;
        private ConfigEntry<string> _textureDirectory = null!;
        private ConfigEntry<float> _loadDelay = null!;
        private ConfigEntry<KeyboardShortcut> _reloadHotkey = null!;
        private ConfigEntry<bool> _logEachReplacement = null!;
        private ConfigEntry<bool> _caseInsensitiveNames = null!;
        private ConfigEntry<int> _maxTextureSize = null!;
        private ConfigEntry<bool> _generateMipMaps = null!;

        private Coroutine? _loadRoutine;
        private readonly Dictionary<string, byte[]> _replacementCache =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        private void Awake()
        {
            Log = Logger;
            _enabled = Config.Bind("General", "Enabled", true, "Включить подмену HD-текстур.");
            _textureDirectory = Config.Bind("General", "TextureDirectory", "Textures", "Папка с текстурами относительно папки плагина. PNG/JPG.");
            _loadDelay = Config.Bind("General", "LoadDelaySeconds", 5.0f, "Задержка после загрузки сцены перед поиском текстур.");
            _reloadHotkey = Config.Bind("General", "ReloadHotkey", new KeyboardShortcut(KeyCode.F8), "Горячая клавиша повторной загрузки текстур.");
            _logEachReplacement = Config.Bind("Logging", "LogEachReplacement", false, "Писать в лог каждую заменённую текстуру.");
            _caseInsensitiveNames = Config.Bind("Matching", "CaseInsensitiveNames", true, "Сопоставлять имена текстур без учёта регистра.");
            _maxTextureSize = Config.Bind("Performance", "MaxTextureSize", 0, "0 = без ограничения; иначе максимум стороны текстуры.");
            _generateMipMaps = Config.Bind("Performance", "GenerateMipMaps", true, "Создавать mip-map уровни после загрузки.");

            SceneManager.sceneLoaded += OnSceneLoaded;
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
            ScheduleReload();
        }

        private void OnDestroy() => SceneManager.sceneLoaded -= OnSceneLoaded;

        private void Update()
        {
            if (_enabled.Value && _reloadHotkey.Value.IsDown())
            {
                Logger.LogInfo("Manual texture reload requested.");
                ScheduleReload();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_enabled.Value) ScheduleReload();
        }

        private void ScheduleReload()
        {
            if (!_enabled.Value) return;
            if (_loadRoutine != null) StopCoroutine(_loadRoutine);
            _loadRoutine = StartCoroutine(ReloadDelayed());
        }

        private IEnumerator ReloadDelayed()
        {
            yield return new WaitForSecondsRealtime(Mathf.Max(0f, _loadDelay.Value));
            try { ReloadTextures(); }
            catch (Exception ex) { Logger.LogError($"Texture reload failed: {ex}"); }
            _loadRoutine = null;
        }

        private void ReloadTextures()
        {
            string pluginDir = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
            string texDir = _textureDirectory.Value;
            if (!Path.IsPathRooted(texDir)) texDir = Path.Combine(pluginDir, texDir);

            if (!Directory.Exists(texDir))
            {
                Directory.CreateDirectory(texDir);
                Logger.LogWarning($"Texture folder created: {texDir}");
                return;
            }

            BuildReplacementCache(texDir);
            if (_replacementCache.Count == 0)
            {
                Logger.LogWarning($"No PNG/JPG textures found in: {texDir}");
                return;
            }

            Texture2D[] liveTextures = Resources.FindObjectsOfTypeAll<Texture2D>();
            var comparer = _caseInsensitiveNames.Value ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var byName = liveTextures
                .Where(t => t != null && !string.IsNullOrWhiteSpace(t.name))
                .GroupBy(t => t.name, comparer)
                .ToDictionary(g => g.Key, g => g.ToList(), comparer);

            int matched = 0, replaced = 0, failed = 0;

            foreach (var pair in _replacementCache)
            {
                if (!byName.TryGetValue(pair.Key, out var targets)) continue;
                matched++;

                foreach (var target in targets)
                {
                    try
                    {
                        ReplaceTextureInPlace(target, pair.Value);
                        replaced++;
                        if (_logEachReplacement.Value)
                            Logger.LogInfo($"Replaced: {target.name} ({target.width}x{target.height})");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Logger.LogWarning($"Failed '{target.name}': {ex.Message}");
                    }
                }
            }

            Logger.LogInfo($"HD pass complete. Files={_replacementCache.Count}, matched={matched}, replaced={replaced}, failed={failed}, scanned={liveTextures.Length}");
        }

        private void BuildReplacementCache(string texDir)
        {
            _replacementCache.Clear();
            foreach (string path in Directory.GetFiles(texDir, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") continue;

                string key = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(key)) continue;

                try { _replacementCache[key] = File.ReadAllBytes(path); }
                catch (Exception ex) { Logger.LogWarning($"Cannot read '{path}': {ex.Message}"); }
            }
        }

        private void ReplaceTextureInPlace(Texture2D target, byte[] encodedImage)
        {
            var filter = target.filterMode;
            var wrapU = target.wrapModeU;
            var wrapV = target.wrapModeV;
            int aniso = target.anisoLevel;
            float mipBias = target.mipMapBias;

            if (!ImageConversion.LoadImage(target, encodedImage, false))
                throw new InvalidOperationException("ImageConversion.LoadImage returned false.");

            target.filterMode = filter;
            target.wrapModeU = wrapU;
            target.wrapModeV = wrapV;
            target.anisoLevel = aniso;
            target.mipMapBias = mipBias;
            target.Apply(_generateMipMaps.Value, false);
        }
    }
}
