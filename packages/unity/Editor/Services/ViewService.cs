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

                        RenderTexture renderTexture = RenderTexture.GetTemporary(
                            texture.width,
                            texture.height,
                            24
                        );

                        RenderTexture previousRenderTexture = camera.targetTexture;
                        camera.targetTexture = renderTexture;

                        camera.Render();

                        RenderTexture previousActiveTexture = RenderTexture.active;
                        RenderTexture.active = renderTexture;

                        texture.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                        texture.Apply();

                        camera.targetTexture = previousRenderTexture;
                        RenderTexture.active = previousActiveTexture;

                        RenderTexture.ReleaseTemporary(renderTexture);

                        screenshotBase64 = texture.GetPngBase64();

                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                    else if (EditorApplication.isPlaying)
                    {
                        // In Play mode, use coroutine to capture at end of frame (includes UI)
                        var captureGO = new GameObject("_ScreenshotCapturer");
                        captureGO.hideFlags = HideFlags.HideAndDontSave;
                        var capturer = captureGO.AddComponent<ScreenshotCapturer>();

                        capturer.StartCoroutine(capturer.CaptureEndOfFrame());

                        // Wait for capture to complete
                        int waitCount = 0;
                        while (!capturer.IsDone && waitCount < 100)
                        {
                            await Task.Delay(50);
                            waitCount++;
                        }

                        if (!capturer.IsDone || capturer.CapturedTexture == null)
                        {
                            UnityEngine.Object.DestroyImmediate(captureGO);
                            throw new McpException("Failed to capture screenshot in Play mode");
                        }

                        screenshotBase64 = capturer.CapturedTexture.GetPngBase64();

                        UnityEngine.Object.DestroyImmediate(capturer.CapturedTexture);
                        UnityEngine.Object.DestroyImmediate(captureGO);
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

                        RenderTexture renderTexture = RenderTexture.GetTemporary(
                            width,
                            height,
                            24
                        );

                        RenderTexture previousRenderTexture = sceneCamera.targetTexture;
                        sceneCamera.targetTexture = renderTexture;

                        sceneCamera.Render();

                        RenderTexture previousActiveTexture = RenderTexture.active;
                        RenderTexture.active = renderTexture;

                        texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                        texture.Apply();

                        sceneCamera.targetTexture = previousRenderTexture;
                        RenderTexture.active = previousActiveTexture;

                        RenderTexture.ReleaseTemporary(renderTexture);

                        screenshotBase64 = texture.GetPngBase64();

                        UnityEngine.Object.DestroyImmediate(texture);
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
    }
}

#endif
