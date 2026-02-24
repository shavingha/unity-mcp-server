#if !NO_MCP

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.UIElements;

namespace Nurture.MCP.Editor.Services
{
    internal class ScreenshotCapturer : MonoBehaviour
    {
        public Texture2D CapturedTexture { get; private set; }
        public bool IsDone { get; private set; }

        public IEnumerator CaptureEndOfFrame()
        {
            yield return new WaitForEndOfFrame();

            int width = Screen.width;
            int height = Screen.height;

            CapturedTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            CapturedTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            CapturedTexture.Apply();

            IsDone = true;
        }
    }

    [McpServerToolType]
    public static class ViewService
    {
        [McpServerTool(
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
            ReadOnly = false,
            Title = "Unity Focus on Game Object",
            Name = "focus_game_object"
        )]
        [Description("Focus on a game object in the scene view.")]
        internal static Task<string> FocusOnGameObject(
            SynchronizationContext context,
            CancellationToken cancellationToken,
            [Description("The path to the game object to focus on.")]
                string gameObjectHierarchyPath,
            [Description("Whether to hide all other game objects in the scene.")]
                bool isolated = false
        )
        {
            return context.Run(
                async () =>
                {
                    // Get the last active scene view
                    var sceneView =
                        SceneView.lastActiveSceneView
                        ?? throw new McpException("No active scene view found");

                    sceneView.Focus();

                    var gameObject =
                        GameObject.Find(gameObjectHierarchyPath)
                        ?? throw new McpException("Game object not found");

                    if (Selection.activeGameObject != gameObject)
                    {
                        Selection.activeGameObject = gameObject;
                    }

                    if (isolated)
                    {
                        SceneVisibilityManager.instance.Isolate(gameObject, true);
                    }

                    // Wait for the selection to be active
                    await Task.Delay(500);

                    // FIXME: Doing this twice focuses inside the object
                    sceneView.FrameSelected(false, true);

                    // Wait for focus to animate
                    await Task.Delay(500);

                    sceneView.Focus();

                    return $"Focused on {gameObjectHierarchyPath}";
                },
                cancellationToken
            );
        }

        [McpServerTool(
            Destructive = false,
            Idempotent = true,
            OpenWorld = false,
            ReadOnly = true,
            Title = "Unity Take Screenshot",
            Name = "screenshot"
        )]
        [Description(@"Retrieve a screenshot. In Play mode, captures the Game View (including UI). Otherwise, captures the Scene View.")]
        internal static async Task<ImageContentBlock> TakeScreenshot(
            SynchronizationContext context,
            CancellationToken cancellationToken,
            [Description(
                "The path to the camera to render. If null, it will use the Game View (in Play mode) or Scene View camera."
            )]
                string cameraHierarchyPath = ""
        )
        {
            return await context.Run(
                async () =>
                {
                    string screenshotBase64 = null;
                    Camera camera = null;

                    if (cameraHierarchyPath?.Length > 0)
                    {
                        camera = GameObject.Find(cameraHierarchyPath)?.GetComponent<Camera>();
                    }

                    if (camera != null)
                    {
                        var texture = new Texture2D(
                            (int)camera.pixelRect.width,
                            (int)camera.pixelRect.height,
                            TextureFormat.RGB24,
                            false
                        );

                        try
                        {
                            RenderTexture renderTexture = RenderTexture.GetTemporary(
                                texture.width,
                                texture.height,
                                24
                            );

                            RenderTexture previousRenderTexture = camera.targetTexture;
                            camera.targetTexture = renderTexture;

                            try
                            {
                                camera.Render();

                                RenderTexture previousActiveTexture = RenderTexture.active;
                                RenderTexture.active = renderTexture;

                                try
                                {
                                    texture.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                                    texture.Apply();

                                    screenshotBase64 = texture.GetPngBase64();
                                }
                                finally
                                {
                                    RenderTexture.active = previousActiveTexture;
                                }
                            }
                            finally
                            {
                                camera.targetTexture = previousRenderTexture;
                                RenderTexture.ReleaseTemporary(renderTexture);
                            }
                        }
                        finally
                        {
                            UnityEngine.Object.DestroyImmediate(texture);
                        }
                    }
                    else if (EditorApplication.isPlaying)
                    {
                        // In Play mode, use coroutine to capture at end of frame (includes UI)
                        var captureGO = new GameObject("_ScreenshotCapturer");
                        captureGO.hideFlags = HideFlags.HideAndDontSave;
                        var capturer = captureGO.AddComponent<ScreenshotCapturer>();

                        try
                        {
                            Coroutine captureCoroutine = capturer.StartCoroutine(capturer.CaptureEndOfFrame());

                            // Wait for capture to complete
                            int waitCount = 0;
                            while (!capturer.IsDone && waitCount < 100)
                            {
                                await Task.Delay(50);
                                waitCount++;
                            }

                            if (!capturer.IsDone || capturer.CapturedTexture == null)
                            {
                                capturer.StopCoroutine(captureCoroutine);
                                throw new McpException("Failed to capture screenshot in Play mode");
                            }

                            screenshotBase64 = capturer.CapturedTexture.GetPngBase64();
                        }
                        finally
                        {
                            if (capturer.CapturedTexture != null)
                            {
                                UnityEngine.Object.DestroyImmediate(capturer.CapturedTexture);
                            }
                            UnityEngine.Object.DestroyImmediate(captureGO);
                        }
                    }
                    else
                    {
                        // Not in Play mode, capture Scene View
                        var sceneView =
                            SceneView.lastActiveSceneView
                            ?? throw new McpException("No active scene view found");

                        var sceneCamera = sceneView.camera;
                        if (sceneCamera == null)
                        {
                            throw new McpException("Scene view camera not available");
                        }

                        int width = Mathf.RoundToInt(sceneView.position.width);
                        int height = Mathf.RoundToInt(sceneView.position.height);

                        if (width <= 0 || height <= 0)
                        {
                            throw new McpException(
                                $"Invalid Scene View dimensions: {width}x{height}"
                            );
                        }

                        var texture = new Texture2D(width, height, TextureFormat.RGB24, false);

                        try
                        {
                            RenderTexture renderTexture = RenderTexture.GetTemporary(
                                width,
                                height,
                                24
                            );

                            RenderTexture previousRenderTexture = sceneCamera.targetTexture;
                            sceneCamera.targetTexture = renderTexture;

                            try
                            {
                                sceneCamera.Render();

                                RenderTexture previousActiveTexture = RenderTexture.active;
                                RenderTexture.active = renderTexture;

                                try
                                {
                                    texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                                    texture.Apply();

                                    screenshotBase64 = texture.GetPngBase64();
                                }
                                finally
                                {
                                    RenderTexture.active = previousActiveTexture;
                                }
                            }
                            finally
                            {
                                sceneCamera.targetTexture = previousRenderTexture;
                                RenderTexture.ReleaseTemporary(renderTexture);
                            }
                        }
                        finally
                        {
                            UnityEngine.Object.DestroyImmediate(texture);
                        }
                    }

                    return new ImageContentBlock()
                    {
                        Data = screenshotBase64,
                        MimeType = "image/png",
                    };
                },
                cancellationToken
            );
        }

        [McpServerTool(
            Destructive = false,
            Idempotent = false,
            OpenWorld = false,
            ReadOnly = false,
            Title = "Unity Interact UI",
            Name = "interact_ui"
        )]
        [Description(@"Interact with a UI element in Play mode. Supports clicking buttons, inputting text, toggling, and selecting dropdown options.")]
        internal static Task<string> InteractUI(
            SynchronizationContext context,
            CancellationToken cancellationToken,
            [Description("The hierarchy path to the UI element (e.g., 'Canvas/Panel/Button').")]
                string uiElementPath,
            [Description("The type of interaction: 'click', 'input', 'toggle', or 'select'.")]
                string action,
            [Description("The value for the interaction. Required for 'input' (text to enter) and 'select' (option index or text). Optional for 'toggle' (true/false, defaults to toggle current state).")]
                string value = ""
        )
        {
            return context.Run(
                () =>
                {
                    if (!EditorApplication.isPlaying)
                    {
                        throw new McpException("interact_ui requires Play mode. Start the game first.");
                    }

                    var gameObject = GameObject.Find(uiElementPath);
                    if (gameObject == null)
                    {
                        throw new McpException($"UI element not found: {uiElementPath}");
                    }

                    var eventSystem = EventSystem.current;
                    if (eventSystem == null)
                    {
                        throw new McpException("No EventSystem found in the scene.");
                    }

                    string result;
                    if (string.IsNullOrEmpty(action))
                    {
                        throw new McpException("Action is required. Supported actions: click, input, toggle, select.");
                    }
                    switch (action.ToLower())
                    {
                        case "click":
                            result = PerformClick(gameObject);
                            break;
                        case "input":
                            result = PerformInput(gameObject, value);
                            break;
                        case "toggle":
                            result = PerformToggle(gameObject, value);
                            break;
                        case "select":
                            result = PerformSelect(gameObject, value);
                            break;
                        default:
                            throw new McpException($"Unknown action: {action}. Supported actions: click, input, toggle, select.");
                    }

                    return Task.FromResult(result);
                },
                cancellationToken
            );
        }

        private static string PerformClick(GameObject gameObject)
        {
            var button = gameObject.GetComponent<UnityEngine.UI.Button>();
            if (button != null)
            {
                if (!button.interactable)
                {
                    throw new McpException($"Button '{gameObject.name}' is not interactable.");
                }

                var pointer = new PointerEventData(EventSystem.current);
                ExecuteEvents.Execute(button.gameObject, pointer, ExecuteEvents.pointerClickHandler);
                return $"Clicked button: {gameObject.name}";
            }

            var selectable = gameObject.GetComponent<Selectable>();
            if (selectable != null)
            {
                var pointer = new PointerEventData(EventSystem.current);
                ExecuteEvents.Execute(gameObject, pointer, ExecuteEvents.pointerClickHandler);
                return $"Clicked UI element: {gameObject.name}";
            }

            throw new McpException($"No clickable component found on: {gameObject.name}");
        }

        private static string PerformInput(GameObject gameObject, string value)
        {
            var inputField = gameObject.GetComponent<InputField>();
            if (inputField != null)
            {
                if (!inputField.interactable)
                {
                    throw new McpException($"InputField '{gameObject.name}' is not interactable.");
                }

                inputField.text = value;
                inputField.onValueChanged?.Invoke(value);
                inputField.onEndEdit?.Invoke(value);
                return $"Set InputField '{gameObject.name}' text to: {value}";
            }

            // Try TMP_InputField via reflection to avoid assembly dependency
            var tmpInputFieldType = gameObject.GetComponent("TMP_InputField");
            if (tmpInputFieldType != null)
            {
                var type = tmpInputFieldType.GetType();
                var interactableProp = type.GetProperty("interactable");
                if (interactableProp != null && !(bool)interactableProp.GetValue(tmpInputFieldType))
                {
                    throw new McpException($"TMP_InputField '{gameObject.name}' is not interactable.");
                }

                var textProp = type.GetProperty("text");
                textProp?.SetValue(tmpInputFieldType, value);

                var onValueChangedField = type.GetField("onValueChanged");
                var onValueChanged = onValueChangedField?.GetValue(tmpInputFieldType);
                onValueChanged?.GetType().GetMethod("Invoke", new[] { typeof(string) })?.Invoke(onValueChanged, new object[] { value });

                var onEndEditField = type.GetField("onEndEdit");
                var onEndEdit = onEndEditField?.GetValue(tmpInputFieldType);
                onEndEdit?.GetType().GetMethod("Invoke", new[] { typeof(string) })?.Invoke(onEndEdit, new object[] { value });

                return $"Set TMP_InputField '{gameObject.name}' text to: {value}";
            }

            throw new McpException($"No InputField component found on: {gameObject.name}");
        }

        private static string PerformToggle(GameObject gameObject, string value)
        {
            var toggle = gameObject.GetComponent<UnityEngine.UI.Toggle>();
            if (toggle == null)
            {
                throw new McpException($"No Toggle component found on: {gameObject.name}");
            }

            if (!toggle.interactable)
            {
                throw new McpException($"Toggle '{gameObject.name}' is not interactable.");
            }

            bool newValue;
            if (string.IsNullOrEmpty(value))
            {
                newValue = !toggle.isOn;
            }
            else if (!bool.TryParse(value, out newValue))
            {
                throw new McpException($"Invalid toggle value: {value}. Use 'true' or 'false'.");
            }

            toggle.isOn = newValue;
            return $"Set Toggle '{gameObject.name}' to: {newValue}";
        }

        private static string PerformSelect(GameObject gameObject, string value)
        {
            var dropdown = gameObject.GetComponent<Dropdown>();
            if (dropdown != null)
            {
                if (!dropdown.interactable)
                {
                    throw new McpException($"Dropdown '{gameObject.name}' is not interactable.");
                }

                if (int.TryParse(value, out int index))
                {
                    if (index < 0 || index >= dropdown.options.Count)
                    {
                        throw new McpException($"Dropdown index {index} out of range. Valid range: 0-{dropdown.options.Count - 1}");
                    }
                    dropdown.value = index;
                    dropdown.onValueChanged?.Invoke(index);
                    return $"Selected Dropdown '{gameObject.name}' option at index {index}: {dropdown.options[index].text}";
                }
                else
                {
                    var optionIndex = dropdown.options.FindIndex(o => o.text == value);
                    if (optionIndex < 0)
                    {
                        throw new McpException($"Dropdown option '{value}' not found. Available options: {string.Join(", ", dropdown.options.Select(o => o.text))}");
                    }
                    dropdown.value = optionIndex;
                    dropdown.onValueChanged?.Invoke(optionIndex);
                    return $"Selected Dropdown '{gameObject.name}' option: {value}";
                }
            }

            // Try TMP_Dropdown via reflection to avoid assembly dependency
            var tmpDropdownComponent = gameObject.GetComponent("TMP_Dropdown");
            if (tmpDropdownComponent != null)
            {
                var type = tmpDropdownComponent.GetType();
                var interactableProp = type.GetProperty("interactable");
                if (interactableProp != null && !(bool)interactableProp.GetValue(tmpDropdownComponent))
                {
                    throw new McpException($"TMP_Dropdown '{gameObject.name}' is not interactable.");
                }

                var optionsProp = type.GetProperty("options");
                var options = optionsProp?.GetValue(tmpDropdownComponent) as System.Collections.IList;
                if (options == null)
                {
                    throw new McpException($"Could not get options from TMP_Dropdown '{gameObject.name}'.");
                }

                var valueProp = type.GetProperty("value");
                var onValueChangedField = type.GetField("onValueChanged");
                var onValueChanged = onValueChangedField?.GetValue(tmpDropdownComponent);

                if (int.TryParse(value, out int index))
                {
                    if (index < 0 || index >= options.Count)
                    {
                        throw new McpException($"TMP_Dropdown index {index} out of range. Valid range: 0-{options.Count - 1}");
                    }
                    valueProp?.SetValue(tmpDropdownComponent, index);
                    onValueChanged?.GetType().GetMethod("Invoke", new[] { typeof(int) })?.Invoke(onValueChanged, new object[] { index });

                    var optionTextProp = options[index]?.GetType().GetProperty("text");
                    var optionText = optionTextProp?.GetValue(options[index]) as string ?? "";
                    return $"Selected TMP_Dropdown '{gameObject.name}' option at index {index}: {optionText}";
                }
                else
                {
                    int optionIndex = -1;
                    for (int i = 0; i < options.Count; i++)
                    {
                        var optionTextProp = options[i]?.GetType().GetProperty("text");
                        var optionText = optionTextProp?.GetValue(options[i]) as string;
                        if (optionText == value)
                        {
                            optionIndex = i;
                            break;
                        }
                    }

                    if (optionIndex < 0)
                    {
                        var optionTexts = new List<string>();
                        for (int i = 0; i < options.Count; i++)
                        {
                            var optionTextProp = options[i]?.GetType().GetProperty("text");
                            optionTexts.Add(optionTextProp?.GetValue(options[i]) as string ?? "");
                        }
                        throw new McpException($"TMP_Dropdown option '{value}' not found. Available options: {string.Join(", ", optionTexts)}");
                    }
                    valueProp?.SetValue(tmpDropdownComponent, optionIndex);
                    onValueChanged?.GetType().GetMethod("Invoke", new[] { typeof(int) })?.Invoke(onValueChanged, new object[] { optionIndex });
                    return $"Selected TMP_Dropdown '{gameObject.name}' option: {value}";
                }
            }

            throw new McpException($"No Dropdown component found on: {gameObject.name}");
        }

        [McpServerTool(
            Destructive = false,
            Idempotent = false,
            OpenWorld = false,
            ReadOnly = false,
            Title = "Unity Interact UI Toolkit",
            Name = "interact_ui_toolkit"
        )]
        [Description(@"Interact with a UI Toolkit element in Play mode. Supports clicking buttons, inputting text, toggling, and selecting dropdown options. Use this for UI built with UI Toolkit (UIDocument).")]
        internal static Task<string> InteractUIToolkit(
            SynchronizationContext context,
            CancellationToken cancellationToken,
            [Description("The name or path to the UI element. Use '#name' for name query, '.class' for class query, or 'Type' for type query. Examples: '#login-button', '.submit-btn', 'Button'.")]
                string elementQuery,
            [Description("The type of interaction: 'click', 'input', 'toggle', or 'select'.")]
                string action,
            [Description("The value for the interaction. Required for 'input' (text to enter) and 'select' (option index or text). Optional for 'toggle' (true/false, defaults to toggle current state).")]
                string value = "",
            [Description("Optional: The name of the GameObject with UIDocument component. If not specified, uses the first UIDocument found.")]
                string uiDocumentName = ""
        )
        {
            return context.Run(
                () =>
                {
                    if (!EditorApplication.isPlaying)
                    {
                        throw new McpException("interact_ui_toolkit requires Play mode. Start the game first.");
                    }

                    UIDocument uiDocument = null;
                    if (!string.IsNullOrEmpty(uiDocumentName))
                    {
                        var go = GameObject.Find(uiDocumentName);
                        if (go != null)
                        {
                            uiDocument = go.GetComponent<UIDocument>();
                        }
                        if (uiDocument == null)
                        {
                            throw new McpException($"UIDocument not found on GameObject: {uiDocumentName}");
                        }
                    }
                    else
                    {
                        uiDocument = UnityEngine.Object.FindObjectOfType<UIDocument>();
                        if (uiDocument == null)
                        {
                            throw new McpException("No UIDocument found in the scene.");
                        }
                    }

                    var root = uiDocument.rootVisualElement;
                    if (root == null)
                    {
                        throw new McpException("UIDocument has no root visual element.");
                    }

                    var element = QueryElement(root, elementQuery);
                    if (element == null)
                    {
                        throw new McpException($"UI Toolkit element not found: {elementQuery}");
                    }

                    string result;
                    if (string.IsNullOrEmpty(action))
                    {
                        throw new McpException("Action is required. Supported actions: click, input, toggle, select.");
                    }
                    switch (action.ToLower())
                    {
                        case "click":
                            result = PerformToolkitClick(element, elementQuery);
                            break;
                        case "input":
                            result = PerformToolkitInput(element, elementQuery, value);
                            break;
                        case "toggle":
                            result = PerformToolkitToggle(element, elementQuery, value);
                            break;
                        case "select":
                            result = PerformToolkitSelect(element, elementQuery, value);
                            break;
                        default:
                            throw new McpException($"Unknown action: {action}. Supported actions: click, input, toggle, select.");
                    }

                    return Task.FromResult(result);
                },
                cancellationToken
            );
        }

        private static VisualElement QueryElement(VisualElement root, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return null;
            }

            // 支持 panel/element 形式的层级查询，例如：
            // "register-panel/#username" 或 "register-panel/username"
            // 先在 root 下找到 panel 元素，然后在该 panel 内继续查询子元素
            var slashIndex = query.IndexOf('/');
            if (slashIndex > 0 && slashIndex < query.Length - 1)
            {
                var panelName = query.Substring(0, slashIndex);
                var childQuery = query.Substring(slashIndex + 1);

                var panelRoot = root.Q(panelName);
                if (panelRoot == null)
                {
                    Debug.LogWarning($"[MCP][interact_ui_toolkit] Panel root not found for query '{query}', panelName='{panelName}'");
                    return null;
                }

                return QueryElement(panelRoot, childQuery);
            }

            if (query.StartsWith("#"))
            {
                return root.Q(query.Substring(1));
            }
            else if (query.StartsWith("."))
            {
                return root.Q(className: query.Substring(1));
            }
            else
            {
                var byName = root.Q(query);
                if (byName != null) return byName;

                return query switch
                {
                    "Button" => root.Q<UnityEngine.UIElements.Button>(),
                    "TextField" => root.Q<TextField>(),
                    "Toggle" => root.Q<UnityEngine.UIElements.Toggle>(),
                    "DropdownField" => root.Q<DropdownField>(),
                    "Slider" => root.Q<UnityEngine.UIElements.Slider>(),
                    "SliderInt" => root.Q<SliderInt>(),
                    _ => root.Q(query)
                };
            }
        }

        private static string PerformToolkitClick(VisualElement element, string query)
        {
            if (element is UnityEngine.UIElements.Button button)
            {
                if (!button.enabledSelf)
                {
                    throw new McpException($"Button '{query}' is not enabled.");
                }

                using (var clickEvent = ClickEvent.GetPooled())
                {
                    clickEvent.target = button;
                    button.SendEvent(clickEvent);
                }
                return $"Clicked UI Toolkit button: {query}";
            }

            if (!element.enabledSelf)
            {
                throw new McpException($"Element '{query}' is not enabled.");
            }

            using (var clickEvent = ClickEvent.GetPooled())
            {
                clickEvent.target = element;
                element.SendEvent(clickEvent);
            }
            return $"Clicked UI Toolkit element: {query}";
        }

        private static string PerformToolkitInput(VisualElement element, string query, string value)
        {
            if (element is TextField textField)
            {
                if (!textField.enabledSelf)
                {
                    throw new McpException($"TextField '{query}' is not enabled.");
                }

                textField.value = value;
                return $"Set TextField '{query}' value to: {value}";
            }

            if (element is BaseField<string> stringField)
            {
                if (!stringField.enabledSelf)
                {
                    throw new McpException($"Field '{query}' is not enabled.");
                }

                stringField.value = value;
                return $"Set field '{query}' value to: {value}";
            }

            throw new McpException($"Element '{query}' is not a text input field.");
        }

        private static string PerformToolkitToggle(VisualElement element, string query, string value)
        {
            if (element is UnityEngine.UIElements.Toggle toggle)
            {
                if (!toggle.enabledSelf)
                {
                    throw new McpException($"Toggle '{query}' is not enabled.");
                }

                bool newValue;
                if (string.IsNullOrEmpty(value))
                {
                    newValue = !toggle.value;
                }
                else if (!bool.TryParse(value, out newValue))
                {
                    throw new McpException($"Invalid toggle value: {value}. Use 'true' or 'false'.");
                }

                toggle.value = newValue;
                return $"Set Toggle '{query}' to: {newValue}";
            }

            throw new McpException($"Element '{query}' is not a Toggle.");
        }

        private static string PerformToolkitSelect(VisualElement element, string query, string value)
        {
            if (element is DropdownField dropdown)
            {
                if (!dropdown.enabledSelf)
                {
                    throw new McpException($"DropdownField '{query}' is not enabled.");
                }

                if (int.TryParse(value, out int index))
                {
                    if (index < 0 || index >= dropdown.choices.Count)
                    {
                        throw new McpException($"DropdownField index {index} out of range. Valid range: 0-{dropdown.choices.Count - 1}");
                    }
                    dropdown.index = index;
                    return $"Selected DropdownField '{query}' option at index {index}: {dropdown.choices[index]}";
                }
                else
                {
                    var optionIndex = dropdown.choices.IndexOf(value);
                    if (optionIndex < 0)
                    {
                        throw new McpException($"DropdownField option '{value}' not found. Available options: {string.Join(", ", dropdown.choices)}");
                    }
                    dropdown.index = optionIndex;
                    return $"Selected DropdownField '{query}' option: {value}";
                }
            }

            if (element is PopupField<string> popupField)
            {
                if (!popupField.enabledSelf)
                {
                    throw new McpException($"PopupField '{query}' is not enabled.");
                }

                if (int.TryParse(value, out int index))
                {
                    if (index < 0 || index >= popupField.choices.Count)
                    {
                        throw new McpException($"PopupField index {index} out of range. Valid range: 0-{popupField.choices.Count - 1}");
                    }
                    popupField.index = index;
                    return $"Selected PopupField '{query}' option at index {index}: {popupField.choices[index]}";
                }
                else
                {
                    var optionIndex = popupField.choices.IndexOf(value);
                    if (optionIndex < 0)
                    {
                        throw new McpException($"PopupField option '{value}' not found. Available options: {string.Join(", ", popupField.choices)}");
                    }
                    popupField.index = optionIndex;
                    return $"Selected PopupField '{query}' option: {value}";
                }
            }

            throw new McpException($"Element '{query}' is not a DropdownField or PopupField.");
        }
    }
}

#endif
