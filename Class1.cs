using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace WinterBurrowZoomMod {
    [BepInPlugin("tian.winterburrow.zoommod", "Winter Burrow Zoom Mod", "12.0.0")]
    public class ZoomModPlugin : BaseUnityPlugin {
        private void Awake() {
            Logger.LogInfo("[ZoomMod] Plugin Awake() – initializing Harmony zoom core (SRP hook).");
            ZoomCore.Initialize(Logger);
        }

        private void OnEnable() {
            Logger.LogInfo("[ZoomMod] Plugin OnEnable().");
        }

        private void OnDisable() {
            Logger.LogInfo("[ZoomMod] Plugin OnDisable(). (Harmony patch stays active)");
        }
    }

    internal static class ZoomCore {
        private static ManualLogSource _log;
        private static Harmony _harmony;
        private static bool _initialized;

        private static Camera _worldCamera;

        private static float _baseOrthoSize = -1f;
        private static float _baseFov = -1f;

        // Zoom level: 1.0 = default, >1 = zoom in (closer), <1 = zoom out (farther)
        private static float _zoomLevel = 1.0f;

        private const float MinZoomLevel = 0.3f;
        private const float MaxZoomLevel = 4.0f;
        private const float ZoomStep = 0.1f;

        private static float _lastTickLogTime = 0f;
        private static float _lastApplyLogTime = 0f;

        private static bool _renderLoopPatched = false;
        private static bool _inputBroken = false;

        public static void Initialize(ManualLogSource logger) {
            if (_initialized)
                return;

            _initialized = true;
            _log = logger;

            _log.LogInfo("[ZoomMod] Initialize() called (SRP render loop patch).");
            _log.LogInfo($"[ZoomMod] Config: MinZoomLevel={MinZoomLevel}, MaxZoomLevel={MaxZoomLevel}, ZoomStep={ZoomStep}, initial zoomLevel={_zoomLevel}");

            _baseOrthoSize = -1f;
            _baseFov = -1f;

            try {
                _harmony = new Harmony("tian.winterburrow.zoommod");

                var rpType = AccessTools.TypeByName("UnityEngine.Rendering.RenderPipelineManager");
                if (rpType == null) {
                    _log.LogError("[ZoomMod] Could not find UnityEngine.Rendering.RenderPipelineManager. Zoom will not work.");
                    return;
                }

                // Don't specify parameter types – HarmonyX will find the right overload
                var doRenderLoop = AccessTools.Method(rpType, "DoRenderLoop_Internal");
                if (doRenderLoop == null) {
                    _log.LogError("[ZoomMod] Could not find DoRenderLoop_Internal method. Zoom will not work.");
                    return;
                }

                var prefix = new HarmonyMethod(typeof(ZoomCore)
                    .GetMethod(nameof(RenderLoop_Prefix), BindingFlags.Static | BindingFlags.NonPublic));

                _harmony.Patch(doRenderLoop, prefix: prefix);
                _renderLoopPatched = true;

                _log.LogInfo("[ZoomMod] Harmony patch applied to RenderPipelineManager.DoRenderLoop_Internal.");
            } catch (Exception ex) {
                _log.LogError("[ZoomMod] Error while applying Harmony patch: " + ex);
            }
        }

        // Runs once per frame under SRP/URP.
        private static void RenderLoop_Prefix(object __0, IntPtr __1) {
            if (!_renderLoopPatched)
                return;

            // heartbeat log
            if (Time.unscaledTime - _lastTickLogTime > 3.0f) {
                _lastTickLogTime = Time.unscaledTime;
                _log?.LogInfo($"[ZoomMod] RenderLoop tick at t={Time.unscaledTime:0.00}, zoomLevel={_zoomLevel:0.000}, worldCam={(_worldCamera ? _worldCamera.name : "null")}");
            }

            EnsureWorldCamera();
            HandleInput();

            if (_worldCamera != null) {
                ApplyZoom(_worldCamera);
            }
        }

        private static void EnsureWorldCamera() {
            if (_worldCamera != null && _worldCamera.enabled)
                return;

            Camera cam = null;

            // 1) Prefer Camera.main if it looks like a world camera
            if (Camera.main != null && !IsUICamera(Camera.main)) {
                cam = Camera.main;
            }

            // 2) Otherwise search all cameras for one that looks like a world camera
            if (cam == null && Camera.allCamerasCount > 0) {
                var all = new Camera[Camera.allCamerasCount];
                Camera.GetAllCameras(all);

                // Prefer non-UI cameras, with highest depth
                float bestDepth = float.NegativeInfinity;
                foreach (var c in all) {
                    if (c == null || !c.enabled)
                        continue;
                    if (IsUICamera(c))
                        continue;

                    if (c.depth >= bestDepth) {
                        bestDepth = c.depth;
                        cam = c;
                    }
                }

                // If we still didn't find anything, just fall back to the first camera
                if (cam == null && all.Length > 0) {
                    cam = all[0];
                }
            }

            if (cam == null)
                return;

            _worldCamera = cam;
            _baseOrthoSize = -1f;
            _baseFov = -1f;

            if (_worldCamera.orthographic) {
                _log?.LogInfo($"[ZoomMod] World camera set to '{_worldCamera.name}' (ORTHOGRAPHIC). Base size will be captured on first ApplyZoom().");
            } else {
                _log?.LogInfo($"[ZoomMod] World camera set to '{_worldCamera.name}' (PERSPECTIVE). Base FOV will be captured on first ApplyZoom().");
            }
        }

        private static bool IsUICamera(Camera cam) {
            if (cam == null) return false;
            var name = cam.name.ToLowerInvariant();
            // very crude filter, but usually enough
            return name.Contains("ui") || name.Contains("menu") || name.Contains("overlay");
        }

        private static void HandleInput() {
            if (_inputBroken)
                return;

            bool changed = false;

            try {
                // Zoom OUT (farther): '-' or Keypad '-'
                if (Input.GetKeyDown(KeyCode.Minus) || Input.GetKeyDown(KeyCode.KeypadMinus)) {
                    _zoomLevel -= ZoomStep; // lowering zoomLevel -> larger FOV -> zoom out
                    _zoomLevel = Mathf.Clamp(_zoomLevel, MinZoomLevel, MaxZoomLevel);
                    changed = true;
                    _log?.LogInfo($"[ZoomMod] Keyboard zoom OUT (-) pressed, zoomLevel={_zoomLevel:0.000}");
                }

                // Zoom IN (closer): '=' (often '+') or Keypad '+'
                if (Input.GetKeyDown(KeyCode.Equals) || Input.GetKeyDown(KeyCode.KeypadPlus)) {
                    _zoomLevel += ZoomStep; // raising zoomLevel -> smaller FOV -> zoom in
                    _zoomLevel = Mathf.Clamp(_zoomLevel, MinZoomLevel, MaxZoomLevel);
                    changed = true;
                    _log?.LogInfo($"[ZoomMod] Keyboard zoom IN (+=) pressed, zoomLevel={_zoomLevel:0.000}");
                }

                // Reset zoom to default with '0'
                if (Input.GetKeyDown(KeyCode.Alpha0)) {
                    _zoomLevel = 1.0f;
                    changed = true;
                    _log?.LogInfo("[ZoomMod] Reset zoomLevel to 1.0 via key 0.");
                }
            } catch (Exception ex) {
                // In case the game uses the new Input System and old Input is disabled
                _inputBroken = true;
                _log?.LogError("[ZoomMod] Input.GetKeyDown threw exception; disabling keyboard zoom. Exception: " + ex);
            }

            if (!changed) {
                // no-op: don't spam logs
            }
        }

        private static void ApplyZoom(Camera cam) {
            if (cam == null)
                return;

            if (Time.unscaledTime - _lastApplyLogTime > 1.5f) {
                _lastApplyLogTime = Time.unscaledTime;
                _log?.LogInfo($"[ZoomMod] ApplyZoom on '{cam.name}': zoomLevel={_zoomLevel:0.000}, " +
                              $"currFOV={cam.fieldOfView:0.00}, currOrtho={cam.orthographicSize:0.000}, " +
                              $"baseFOV={_baseFov:0.00}, baseOrtho={_baseOrthoSize:0.000}");
            }

            if (cam.orthographic) {
                if (_baseOrthoSize < 0f) {
                    _baseOrthoSize = cam.orthographicSize;
                    _log?.LogInfo($"[ZoomMod] Captured base ORTHO size from game: {_baseOrthoSize:0.000}");
                }

                // zoomLevel > 1 -> smaller size -> zoom in
                float target = _baseOrthoSize / _zoomLevel;
                cam.orthographicSize = target;

                _log?.LogInfo($"[ZoomMod] -> ORTHO cam '{cam.name}' size set to {target:0.000} (base={_baseOrthoSize:0.000}).");
            } else {
                if (_baseFov < 0f) {
                    _baseFov = cam.fieldOfView;
                    _log?.LogInfo($"[ZoomMod] Captured base FOV from game: {_baseFov:0.00}");
                }

                // zoomLevel > 1 -> smaller FOV -> zoom in
                float target = _baseFov / _zoomLevel;
                target = Mathf.Clamp(target, 10f, 140f);
                cam.fieldOfView = target;

                _log?.LogInfo($"[ZoomMod] -> PERSPECTIVE cam '{cam.name}' FOV set to {target:0.00} (base={_baseFov:0.00}).");
            }
        }
    }
}
