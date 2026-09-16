using System;
using System.Diagnostics;
using System.IO;
using OSFR.Measurement;
using UnityEngine;

namespace OSFR.Editor
{
    /// <summary>
    /// Renders a full camera image as projection-correct tiles at a higher spatial
    /// resolution, then box-averages each tile in linear HDR before stitching it.
    /// </summary>
    public static class TiledLinearHdrReferenceRenderer
    {
        private const string DownsampleShaderName = "Hidden/OSFR/LinearHdrBoxDownsample";
        private static readonly int SupersampleFactorId = Shader.PropertyToID("_OSFRSupersampleFactor");

        public sealed class Result
        {
            public Result(
                Color[] pixels,
                int factor,
                int tileCount,
                int peakRenderWidth,
                int peakRenderHeight,
                float elapsedMilliseconds)
            {
                Pixels = pixels;
                Factor = factor;
                TileCount = tileCount;
                PeakRenderWidth = peakRenderWidth;
                PeakRenderHeight = peakRenderHeight;
                ElapsedMilliseconds = elapsedMilliseconds;
            }

            public Color[] Pixels { get; }

            public int Factor { get; }

            public int TileCount { get; }

            public int PeakRenderWidth { get; }

            public int PeakRenderHeight { get; }

            public float ElapsedMilliseconds { get; }
        }

        public static Result Capture(
            Camera camera,
            int targetWidth,
            int targetHeight,
            int supersampleFactor,
            int targetTileEdge,
            string outputPath)
        {
            if (camera == null)
            {
                throw new ArgumentNullException(nameof(camera));
            }

            if (targetWidth <= 0 || targetHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetWidth), "Target dimensions must be positive.");
            }

            if (supersampleFactor < 1 || supersampleFactor > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(supersampleFactor), "Supersampling must be between 1x and 16x per axis.");
            }

            if (targetTileEdge <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(targetTileEdge));
            }

            Shader shader = Shader.Find(DownsampleShaderName);
            if (shader == null)
            {
                throw new InvalidOperationException($"Required shader was not found: {DownsampleShaderName}");
            }

            int boundedTileEdge = Mathf.Min(targetTileEdge, SystemInfo.maxTextureSize / supersampleFactor);
            if (boundedTileEdge < 1)
            {
                throw new InvalidOperationException(
                    $"The {supersampleFactor}x reference exceeds the device texture limit of {SystemInfo.maxTextureSize}.");
            }

            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            var stitched = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBAFloat, false, true)
            {
                name = $"OSFR {supersampleFactor}x Linear HDR Reference",
                hideFlags = HideFlags.HideAndDontSave
            };

            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            Matrix4x4 previousProjection = camera.projectionMatrix;
            float previousAspect = camera.aspect;
            bool previousAllowHdr = camera.allowHDR;
            bool previousAllowMsaa = camera.allowMSAA;
            var stopwatch = Stopwatch.StartNew();
            int tileCount = 0;
            int peakRenderWidth = 0;
            int peakRenderHeight = 0;

            try
            {
                camera.targetTexture = null;
                camera.allowHDR = true;
                camera.allowMSAA = false;
                camera.aspect = targetWidth / (float)targetHeight;
                camera.ResetProjectionMatrix();
                Matrix4x4 fullProjection = camera.projectionMatrix;
                material.SetInt(SupersampleFactorId, supersampleFactor);

                for (int targetY = 0; targetY < targetHeight; targetY += boundedTileEdge)
                {
                    int tileHeight = Mathf.Min(boundedTileEdge, targetHeight - targetY);
                    for (int targetX = 0; targetX < targetWidth; targetX += boundedTileEdge)
                    {
                        int tileWidth = Mathf.Min(boundedTileEdge, targetWidth - targetX);
                        var tile = new RectInt(targetX, targetY, tileWidth, tileHeight);
                        int renderWidth = tileWidth * supersampleFactor;
                        int renderHeight = tileHeight * supersampleFactor;
                        peakRenderWidth = Mathf.Max(peakRenderWidth, renderWidth);
                        peakRenderHeight = Mathf.Max(peakRenderHeight, renderHeight);

                        RenderTile(
                            camera,
                            material,
                            stitched,
                            fullProjection,
                            tile,
                            targetWidth,
                            targetHeight,
                            renderWidth,
                            renderHeight);
                        tileCount++;
                    }
                }

                stitched.Apply(false, false);
                Color[] pixels = stitched.GetPixels();
                if (!string.IsNullOrEmpty(outputPath))
                {
                    byte[] exr = stitched.EncodeToEXR(
                        Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP);
                    string outputDirectory = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrEmpty(outputDirectory))
                    {
                        Directory.CreateDirectory(outputDirectory);
                    }

                    File.WriteAllBytes(outputPath, exr);
                }
                stopwatch.Stop();
                return new Result(
                    pixels,
                    supersampleFactor,
                    tileCount,
                    peakRenderWidth,
                    peakRenderHeight,
                    (float)stopwatch.Elapsed.TotalMilliseconds);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                camera.allowHDR = previousAllowHdr;
                camera.allowMSAA = previousAllowMsaa;
                camera.aspect = previousAspect;
                camera.projectionMatrix = previousProjection;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(stitched);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        private static void RenderTile(
            Camera camera,
            Material material,
            Texture2D stitched,
            Matrix4x4 fullProjection,
            RectInt tile,
            int targetWidth,
            int targetHeight,
            int renderWidth,
            int renderHeight)
        {
            var supersampled = new RenderTexture(
                renderWidth,
                renderHeight,
                24,
                RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear)
            {
                name = "OSFR Supersampled Reference Tile",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };
            var resolved = new RenderTexture(
                tile.width,
                tile.height,
                0,
                RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear)
            {
                name = "OSFR Resolved Reference Tile",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false
            };

            try
            {
                camera.projectionMatrix = SupersampleReferenceMath.BuildTileProjection(
                    fullProjection,
                    tile,
                    targetWidth,
                    targetHeight);
                camera.targetTexture = supersampled;
                camera.Render();
                Graphics.Blit(supersampled, resolved, material, 0);
                RenderTexture.active = resolved;
                stitched.ReadPixels(
                    new Rect(0, 0, tile.width, tile.height),
                    tile.x,
                    tile.y,
                    false);
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = null;
                UnityEngine.Object.DestroyImmediate(resolved);
                UnityEngine.Object.DestroyImmediate(supersampled);
            }
        }
    }
}
