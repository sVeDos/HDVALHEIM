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
        public const string PluginVersion = "0.3.1";

        internal static ManualLogSource Log = null!;

        private ConfigEntry<bool> _enabled = null!;
        private ConfigEntry<string> _textureDirectory = null!;
        private ConfigEntry<float> _loadDelay = null!;
        private ConfigEntry<KeyboardShortcut> _reloadHotkey = null!;
        private ConfigEntry<bool> _logEachReplacement = null!;
        private ConfigEntry<bool> _caseInsensitiveNames = null!;
        private ConfigEntry<bool> _generateMipMaps = null!;
        private ConfigEntry<int> _materialsPerFrame = null!;
        private ConfigEntry<bool> _repackNormalMaps = null!;

        private Coroutine? _loadRoutine;

        // Храним только пути: 2+ ГБ изображений не загружаются в RAM заранее.
        private readonly Dictionary<string, string> _texturePaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Кэш учитывает тип карты, потому что один и тот же файл теоретически
        // может быть назначен как цветовая и как linear/normal карта.
        private readonly Dictionary<string, Texture2D> _loadedTextures =
            new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

        private void Awake()
        {
            Log = Logger;

            _enabled = Config.Bind("General", "Enabled", true,
                "Включить подмену HD-текстур.");

            _textureDirectory = Config.Bind("General", "TextureDirectory", "Textures",
                "Папка с текстурами относительно папки плагина. PNG/JPG.");

            _loadDelay = Config.Bind("General", "LoadDelaySeconds", 8.0f,
                "Задержка после загрузки сцены перед поиском материалов.");

            _reloadHotkey = Config.Bind("General", "ReloadHotkey",
                new KeyboardShortcut(KeyCode.F8),
                "Повторно просканировать материалы и применить HD-текстуры.");

            _logEachReplacement = Config.Bind("Logging", "LogEachReplacement", false,
                "Писать в лог каждую замену.");

            _caseInsensitiveNames = Config.Bind("Matching", "CaseInsensitiveNames", true,
                "Сопоставлять имена без учёта регистра.");

            _generateMipMaps = Config.Bind("Performance", "GenerateMipMaps", true,
                "Генерировать mipmaps у новых HD-текстур.");

            _materialsPerFrame = Config.Bind("Performance", "MaterialsPerFrame", 25,
                "Сколько материалов обрабатывать за кадр. Меньше = меньше фризов.");

            _repackNormalMaps = Config.Bind("Rendering", "RepackNormalMaps", false,
                "Перепаковывать RGB normal maps из PNG в формат, который ожидают Unity/Valheim shaders.");

            SceneManager.sceneLoaded += OnSceneLoaded;

            Logger.LogInfo($"{PluginName} {PluginVersion} loaded.");
            ScheduleReload();
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void Update()
        {
            if (_enabled.Value && _reloadHotkey.Value.IsDown())
            {
                Logger.LogInfo("Manual texture rescan requested.");
                ScheduleReload();
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_enabled.Value)
                ScheduleReload();
        }

        private void ScheduleReload()
        {
            if (!_enabled.Value)
                return;

            if (_loadRoutine != null)
                StopCoroutine(_loadRoutine);

            _loadRoutine = StartCoroutine(ReloadDelayed());
        }

        private IEnumerator ReloadDelayed()
        {
            yield return new WaitForSecondsRealtime(Mathf.Max(0f, _loadDelay.Value));

            BuildTexturePathIndex();

            if (_texturePaths.Count == 0)
            {
                Logger.LogWarning("No PNG/JPG textures found. HD pass skipped.");
                _loadRoutine = null;
                yield break;
            }

            yield return StartCoroutine(ReplaceMaterialTexturesCoroutine());
            _loadRoutine = null;
        }

        private void BuildTexturePathIndex()
        {
            _texturePaths.Clear();

            string pluginDir = Path.GetDirectoryName(Info.Location) ?? Paths.PluginPath;
            string texDir = _textureDirectory.Value;

            if (!Path.IsPathRooted(texDir))
                texDir = Path.Combine(pluginDir, texDir);

            if (!Directory.Exists(texDir))
            {
                Directory.CreateDirectory(texDir);
                Logger.LogWarning($"Texture folder created: {texDir}");
                return;
            }

            foreach (string path in Directory.GetFiles(texDir, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".png" && ext != ".jpg" && ext != ".jpeg")
                    continue;

                string name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                _texturePaths[name] = path;
            }

            Logger.LogInfo($"Indexed {_texturePaths.Count} HD texture files.");
        }

        private IEnumerator ReplaceMaterialTexturesCoroutine()
        {
            Material[] materials = Resources.FindObjectsOfTypeAll<Material>();

            int scannedMaterials = 0;
            int scannedSlots = 0;
            int matchedSlots = 0;
            int replacedSlots = 0;
            int failedSlots = 0;
            int normalSlots = 0;
            int linearSlots = 0;
            int processedThisFrame = 0;

            foreach (Material material in materials)
            {
                if (material == null)
                    continue;

                scannedMaterials++;
                processedThisFrame++;

                string[] textureProperties;
                try
                {
                    textureProperties = material.GetTexturePropertyNames();
                }
                catch
                {
                    textureProperties = Array.Empty<string>();
                }

                foreach (string propertyName in textureProperties)
                {
                    Texture? sourceTexture;

                    try
                    {
                        sourceTexture = material.GetTexture(propertyName);
                    }
                    catch
                    {
                        continue;
                    }

                    if (sourceTexture == null || string.IsNullOrWhiteSpace(sourceTexture.name))
                        continue;

                    scannedSlots++;

                    string sourceName = sourceTexture.name;
                    string? imagePath = FindTexturePath(sourceName);

                    if (string.IsNullOrEmpty(imagePath))
                        continue;

                    matchedSlots++;

                    MapKind kind = ClassifyMap(propertyName, sourceName);
                    if (kind == MapKind.Normal) normalSlots++;
                    if (kind != MapKind.Color) linearSlots++;

                    try
                    {
                        Texture2D replacement = GetOrLoadReplacement(
                            sourceName,
                            imagePath,
                            sourceTexture as Texture2D,
                            kind);

                        material.SetTexture(propertyName, replacement);
                        replacedSlots++;

                        if (_logEachReplacement.Value)
                        {
                            Logger.LogInfo(
                                $"Replaced material '{material.name}' property '{propertyName}' " +
                                $"using '{sourceName}' as {kind}.");
                        }
                    }
                    catch (Exception ex)
                    {
                        failedSlots++;
                        Logger.LogWarning(
                            $"Failed material '{material.name}' property '{propertyName}' / " +
                            $"'{sourceName}': {ex.Message}");
                    }
                }

                if (processedThisFrame >= Mathf.Max(1, _materialsPerFrame.Value))
                {
                    processedThisFrame = 0;
                    yield return null;
                }
            }

            Logger.LogInfo(
                $"HD material pass complete. Files={_texturePaths.Count}, " +
                $"materials={scannedMaterials}, slots={scannedSlots}, matched={matchedSlots}, " +
                $"replaced={replacedSlots}, failed={failedSlots}, " +
                $"normalSlots={normalSlots}, linearSlots={linearSlots}, loadedHD={_loadedTextures.Count}");
        }

        private string? FindTexturePath(string sourceName)
        {
            if (_texturePaths.TryGetValue(sourceName, out string? path))
                return path;

            if (!_caseInsensitiveNames.Value)
                return null;

            foreach (var pair in _texturePaths)
            {
                if (string.Equals(pair.Key, sourceName, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }

            return null;
        }

        private enum MapKind
        {
            Color,
            Linear,
            Normal
        }

        private static MapKind ClassifyMap(string propertyName, string textureName)
        {
            string p = propertyName.ToLowerInvariant();
            string n = textureName.ToLowerInvariant();

            // Normal maps: по shader property и по распространённому суффиксу старого HD-пака.
            if (p.Contains("bump") || p.Contains("normal") ||
                n.EndsWith("_n") || n.EndsWith("_normal") || n.Contains("normal"))
                return MapKind.Normal;

            // Данные, которые нельзя интерпретировать через sRGB.
            if (p.Contains("metal") || p.Contains("mask") || p.Contains("occlusion") ||
                p.Contains("rough") || p.Contains("smooth") || p.Contains("height") ||
                p.Contains("parallax") || p.Contains("detailmask") ||
                n.EndsWith("_m") || n.EndsWith("_mask") || n.EndsWith("_e") ||
                n.Contains("metal") || n.Contains("rough") || n.Contains("occlusion"))
                return MapKind.Linear;

            return MapKind.Color;
        }

        private Texture2D GetOrLoadReplacement(
            string textureName,
            string imagePath,
            Texture2D? source,
            MapKind kind)
        {
            string cacheKey = $"{kind}:{textureName}";

            if (_loadedTextures.TryGetValue(cacheKey, out Texture2D? cached) && cached != null)
                return cached;

            byte[] bytes = File.ReadAllBytes(imagePath);

            bool linear = kind != MapKind.Color;

            // В отличие от v0.1 мы не изменяем non-readable Texture2D игры.
            // Создаём новый объект. Для normal/mask/metal/occlusion используем linear=true.
            Texture2D replacement = new Texture2D(
                2,
                2,
                TextureFormat.RGBA32,
                _generateMipMaps.Value,
                linear);

            replacement.name = textureName;

            if (!ImageConversion.LoadImage(replacement, bytes, false))
            {
                UnityEngine.Object.Destroy(replacement);
                throw new InvalidOperationException("ImageConversion.LoadImage returned false.");
            }

            if (kind == MapKind.Normal && _repackNormalMaps.Value)
                RepackNormalMapForUnity(replacement);

            if (source != null)
            {
                replacement.filterMode = source.filterMode;
                replacement.wrapMode = source.wrapMode;
                replacement.wrapModeU = source.wrapModeU;
                replacement.wrapModeV = source.wrapModeV;
                replacement.anisoLevel = source.anisoLevel;
                replacement.mipMapBias = source.mipMapBias;
            }

            if (_generateMipMaps.Value)
                replacement.Apply(true, false);

            _loadedTextures[cacheKey] = replacement;
            return replacement;
        }

        private static void RepackNormalMapForUnity(Texture2D texture)
        {
            // AssetRipper экспортирует normal map как обычное RGB-изображение.
            // В desktop-шейдерах Unity UnpackNormal часто читает X из A и Y из G.
            // Поэтому переносим R -> A, сохраняем G, а R/B ставим в 1.
            // Это совместимо с UnpackNormalmapRGorAG: normal.xy = packed.wy,
            // normal.x *= packed.x; при R=1 X берётся из A.
            Color32[] pixels = texture.GetPixels32();

            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                pixels[i] = new Color32(255, c.g, 255, c.r);
            }

            texture.SetPixels32(pixels);
            texture.Apply(true, false);
        }
    }
}
