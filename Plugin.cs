using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace IGTAS
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        internal static new ManualLogSource Logger;

        private MonoBehaviour movementComp;
        private FieldInfo momentumField;
        private FieldInfo bodyField;
        private FieldInfo onGroundField;
        private FieldInfo moveActionField;
        private FieldInfo jumpActionField;
        private FieldInfo dashActionField;

        private GUIStyle topCenterStyle;
        private GUIStyle debugStyle;
        private GUIStyle fpsStyle;
        private GUIStyle sidebarStyle;
        private GUIStyle sidebarHighlightStyle;
        private GUIStyle sidebarDimStyle;
        private float deltaTime;

        // ===== TAS CORE =====
        private List<FrameInputSnapshot> recordedFrames = new();
        private int replayIndex = 0;
        private bool isRecording = false;
        private bool isReplaying = false;
        private string tasFolder;
        private string savedInputFile;

        private HashSet<Key> previouslyDown = new();
        private Keyboard virtualKeyboard;

        // ===== EDITOR =====
        private bool isEditing = false;
        private int editFrame = 0;

        private static readonly Dictionary<Key, string> keyLabels = new()
        {
            { Key.A,          "←  A"    },
            { Key.D,          "→  D"    },
            { Key.W,          "↑  W"    },
            { Key.S,          "↓  S"    },
            { Key.Space,      "⎵  Jump" },
            { Key.LeftShift,  "⇧  Dash" },
        };

        private static readonly HashSet<Key> ignoredKeys = new()
        {
            Key.F1,  Key.F2,  Key.F3,  Key.F4,
            Key.F5,  Key.F6,  Key.F7,  Key.F8,
            Key.F9,  Key.F10, Key.F11, Key.F12,
            Key.LeftArrow, Key.RightArrow, Key.UpArrow, Key.DownArrow,
        };

        private void Awake()
        {
            Logger = base.Logger;

            topCenterStyle = new GUIStyle { fontSize = 40, normal = { textColor = Color.red }, alignment = TextAnchor.UpperCenter };
            debugStyle = new GUIStyle { fontSize = 16, normal = { textColor = Color.white }, alignment = TextAnchor.UpperLeft };
            fpsStyle = new GUIStyle { fontSize = 18, normal = { textColor = Color.yellow }, alignment = TextAnchor.UpperCenter };

            sidebarStyle = new GUIStyle
            {
                fontSize = 14,
                normal = { textColor = new Color(0.8f, 0.8f, 0.8f) },
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(6, 6, 2, 2)
            };
            sidebarHighlightStyle = new GUIStyle(sidebarStyle)
            {
                fontSize = 15,
                normal = { textColor = Color.yellow },
                fontStyle = FontStyle.Bold
            };
            sidebarDimStyle = new GUIStyle(sidebarStyle)
            {
                normal = { textColor = new Color(0.45f, 0.45f, 0.45f) }
            };

            tasFolder = Path.Combine(Paths.ConfigPath, "TAS");
            if (!Directory.Exists(tasFolder))
                Directory.CreateDirectory(tasFolder);

            InputSystem.onDeviceChange += OnDeviceChange;
        }

        private void OnDeviceChange(InputDevice device, InputDeviceChange change)
        {
            if (device.name == "VirtualKeyboard") return;
            if (virtualKeyboard != null) return;
            if (change != InputDeviceChange.Added) return;

            try
            {
                virtualKeyboard = InputSystem.AddDevice<Keyboard>("VirtualKeyboard");
                Logger.LogInfo($"Virtual keyboard added, id={virtualKeyboard.deviceId}");
            }
            catch (Exception e)
            {
                Logger.LogError($"Failed to add virtual keyboard: {e}");
            }
        }

        private void OnDestroy()
        {
            InputSystem.onDeviceChange -= OnDeviceChange;
            if (virtualKeyboard != null)
                InputSystem.RemoveDevice(virtualKeyboard);
        }

        private void Update()
        {
            deltaTime += (Time.unscaledDeltaTime - deltaTime) * 0.1f;
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            HandleTASControls(keyboard);
            if (isEditing) HandleEditorControls(keyboard);
            if (movementComp == null) TryFindMovement();
        }

        private void FixedUpdate()
        {
            if (isRecording) CaptureFrame();
            if (isReplaying) PlayFrame();
        }

        // ==============================
        // RECORDING
        // ==============================
        private void CaptureFrame()
        {
            var keyboard = Keyboard.current;
            if (keyboard == null) return;

            var snapshot = new FrameInputSnapshot();

            foreach (Key key in Enum.GetValues(typeof(Key)))
            {
                if (key == Key.None) continue;
                if (ignoredKeys.Contains(key)) continue;

                var ctrl = keyboard[key];
                if (ctrl == null) continue;

                bool isDown = ctrl.isPressed;
                bool wasDown = previouslyDown.Contains(key);

                if (isDown || wasDown)
                {
                    snapshot.keyStates[key] = new KeySnapshot
                    {
                        isDown = isDown,
                        wentDown = ctrl.wasPressedThisFrame,
                        wentUp = ctrl.wasReleasedThisFrame
                    };
                }

                if (isDown) previouslyDown.Add(key);
                else previouslyDown.Remove(key);
            }

            recordedFrames.Add(snapshot);
        }

        // ==============================
        // PLAYBACK
        // ==============================
        private void PlayFrame()
        {
            if (virtualKeyboard == null) { StopPlayback(); return; }

            if (replayIndex >= recordedFrames.Count)
            {
                StopPlayback();
                return;
            }

            var snapshot = recordedFrames[replayIndex];
            var keyboardState = new KeyboardState();

            foreach (var kv in snapshot.keyStates)
                if (kv.Value.isDown)
                    keyboardState.Set(kv.Key, true);

            InputSystem.QueueStateEvent(virtualKeyboard, keyboardState, InputState.currentTime);
            replayIndex++;
        }

        private void StopPlayback()
        {
            isReplaying = false;
            replayIndex = 0;

            if (virtualKeyboard != null)
                InputSystem.QueueStateEvent(virtualKeyboard, new KeyboardState(), InputState.currentTime);

            ResetActionsToDefault();
            Logger.LogInfo("Playback complete.");
        }

        // ==============================
        // EDITOR
        // ==============================
        private void EnterEditor()
        {
            if (recordedFrames.Count == 0) { Logger.LogWarning("No frames to edit."); return; }

            isEditing = true;
            isReplaying = false;
            isRecording = false;
            editFrame = Mathf.Clamp(editFrame, 0, recordedFrames.Count - 1);

            Time.timeScale = 0f;
            Logger.LogInfo($"Editor opened at frame {editFrame}.");
        }

        private void ExitEditor()
        {
            isEditing = false;
            Time.timeScale = 1f;

            if (savedInputFile != null)
            {
                SaveRecording(savedInputFile);
                Logger.LogInfo("Editor closed, changes saved.");
            }
        }

        private void HandleEditorControls(Keyboard keyboard)
        {
            if (keyboard.leftArrowKey.wasPressedThisFrame)
                editFrame = Mathf.Max(0, editFrame - 1);

            if (keyboard.rightArrowKey.wasPressedThisFrame)
                editFrame = Mathf.Min(recordedFrames.Count - 1, editFrame + 1);

            foreach (Key key in Enum.GetValues(typeof(Key)))
            {
                if (key == Key.None) continue;
                if (ignoredKeys.Contains(key)) continue;

                var ctrl = keyboard[key];
                if (ctrl == null || !ctrl.wasPressedThisFrame) continue;

                var frame = recordedFrames[editFrame];

                if (frame.keyStates.TryGetValue(key, out var existing))
                {
                    if (existing.isDown)
                    {
                        frame.keyStates.Remove(key);
                        Logger.LogInfo($"Frame {editFrame}: removed {key}");
                    }
                    else
                    {
                        existing.isDown = true;
                        Logger.LogInfo($"Frame {editFrame}: set {key} down");
                    }
                }
                else
                {
                    frame.keyStates[key] = new KeySnapshot { isDown = true, wentDown = true, wentUp = false };
                    Logger.LogInfo($"Frame {editFrame}: added {key}");
                }
            }
        }

        // ==============================
        // REBINDING
        // ==============================
        private void RebindActionsToVirtualKeyboard()
        {
            if (virtualKeyboard == null) { Logger.LogWarning("Virtual keyboard not ready."); return; }
            if (movementComp == null || moveActionField == null) return;

            string vkPath = "/" + virtualKeyboard.name;
            Logger.LogInfo($"Rebinding to virtual keyboard: {vkPath}");

            RebindComposite(moveActionField, new Dictionary<string, string>
            {
                { "up",    $"{vkPath}/w" },
                { "down",  $"{vkPath}/s" },
                { "left",  $"{vkPath}/a" },
                { "right", $"{vkPath}/d" },
            });

            RebindSimple(jumpActionField, $"{vkPath}/space");
            RebindSimple(dashActionField, $"{vkPath}/leftShift");
        }

        private void ResetActionsToDefault()
        {
            if (movementComp == null || moveActionField == null) return;
            Logger.LogInfo("Resetting actions to default bindings.");
            ResetAction(moveActionField);
            ResetAction(jumpActionField);
            ResetAction(dashActionField);
        }

        private void ResetAction(FieldInfo field)
        {
            if (field == null) return;
            var action = field.GetValue(movementComp) as InputAction;
            if (action == null) return;
            bool wasEnabled = action.enabled;
            action.Disable();
            action.RemoveAllBindingOverrides();
            if (wasEnabled) action.Enable();
        }

        private void RebindComposite(FieldInfo field, Dictionary<string, string> partPaths)
        {
            if (field == null) return;
            var action = field.GetValue(movementComp) as InputAction;
            if (action == null) return;

            bool wasEnabled = action.enabled;
            action.Disable();

            for (int i = 0; i < action.bindings.Count; i++)
            {
                var binding = action.bindings[i];
                if (!binding.isPartOfComposite) continue;
                string partName = binding.name.ToLower();
                if (partPaths.TryGetValue(partName, out string newPath))
                    action.ApplyBindingOverride(i, new InputBinding { overridePath = newPath });
            }

            if (wasEnabled) action.Enable();
        }

        private void RebindSimple(FieldInfo field, string newPath)
        {
            if (field == null) return;
            var action = field.GetValue(movementComp) as InputAction;
            if (action == null) return;

            bool wasEnabled = action.enabled;
            action.Disable();

            for (int i = 0; i < action.bindings.Count; i++)
            {
                if (action.bindings[i].isComposite) continue;
                action.ApplyBindingOverride(i, new InputBinding { overridePath = newPath });
            }

            if (wasEnabled) action.Enable();
        }

        // ==============================
        // SAVE / LOAD
        // ==============================
        private void SaveRecording(string path)
        {
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);

            bw.Write(recordedFrames.Count);
            foreach (var frame in recordedFrames)
            {
                bw.Write(frame.keyStates.Count);
                foreach (var kv in frame.keyStates)
                {
                    bw.Write((int)kv.Key);
                    bw.Write(kv.Value.isDown);
                    bw.Write(kv.Value.wentDown);
                    bw.Write(kv.Value.wentUp);
                }
            }

            Logger.LogInfo($"Saved {recordedFrames.Count} frames.");
        }

        private void LoadRecording(string path)
        {
            if (!File.Exists(path)) return;
            recordedFrames.Clear();

            using var fs = new FileStream(path, FileMode.Open);
            using var br = new BinaryReader(fs);

            int frameCount = br.ReadInt32();
            for (int i = 0; i < frameCount; i++)
            {
                var frame = new FrameInputSnapshot();
                int keyCount = br.ReadInt32();
                for (int j = 0; j < keyCount; j++)
                {
                    var key = (Key)br.ReadInt32();
                    frame.keyStates[key] = new KeySnapshot
                    {
                        isDown = br.ReadBoolean(),
                        wentDown = br.ReadBoolean(),
                        wentUp = br.ReadBoolean()
                    };
                }
                recordedFrames.Add(frame);
            }

            Logger.LogInfo($"Loaded {recordedFrames.Count} frames.");
        }

        // ==============================
        // INPUT CONTROLS
        // ==============================
        private void HandleTASControls(Keyboard keyboard)
        {
            if (keyboard.f6Key.wasPressedThisFrame)
            {
                if (isEditing) ExitEditor();
                isRecording = true;
                isReplaying = false;
                recordedFrames.Clear();
                previouslyDown.Clear();
                savedInputFile = Path.Combine(tasFolder, $"tas_{DateTime.Now:yyyyMMdd_HHmmss}.bin");
                Logger.LogInfo("Recording started.");
            }

            if (keyboard.f7Key.wasPressedThisFrame)
            {
                if (isRecording)
                {
                    isRecording = false;
                    SaveRecording(savedInputFile);
                    Logger.LogInfo("Recording stopped.");
                }
                if (isReplaying) StopPlayback();
            }

            if (keyboard.f8Key.wasPressedThisFrame)
            {
                if (isEditing) ExitEditor();

                string[] files = Directory.GetFiles(tasFolder, "tas_*.bin");
                if (files.Length == 0) { Logger.LogWarning("No TAS files found."); return; }

                Array.Sort(files);
                savedInputFile = files[files.Length - 1];
                LoadRecording(savedInputFile);
                RebindActionsToVirtualKeyboard();

                isRecording = false;
                isReplaying = true;
                replayIndex = 0;

                Logger.LogInfo($"Playback started from {savedInputFile}");
            }

            if (keyboard.f9Key.wasPressedThisFrame)
            {
                if (isEditing) ExitEditor();
                else EnterEditor();
            }

            if (keyboard.f10Key.wasPressedThisFrame && isEditing)
            {
                int insertAt = Mathf.Clamp(editFrame + 1, 0, recordedFrames.Count);
                recordedFrames.Insert(insertAt, new FrameInputSnapshot());
                editFrame = insertAt;
                Logger.LogInfo($"Inserted blank frame at {insertAt}. Total: {recordedFrames.Count}");
            }

            if (keyboard.f11Key.wasPressedThisFrame && isEditing)
            {
                if (recordedFrames.Count > 1)
                {
                    recordedFrames.RemoveAt(editFrame);
                    editFrame = Mathf.Clamp(editFrame, 0, recordedFrames.Count - 1);
                    Logger.LogInfo($"Removed frame. Now at {editFrame}. Total: {recordedFrames.Count}");
                }
                else
                {
                    Logger.LogWarning("Can't remove last remaining frame.");
                }
            }
        }

        // ==============================
        // DEBUG / GUI
        // ==============================
        private void TryFindMovement()
        {
            GameObject player = GameObject.FindWithTag("Player");
            if (player == null) return;

            movementComp = player.GetComponent("Movement") as MonoBehaviour;
            if (movementComp == null) return;

            Type t = movementComp.GetType();
            momentumField = t.GetField("momentum", BindingFlags.NonPublic | BindingFlags.Instance);
            bodyField = t.GetField("body", BindingFlags.NonPublic | BindingFlags.Instance);
            onGroundField = t.GetField("onGround", BindingFlags.NonPublic | BindingFlags.Instance);
            moveActionField = t.GetField("moveAction", BindingFlags.NonPublic | BindingFlags.Instance);
            jumpActionField = t.GetField("jumpAction", BindingFlags.NonPublic | BindingFlags.Instance);
            dashActionField = t.GetField("dashAction", BindingFlags.NonPublic | BindingFlags.Instance);

            Logger.LogInfo("Movement component found.");
        }

        private string FrameToString(int index)
        {
            if (index < 0 || index >= recordedFrames.Count) return "";

            var frame = recordedFrames[index];
            var parts = new List<string>();

            foreach (var kv in frame.keyStates)
                if (kv.Value.isDown)
                    parts.Add(keyLabels.TryGetValue(kv.Key, out string label) ? label : kv.Key.ToString());

            return parts.Count > 0 ? string.Join("  ", parts) : "—";
        }

        private void OnGUI()
        {
            GUI.depth = -1000;

            float fps = 1.0f / deltaTime;
            GUI.Label(new Rect(0, 0, Screen.width, 25), $"FPS: {fps:F1}", fpsStyle);
            GUI.Label(new Rect(0, 25, Screen.width, 50), "MODDED", topCenterStyle);

            if (movementComp != null && bodyField != null)
            {
                Rigidbody2D body = (Rigidbody2D)bodyField.GetValue(movementComp);
                bool onGround = onGroundField != null && (bool)onGroundField.GetValue(movementComp);
                Vector2 momentum = (Vector2)momentumField.GetValue(movementComp);

                string debugText =
                    $"DEBUG:\n" +
                    $"Momentum X: {momentum.x:F2}\n" +
                    $"Momentum Y: {momentum.y:F2}\n\n" +
                    $"Velocity: {body.velocity}\n" +
                    $"Position: {body.position}\n" +
                    $"On Ground: {onGround}\n" +
                    $"Recording: {isRecording}\n" +
                    $"Replaying: {isReplaying}\n" +
                    $"Editing:   {isEditing}\n" +
                    $"Replay: {replayIndex}/{recordedFrames.Count}";

                GUI.Label(new Rect(10, 10, 400, 240), debugText, debugStyle);
            }

            // ---- Sidebar ----
            if ((isEditing || isReplaying) && recordedFrames.Count > 0)
            {
                int currentFrame = isEditing ? editFrame : replayIndex - 1;
                int visibleCount = 10;
                int rowHeight = 28;
                int sidebarWidth = 220;
                int totalHeight = visibleCount * rowHeight;
                int startY = (Screen.height - totalHeight) / 2;
                int startX = 10;

                GUI.color = new Color(0, 0, 0, 0.55f);
                GUI.DrawTexture(new Rect(startX - 4, startY - 4, sidebarWidth + 8, totalHeight + 8), Texture2D.whiteTexture);
                GUI.color = Color.white;

                int half = visibleCount / 2;
                int start = Mathf.Clamp(currentFrame - half, 0, Mathf.Max(0, recordedFrames.Count - visibleCount));

                for (int i = 0; i < visibleCount; i++)
                {
                    int frameIdx = start + i;
                    if (frameIdx >= recordedFrames.Count) break;

                    bool isCurrent = frameIdx == currentFrame;
                    string label = $"[{frameIdx:D4}]  {FrameToString(frameIdx)}";
                    var style = isCurrent ? sidebarHighlightStyle
                                   : (Mathf.Abs(frameIdx - currentFrame) > 3 ? sidebarDimStyle : sidebarStyle);

                    if (isCurrent)
                    {
                        GUI.color = new Color(1f, 1f, 0f, 0.15f);
                        GUI.DrawTexture(new Rect(startX - 4, startY + i * rowHeight, sidebarWidth + 8, rowHeight), Texture2D.whiteTexture);
                        GUI.color = Color.white;
                    }

                    GUI.Label(new Rect(startX, startY + i * rowHeight, sidebarWidth, rowHeight), label, style);
                }

                if (isEditing)
                {
                    GUI.color = new Color(1, 1, 0, 0.9f);
                    GUI.Label(
                        new Rect(startX, startY + totalHeight + 8, sidebarWidth, 80),
                        "◀ ▶  step frame\nAny key  toggle\nF10  insert frame\nF11  remove frame\nF9  save & exit",
                        sidebarStyle
                    );
                    GUI.color = Color.white;
                }
            }
        }
    }

    public class KeySnapshot
    {
        public bool isDown;
        public bool wentDown;
        public bool wentUp;
    }

    public class FrameInputSnapshot
    {
        public Dictionary<Key, KeySnapshot> keyStates = new();
    }
}