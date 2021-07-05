using UnityEngine.Experimental.Rendering;
using UnityEngine.Experimental.Rendering.RenderGraphModule;

namespace UnityEngine.Rendering.HighDefinition
{
    public partial class HDRenderPipeline
    {
        // Tile of the side used to dispatch the various compute shader kernels
        const int rtReflectionsComputeTileSize = 8;

        // String values
        const string m_RayGenIntegrationName = "RayGenIntegration";
        const string m_RayGenIntegrationTransparentName = "RayGenIntegrationTransparent";

        // Kernels
        int m_RaytracingReflectionsFullResKernel;
        int m_RaytracingReflectionsHalfResKernel;
        int m_RaytracingReflectionsTransparentFullResKernel;
        int m_RaytracingReflectionsTransparentHalfResKernel;
        int m_ReflectionAdjustWeightKernel;
        int m_ReflectionRescaleAndAdjustWeightKernel;
        int m_ReflectionUpscaleKernel;

        void InitRayTracedReflections()
        {
            ComputeShader reflectionShaderCS = m_GlobalSettings.renderPipelineRayTracingResources.reflectionRaytracingCS;
            ComputeShader reflectionBilateralFilterCS = m_GlobalSettings.renderPipelineRayTracingResources.reflectionBilateralFilterCS;

            // Grab all the kernels we shall be using
            m_RaytracingReflectionsFullResKernel = reflectionShaderCS.FindKernel("RaytracingReflectionsFullRes");
            m_RaytracingReflectionsHalfResKernel = reflectionShaderCS.FindKernel("RaytracingReflectionsHalfRes");
            m_RaytracingReflectionsTransparentFullResKernel = reflectionShaderCS.FindKernel("RaytracingReflectionsTransparentFullRes");
            m_RaytracingReflectionsTransparentHalfResKernel = reflectionShaderCS.FindKernel("RaytracingReflectionsTransparentHalfRes");
            m_ReflectionAdjustWeightKernel = reflectionBilateralFilterCS.FindKernel("ReflectionAdjustWeight");
            m_ReflectionRescaleAndAdjustWeightKernel = reflectionBilateralFilterCS.FindKernel("ReflectionRescaleAndAdjustWeight");
            m_ReflectionUpscaleKernel = reflectionBilateralFilterCS.FindKernel("ReflectionUpscale");
        }

        static RTHandle ReflectionHistoryBufferAllocatorFunction(string viewName, int frameIndex, RTHandleSystem rtHandleSystem)
        {
            return rtHandleSystem.Alloc(Vector2.one, TextureXR.slices, colorFormat: GraphicsFormat.R16G16B16A16_SFloat, dimension: TextureXR.dimension,
                enableRandomWrite: true, useMipMap: false, autoGenerateMips: false,
                name: string.Format("{0}_ReflectionHistoryBuffer{1}", viewName, frameIndex));
        }

        static RTHandle ReflectionHistorySampleCountBufferAllocatorFunction(string viewName, int frameIndex, RTHandleSystem rtHandleSystem)
        {
            return rtHandleSystem.Alloc(Vector2.one, TextureXR.slices, colorFormat: GraphicsFormat.R8_UNorm, dimension: TextureXR.dimension,
                enableRandomWrite: true, useMipMap: false, autoGenerateMips: false,
                name: string.Format("{0}_ReflectionHistorySampleCountBuffer{1}", viewName, frameIndex));
        }

        private float EvaluateRayTracedReflectionHistoryValidity(HDCamera hdCamera, bool fullResolution, bool rayTraced)
        {
            // Evaluate the history validity
            float effectHistoryValidity = hdCamera.EffectHistoryValidity(HDCamera.HistoryEffectSlot.RayTracedReflections, fullResolution, rayTraced) ? 1.0f : 0.0f;
            return EvaluateHistoryValidity(hdCamera) * effectHistoryValidity;
        }

        private void PropagateRayTracedReflectionsHistoryValidity(HDCamera hdCamera, bool fullResolution, bool rayTraced)
        {
            hdCamera.PropagateEffectHistoryValidity(HDCamera.HistoryEffectSlot.RayTracedReflections, fullResolution, rayTraced);
        }

        #region Direction Generation
        class DirGenRTRPassData
        {
            // Camera parameters
            public int texWidth;
            public int texHeight;
            public int viewCount;

            // Generation parameters
            public float minSmoothness;

            // Additional resources
            public ComputeShader directionGenCS;
            public int dirGenKernel;
            public BlueNoise.DitheredTextureSet ditheredTextureSet;
            public ShaderVariablesRaytracing shaderVariablesRayTracingCB;

            public TextureHandle depthBuffer;
            public TextureHandle stencilBuffer;
            public TextureHandle normalBuffer;
            public TextureHandle clearCoatMaskTexture;
            public TextureHandle outputBuffer;
        }

        TextureHandle DirGenRTR(RenderGraph renderGraph, HDCamera hdCamera, ScreenSpaceReflection settings, TextureHandle depthBuffer, TextureHandle stencilBuffer, TextureHandle normalBuffer, TextureHandle clearCoatTexture, bool transparent)
        {
            using (var builder = renderGraph.AddRenderPass<DirGenRTRPassData>("Generating the rays for RTR", out var passData, ProfilingSampler.Get(HDProfileId.RaytracingReflectionDirectionGeneration)))
            {
                builder.EnableAsyncCompute(false);

                // Set the camera parameters
                passData.texWidth = settings.fullResolution ? hdCamera.actualWidth : hdCamera.actualWidth / 2;
                passData.texHeight = settings.fullResolution ? hdCamera.actualHeight : hdCamera.actualHeight / 2;
                passData.viewCount = hdCamera.viewCount;

                // Set the generation parameters
                passData.minSmoothness = settings.minSmoothness;

                // Grab the right kernel
                passData.directionGenCS = m_GlobalSettings.renderPipelineRayTracingResources.reflectionRaytracingCS;
                if (settings.fullResolution)
                    passData.dirGenKernel = transparent ? m_RaytracingReflectionsTransparentFullResKernel : m_RaytracingReflectionsFullResKernel;
                else
                    passData.dirGenKernel = transparent ? m_RaytracingReflectionsTransparentHalfResKernel : m_RaytracingReflectionsHalfResKernel;

                // Grab the additional parameters
                passData.ditheredTextureSet = GetBlueNoiseManager().DitheredTextureSet8SPP();
                passData.shaderVariablesRayTracingCB = m_ShaderVariablesRayTracingCB;

                passData.depthBuffer = builder.ReadTexture(depthBuffer);
                passData.stencilBuffer = builder.ReadTexture(stencilBuffer);
                passData.normalBuffer = builder.ReadTexture(normalBuffer);
                passData.clearCoatMaskTexture = builder.ReadTexture(clearCoatTexture);
                passData.outputBuffer = builder.WriteTexture(renderGraph.CreateTexture(new TextureDesc(Vector2.one, true, true)
                    { colorFormat = GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite = true, name = "Reflection Ray Directions" }));

                builder.SetRenderFunc(
                    (DirGenRTRPassData data, RenderGraphContext ctx) =>
                    {
                        // TODO: check if this is required, i do not think so
                        CoreUtils.SetRenderTarget(ctx.cmd, data.outputBuffer, ClearFlag.Color, clearColor: Color.black);

                        // Inject the ray-tracing sampling data
                        BlueNoise.BindDitheredTextureSet(ctx.cmd, data.ditheredTextureSet);

                        // Bind all the required scalars to the CB
                        data.shaderVariablesRayTracingCB._RaytracingReflectionMinSmoothness = data.minSmoothness;
                        ConstantBuffer.PushGlobal(ctx.cmd, data.shaderVariablesRayTracingCB, HDShaderIDs._ShaderVariablesRaytracing);

                        // Bind all the required textures
                        ctx.cmd.SetComputeTextureParam(data.directionGenCS, data.dirGenKernel, HDShaderIDs._DepthTexture, data.depthBuffer);
                        ctx.cmd.SetComputeTextureParam(data.directionGenCS, data.dirGenKernel, HDShaderIDs._NormalBufferTexture, data.normalBuffer);
                        ctx.cmd.SetComputeTextureParam(data.directionGenCS, data.dirGenKernel, HDShaderIDs._SsrClearCoatMaskTexture, data.clearCoatMaskTexture);
                        ctx.cmd.SetComputeIntParam(data.directionGenCS, HDShaderIDs._SsrStencilBit, (int)StencilUsage.TraceReflectionRay);
                        ctx.cmd.SetComputeTextureParam(data.directionGenCS, data.dirGenKernel, HDShaderIDs._StencilTexture, data.stencilBuffer, 0, RenderTextureSubElement.Stencil);

                        // Bind the output buffers
                        ctx.cmd.SetComputeTextureParam(data.directionGenCS, data.dirGenKernel, HDShaderIDs._RaytracingDirectionBuffer, data.outputBuffer);

                        // Evaluate the dispatch parameters
                        int numTilesX = (data.texWidth + (rtReflectionsComputeTileSize - 1)) / rtReflectionsComputeTileSize;
                        int numTilesY = (data.texHeight + (rtReflectionsComputeTileSize - 1)) / rtReflectionsComputeTileSize;
                        // Compute the directions
                        ctx.cmd.DispatchCompute(data.directionGenCS, data.dirGenKernel, numTilesX, numTilesY, data.viewCount);
                    });

                return passData.outputBuffer;
            }
        }

        #endregion

        #region AdjustWeight

        class AdjustWeightRTRPassData
        {
            // Camera parameters
            public int texWidth;
            public int texHeight;
            public int viewCount;

            public float minSmoothness;
            public float smoothnessFadeStart;
            public int fallbackHierarchy;

            // Other parameters
            public ComputeShader reflectionFilterCS;
            public int adjustWeightKernel;
            public ShaderVariablesRaytracing shaderVariablesRayTracingCB;

            public TextureHandle depthStencilBuffer;
            public TextureHandle normalBuffer;
            public TextureHandle clearCoatMaskTexture;
            public TextureHandle lightingTexture;
            public TextureHandle directionTexture;
            public TextureHandle outputTexture;
        }

        TextureHandle AdjustWeightRTR(RenderGraph renderGraph, HDCamera hdCamera, ScreenSpaceReflection settings,
            TextureHandle depthPyramid, TextureHandle normalBuffer, TextureHandle clearCoatTexture, TextureHandle lightingTexture, TextureHandle directionTexture)
        {
            using (var builder = renderGraph.AddRenderPass<AdjustWeightRTRPassData>("Adjust Weight RTR", out var passData, ProfilingSampler.Get(HDProfileId.RaytracingReflectionAdjustWeight)))
            {
                builder.EnableAsyncCompute(false);

                passData.texWidth = settings.fullResolution ? hdCamera.actualWidth : hdCamera.actualWidth / 2;
                passData.texHeight = settings.fullResolution ? hdCamera.actualHeight : hdCamera.actualHeight / 2;
                passData.viewCount = hdCamera.viewCount;

                // Requires parameters
                passData.minSmoothness = settings.minSmoothness;
                passData.smoothnessFadeStart = settings.smoothnessFadeStart;
                passData.fallbackHierarchy = (int)(settings.fallbackHierachy.value);

                // Other parameters
                passData.reflectionFilterCS = m_GlobalSettings.renderPipelineRayTracingResources.reflectionBilateralFilterCS;
                passData.adjustWeightKernel = settings.fullResolution ? m_ReflectionAdjustWeightKernel : m_ReflectionRescaleAndAdjustWeightKernel;
                passData.shaderVariablesRayTracingCB = m_ShaderVariablesRayTracingCB;

                passData.depthStencilBuffer = builder.ReadTexture(depthPyramid);
                passData.normalBuffer = builder.ReadTexture(normalBuffer);
                passData.clearCoatMaskTexture = builder.ReadTexture(clearCoatTexture);
                passData.lightingTexture = builder.ReadTexture(lightingTexture);
                passData.directionTexture = builder.ReadTexture(directionTexture);
                passData.outputTexture = builder.WriteTexture(renderGraph.CreateTexture(new TextureDesc(Vector2.one, true, true)
                    { colorFormat = GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite = true, name = "Reflection Ray Reflections" }));

                builder.SetRenderFunc(
                    (AdjustWeightRTRPassData data, RenderGraphContext ctx) =>
                    {
                        // Bind all the required scalars to the CB
                        data.shaderVariablesRayTracingCB._RaytracingReflectionMinSmoothness = data.minSmoothness;
                        data.shaderVariablesRayTracingCB._RaytracingReflectionSmoothnessFadeStart = data.smoothnessFadeStart;
                        data.shaderVariablesRayTracingCB._RayTracingFallbackHierarchy = data.fallbackHierarchy;
                        ConstantBuffer.PushGlobal(ctx.cmd, data.shaderVariablesRayTracingCB, HDShaderIDs._ShaderVariablesRaytracing);

                        // Source input textures
                        ctx.cmd.SetComputeTextureParam(data.reflectionFilterCS, data.adjustWeightKernel, HDShaderIDs._DepthTexture, data.depthStencilBuffer);
                        ctx.cmd.SetComputeTextureParam(data.reflectionFilterCS, data.adjustWeightKernel, HDShaderIDs._SsrClearCoatMaskTexture, data.clearCoatMaskTexture);
                        ctx.cmd.SetComputeTextureParam(data.reflectionFilterCS, data.adjustWeightKernel, HDShaderIDs._NormalBufferTexture, data.normalBuffer);
                        ctx.cmd.SetComputeTextureParam(data.reflectionFilterCS, data.adjustWeightKernel, HDShaderIDs._DirectionPDFTexture, data.directionTexture);

                        // Lighting textures
                        ctx.cmd.SetComputeTextureParam(data.reflectionFilterCS, data.adjustWeightKernel, HDShaderIDs._SsrLightingTextureRW, data.lightingTexture);

                        // Output texture
                        ctx.cmd.SetComputeTextureParam(data.reflectionFilterCS, data.adjustWeightKernel, HDShaderIDs._RaytracingReflectionTexture, data.outputTexture);

                        // Compute the texture
                        int numTilesXHR = (data.texWidth + (rtReflectionsComputeTileSize - 1)) / rtReflectionsComputeTileSize;
                        int numTilesYHR = (data.texHeight + (rtReflectionsComputeTileSize - 1)) / rtReflectionsComputeTileSize;
                        ctx.cmd.DispatchCompute(data.reflectionFilterCS, data.adjustWeightKernel, numTilesXHR, numTilesYHR, data.viewCount);
                    });

                return passData.outputTexture;
            }
        }

        #endregion

        #region Upscale

        class UpscaleRTRPassData
        {
            // Camera parameters
            public int texWidth;
            public int texHeight;
            public int viewCount;

            // Other parameters
            public ComputeShader reflectionFilterCS;
            public int upscaleKernel;

            public TextureHandle depthStencilBuffer;
            public TextureHandle lightingTexture;
            public TextureHandle outputTexture;
        }

        TextureHandle UpscaleRTR(RenderGraph renderGraph, HDCamera hdCamera, TextureHandle depthBuffer, TextureHandle lightingTexture)
        {
            using (var builder = renderGraph.AddRenderPass<UpscaleRTRPassData>("Upscale RTR", out var passData, ProfilingSampler.Get(HDProfileId.RaytracingReflectionUpscale)))
            {
                builder.EnableAsyncCompute(false);

                passData.texWidth = hdCamera.actualWidth;
                passData.texHeight = hdCamera.actualHeight;
                passData.viewCount = hdCamera.viewCount;
                passData.reflectionFilterCS = m_GlobalSettings.renderPipelineRayTracingResources.reflectionBilateralFilterCS;
                passData.upscaleKernel = m_ReflectionUpscaleKernel;

                passData.depthStencilBuffer = builder.ReadTexture(depthBuffer);
                passData.lightingTexture = builder.ReadTexture(lightingTexture);
                passData.outputTexture = builder.WriteTexture(renderGraph.CreateTexture(new TextureDesc(Vector2.one, true, true)
                    { colorFormat = GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite = true, name = "Reflection Ray Reflections" }));

                builder.SetRenderFunc(
                    (UpscaleRTRPassData data, RenderGraphContext ctx) =>
                    {
                        // Input textures
                        ctx.cmd.SetComputeTextureParam(data.reflectionFilterCS, data.upscaleKernel, HDShaderIDs._DepthTexture, data.depthStencilBuffer);
                        ctx.cmd.SetComputeTextureParam(data.reflectionFilterCS, data.upscaleKernel, HDShaderIDs._SsrLightingTextureRW, data.lightingTexture);

                        // Output texture
                        ctx.cmd.SetComputeTextureParam(data.reflectionFilterCS, data.upscaleKernel, HDShaderIDs._RaytracingReflectionTexture, data.outputTexture);

                        // Compute the texture
                        int numTilesXHR = (data.texWidth + (rtReflectionsComputeTileSize - 1)) / rtReflectionsComputeTileSize;
                        int numTilesYHR = (data.texHeight + (rtReflectionsComputeTileSize - 1)) / rtReflectionsComputeTileSize;
                        ctx.cmd.DispatchCompute(data.reflectionFilterCS, data.upscaleKernel, numTilesXHR, numTilesYHR, data.viewCount);
                    });

                return passData.outputTexture;
            }
        }

        #endregion

        static RTHandle RequestRayTracedReflectionsHistoryTexture(HDCamera hdCamera)
        {
            return hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.RaytracedReflection)
                ?? hdCamera.AllocHistoryFrameRT((int)HDCameraFrameHistoryType.RaytracedReflection,
                ReflectionHistoryBufferAllocatorFunction, 1);
        }

        static RTHandle RequestRayTracedReflectionsHistorySampleCountTexture(HDCamera hdCamera)
        {
            return hdCamera.GetCurrentFrameRT((int)HDCameraFrameHistoryType.RaytracedReflectionSampleCount)
                ?? hdCamera.AllocHistoryFrameRT((int)HDCameraFrameHistoryType.RaytracedReflectionSampleCount,
                ReflectionHistorySampleCountBufferAllocatorFunction, 1);
        }

        DeferredLightingRTParameters PrepareReflectionDeferredLightingRTParameters(HDCamera hdCamera, bool transparent)
        {
            DeferredLightingRTParameters deferredParameters = new DeferredLightingRTParameters();

            // Fetch the GI volume component
            var settings = hdCamera.volumeStack.GetComponent<ScreenSpaceReflection>();
            RayTracingSettings rTSettings = hdCamera.volumeStack.GetComponent<RayTracingSettings>();

            // Make sure the binning buffer has the right size
            CheckBinningBuffersSize(hdCamera);

            // Generic attributes
            deferredParameters.rayBinning = true;
            deferredParameters.layerMask.value = (int)RayTracingRendererFlag.Reflection;
            deferredParameters.diffuseLightingOnly = false;
            deferredParameters.halfResolution = !settings.fullResolution;
            deferredParameters.rayCountType = (int)RayCountValues.ReflectionDeferred;
            deferredParameters.lodBias = settings.textureLodBias.value;
            deferredParameters.fallbackHierarchy = (int)(settings.fallbackHierachy.value);

            // Ray Marching parameters
            deferredParameters.mixedTracing = (settings.tracing.value == RayCastingMode.Mixed && hdCamera.frameSettings.litShaderMode == LitShaderMode.Deferred) && !transparent;
            deferredParameters.raySteps = settings.rayMaxIterationsRT;
            deferredParameters.nearClipPlane = hdCamera.camera.nearClipPlane;
            deferredParameters.farClipPlane = hdCamera.camera.farClipPlane;

            // Camera data
            deferredParameters.width = hdCamera.actualWidth;
            deferredParameters.height = hdCamera.actualHeight;
            deferredParameters.viewCount = hdCamera.viewCount;

            // Compute buffers
            deferredParameters.rayBinResult = m_RayBinResult;
            deferredParameters.rayBinSizeResult = m_RayBinSizeResult;
            deferredParameters.accelerationStructure = RequestAccelerationStructure();
            deferredParameters.lightCluster = RequestLightCluster();
            deferredParameters.mipChainBuffer = m_DepthBufferMipChainInfo.GetOffsetBufferData(m_DepthPyramidMipLevelOffsetsBuffer);

            // Shaders
            deferredParameters.rayMarchingCS = m_GlobalSettings.renderPipelineRayTracingResources.rayMarchingCS;
            deferredParameters.gBufferRaytracingRT = m_GlobalSettings.renderPipelineRayTracingResources.gBufferRaytracingRT;
            deferredParameters.deferredRaytracingCS = m_GlobalSettings.renderPipelineRayTracingResources.deferredRaytracingCS;
            deferredParameters.rayBinningCS = m_GlobalSettings.renderPipelineRayTracingResources.rayBinningCS;

            // Make a copy of the previous values that were defined in the CB
            deferredParameters.raytracingCB = m_ShaderVariablesRayTracingCB;
            // Override the ones we need to
            deferredParameters.raytracingCB._RaytracingRayMaxLength = settings.rayLength;
            deferredParameters.raytracingCB._RaytracingIntensityClamp = settings.clampValue;
            deferredParameters.raytracingCB._RaytracingPreExposition = 0;
            deferredParameters.raytracingCB._RayTracingDiffuseLightingOnly = 0;
            deferredParameters.raytracingCB._RayTracingFallbackHierarchy = deferredParameters.fallbackHierarchy;

            return deferredParameters;
        }

        TextureHandle RenderReflectionsPerformance(RenderGraph renderGraph, HDCamera hdCamera,
            in PrepassOutput prepassOutput, TextureHandle rayCountTexture, TextureHandle clearCoatTexture, Texture skyTexture,
            ShaderVariablesRaytracing shaderVariablesRaytracing, bool transparent)
        {
            // Pointer to the final result
            TextureHandle rtrResult;

            // Fetch all the settings
            ScreenSpaceReflection settings = hdCamera.volumeStack.GetComponent<ScreenSpaceReflection>();

            TextureHandle directionBuffer = DirGenRTR(renderGraph, hdCamera, settings, prepassOutput.depthBuffer, prepassOutput.stencilBuffer, prepassOutput.normalBuffer, clearCoatTexture, transparent);

            DeferredLightingRTParameters deferredParamters = PrepareReflectionDeferredLightingRTParameters(hdCamera, transparent);
            TextureHandle lightingBuffer = DeferredLightingRT(renderGraph, in deferredParamters, directionBuffer, prepassOutput, skyTexture, rayCountTexture);

            rtrResult = AdjustWeightRTR(renderGraph, hdCamera, settings, prepassOutput.depthBuffer, prepassOutput.normalBuffer, clearCoatTexture, lightingBuffer, directionBuffer);

            // Denoise if required
            if (settings.denoise && !transparent)
            {
                bool skyHitsHaveZeroWeight = (settings.fallbackHierachy.value == RayTracingFallbackHierachy.NonRaytracedReflectionProbesAndSky);
                rtrResult = DenoiseReflection(renderGraph, hdCamera, settings.fullResolution, settings.denoiserRadius, singleReflectionBounce: true, settings.affectSmoothSurfaces, skyHitsHaveZeroWeight,
                    rtrResult, prepassOutput.depthBuffer, prepassOutput.normalBuffer, prepassOutput.motionVectorsBuffer, clearCoatTexture);
            }

            // We only need to upscale if the effect was not rendered in full res
            if (!settings.fullResolution)
            {
                rtrResult = UpscaleRTR(renderGraph, hdCamera, prepassOutput.depthBuffer, rtrResult);
            }

            return rtrResult;
        }

        #region Quality
        class TraceQualityRTRPassData
        {
            // Camera parameters
            public int texWidth;
            public int texHeight;
            public int viewCount;

            // Reflection evaluation parameters
            public float clampValue;
            public int reflectSky;
            public float rayLength;
            public int sampleCount;
            public int bounceCount;
            public bool transparent;
            public float minSmoothness;
            public float smoothnessFadeStart;
            public float lodBias;
            public int fallbackHierarchy;
            public int historyAffectSmoothSurfaces;

            // Other parameters
            public RayTracingAccelerationStructure accelerationStructure;
            public HDRaytracingLightCluster lightCluster;
            public BlueNoise.DitheredTextureSet ditheredTextureSet;
            public ShaderVariablesRaytracing shaderVariablesRayTracingCB;
            public Texture skyTexture;
            public RayTracingShader reflectionShader;

            public TextureHandle depthBuffer;
            public TextureHandle stencilBuffer;
            public TextureHandle normalBuffer;
            public TextureHandle clearCoatMaskTexture;
            public TextureHandle rayCountTexture;
            public TextureHandle outputTexture;
        }

        TextureHandle QualityRTR(RenderGraph renderGraph, HDCamera hdCamera, ScreenSpaceReflection settings,
            TextureHandle depthPyramid, TextureHandle stencilBuffer, TextureHandle normalBuffer, TextureHandle clearCoatTexture, TextureHandle rayCountTexture, bool transparent)
        {
            using (var builder = renderGraph.AddRenderPass<TraceQualityRTRPassData>("Quality RT Reflections", out var passData, ProfilingSampler.Get(HDProfileId.RaytracingReflectionEvaluation)))
            {
                builder.EnableAsyncCompute(false);

                // Camera parameters
                passData.texWidth = hdCamera.actualWidth;
                passData.texHeight = hdCamera.actualHeight;
                passData.viewCount = hdCamera.viewCount;

                // Reflection evaluation parameters
                passData.clampValue = settings.clampValue;
                passData.reflectSky = settings.reflectSky.value ? 1 : 0;
                passData.rayLength = settings.rayLength;
                passData.sampleCount = settings.sampleCount.value;
                passData.bounceCount = settings.bounceCount.value;
                passData.transparent = transparent;
                passData.minSmoothness = settings.minSmoothness;
                passData.smoothnessFadeStart = settings.smoothnessFadeStart;
                passData.lodBias = settings.textureLodBias.value;
                passData.fallbackHierarchy = (int)settings.fallbackHierachy.value;
                passData.historyAffectSmoothSurfaces = settings.affectSmoothSurfaces ? 1 : 0;

                // Other parameters
                passData.accelerationStructure = RequestAccelerationStructure();
                passData.lightCluster = RequestLightCluster();
                passData.ditheredTextureSet = GetBlueNoiseManager().DitheredTextureSet8SPP();
                passData.shaderVariablesRayTracingCB = m_ShaderVariablesRayTracingCB;
                passData.skyTexture = m_SkyManager.GetSkyReflection(hdCamera);
                passData.reflectionShader = m_GlobalSettings.renderPipelineRayTracingResources.reflectionRaytracingRT;

                passData.depthBuffer = builder.ReadTexture(depthPyramid);
                passData.stencilBuffer = builder.ReadTexture(stencilBuffer);
                passData.normalBuffer = builder.ReadTexture(normalBuffer);
                passData.clearCoatMaskTexture = builder.ReadTexture(clearCoatTexture);
                passData.rayCountTexture = builder.ReadWriteTexture(rayCountTexture);
                passData.outputTexture = builder.WriteTexture(renderGraph.CreateTexture(new TextureDesc(Vector2.one, true, true)
                    { colorFormat = GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite = true, name = "Ray Traced Reflections" }));

                builder.SetRenderFunc(
                    (TraceQualityRTRPassData data, RenderGraphContext ctx) =>
                    {
                        // Define the shader pass to use for the reflection pass
                        ctx.cmd.SetRayTracingShaderPass(data.reflectionShader, "IndirectDXR");

                        // Set the acceleration structure for the pass
                        ctx.cmd.SetRayTracingAccelerationStructure(data.reflectionShader, HDShaderIDs._RaytracingAccelerationStructureName, data.accelerationStructure);

                        // Global reflection parameters
                        data.shaderVariablesRayTracingCB._RaytracingIntensityClamp = data.clampValue;
                        // Inject the ray generation data
                        data.shaderVariablesRayTracingCB._RaytracingRayMaxLength = data.rayLength;
                        data.shaderVariablesRayTracingCB._RaytracingNumSamples = data.sampleCount;
                        // Set the number of bounces for reflections
                        data.shaderVariablesRayTracingCB._RaytracingMaxRecursion = data.bounceCount;
                        data.shaderVariablesRayTracingCB._RayTracingDiffuseLightingOnly = 0;
                        // Bind all the required scalars to the CB
                        data.shaderVariablesRayTracingCB._RaytracingReflectionMinSmoothness = data.minSmoothness;
                        data.shaderVariablesRayTracingCB._RaytracingReflectionSmoothnessFadeStart = data.smoothnessFadeStart;
                        data.shaderVariablesRayTracingCB._RayTracingLodBias = data.lodBias;
                        data.shaderVariablesRayTracingCB._RayTracingFallbackHierarchy = data.fallbackHierarchy;
                        ConstantBuffer.PushGlobal(ctx.cmd, data.shaderVariablesRayTracingCB, HDShaderIDs._ShaderVariablesRaytracing);

                        // Inject the ray-tracing sampling data
                        BlueNoise.BindDitheredTextureSet(ctx.cmd, data.ditheredTextureSet);

                        // Set the data for the ray generation
                        ctx.cmd.SetRayTracingTextureParam(data.reflectionShader, HDShaderIDs._SsrLightingTextureRW, data.outputTexture);
                        ctx.cmd.SetRayTracingTextureParam(data.reflectionShader, HDShaderIDs._DepthTexture, data.depthBuffer);
                        ctx.cmd.SetRayTracingTextureParam(data.reflectionShader, HDShaderIDs._NormalBufferTexture, data.normalBuffer);
                        ctx.cmd.SetGlobalTexture(HDShaderIDs._StencilTexture, data.stencilBuffer, RenderTextureSubElement.Stencil);
                        ctx.cmd.SetRayTracingIntParams(data.reflectionShader, HDShaderIDs._SsrStencilBit, (int)StencilUsage.TraceReflectionRay);
                        ctx.cmd.SetRayTracingIntParams(data.reflectionShader, HDShaderIDs._AffectSmoothSurfaces, data.historyAffectSmoothSurfaces);

                        // Set ray count texture
                        ctx.cmd.SetRayTracingTextureParam(data.reflectionShader, HDShaderIDs._RayCountTexture, data.rayCountTexture);

                        // Bind the lightLoop data
                        data.lightCluster.BindLightClusterData(ctx.cmd);

                        // Evaluate the clear coat mask texture based on the lit shader mode
                        ctx.cmd.SetRayTracingTextureParam(data.reflectionShader, HDShaderIDs._SsrClearCoatMaskTexture, data.clearCoatMaskTexture);

                        // Set the data for the ray miss
                        ctx.cmd.SetRayTracingTextureParam(data.reflectionShader, HDShaderIDs._SkyTexture, data.skyTexture);

                        // Only use the shader variant that has multi bounce if the bounce count > 1
                        CoreUtils.SetKeyword(ctx.cmd, "MULTI_BOUNCE_INDIRECT", data.bounceCount > 1);

                        // Run the computation
                        ctx.cmd.DispatchRays(data.reflectionShader, data.transparent ? m_RayGenIntegrationTransparentName : m_RayGenIntegrationName, (uint)data.texWidth, (uint)data.texHeight, (uint)data.viewCount);

                        // Disable multi-bounce
                        CoreUtils.SetKeyword(ctx.cmd, "MULTI_BOUNCE_INDIRECT", false);
                    });

                return passData.outputTexture;
            }
        }

        TextureHandle RenderReflectionsQuality(RenderGraph renderGraph, HDCamera hdCamera,
            TextureHandle depthPyramid, TextureHandle stencilBuffer, TextureHandle normalBuffer, TextureHandle motionVectors, TextureHandle rayCountTexture, TextureHandle clearCoatTexture, Texture skyTexture,
            ShaderVariablesRaytracing shaderVariablesRaytracing, bool transparent)
        {
            TextureHandle rtrResult;

            var settings = hdCamera.volumeStack.GetComponent<ScreenSpaceReflection>();
            rtrResult = QualityRTR(renderGraph, hdCamera, settings, depthPyramid, stencilBuffer, normalBuffer, clearCoatTexture, rayCountTexture, transparent);

            // Denoise if required
            if (settings.denoise && !transparent)
            {
                bool skyHitsHaveZeroWeight = (settings.fallbackHierachy.value == RayTracingFallbackHierachy.NonRaytracedReflectionProbesAndSky);
                rtrResult = DenoiseReflection(renderGraph, hdCamera, fullResolution: true, settings.denoiserRadius, settings.bounceCount == 1, settings.affectSmoothSurfaces, skyHitsHaveZeroWeight,
                    rtrResult, depthPyramid, normalBuffer, motionVectors, clearCoatTexture);
            }

            return rtrResult;
        }

        #endregion

        TextureHandle DenoiseReflection(RenderGraph renderGraph, HDCamera hdCamera, bool fullResolution, int denoiserRadius, bool singleReflectionBounce, bool affectSmoothSurfaces, bool skyHitsHaveZeroWeight,
            TextureHandle input, TextureHandle depthPyramid, TextureHandle normalBuffer, TextureHandle motionVectors, TextureHandle clearCoatTexture)
        {
            // Prepare the parameters and the resources
            HDReflectionDenoiser reflectionDenoiser = GetReflectionDenoiser();
            float historyValidity = EvaluateRayTracedReflectionHistoryValidity(hdCamera, fullResolution, true);

            RTHandle historySignal = RequestRayTracedReflectionsHistoryTexture(hdCamera);
            RTHandle historySampleCount = skyHitsHaveZeroWeight ? RequestRayTracedReflectionsHistorySampleCountTexture(hdCamera) : renderGraph.defaultResources.emptyReadWriteXRRTH;
            // See DenoiseBuffer in HDReflectionDenoiser.cs : guards in compute dont seem to work so always bind something on the UAV with emptyReadWriteXRRTH.
            var rtrResult = reflectionDenoiser.DenoiseRTR(renderGraph, hdCamera, historyValidity, denoiserRadius, fullResolution, singleReflectionBounce, affectSmoothSurfaces, skyHitsHaveZeroWeight, depthPyramid, normalBuffer, motionVectors, clearCoatTexture, input, historySignal, historySampleCount);
            PropagateRayTracedReflectionsHistoryValidity(hdCamera, fullResolution, true);

            return rtrResult;
        }

        TextureHandle RenderRayTracedReflections(RenderGraph renderGraph, HDCamera hdCamera,
            in PrepassOutput prepassOutput, TextureHandle clearCoatTexture, Texture skyTexture, TextureHandle rayCountTexture,
            ShaderVariablesRaytracing shaderVariablesRaytracing, bool transparent)
        {
            ScreenSpaceReflection reflectionSettings = hdCamera.volumeStack.GetComponent<ScreenSpaceReflection>();

            bool qualityMode = false;

            // Based on what the asset supports, follow the volume or force the right mode.
            if (m_Asset.currentPlatformRenderPipelineSettings.supportedRayTracingMode == RenderPipelineSettings.SupportedRayTracingMode.Both)
                qualityMode = (reflectionSettings.tracing.value == RayCastingMode.RayTracing) && (reflectionSettings.mode.value == RayTracingMode.Quality);
            else
                qualityMode = m_Asset.currentPlatformRenderPipelineSettings.supportedRayTracingMode == RenderPipelineSettings.SupportedRayTracingMode.Quality;


            if (qualityMode)
                return RenderReflectionsQuality(renderGraph, hdCamera,
                    prepassOutput.depthBuffer, prepassOutput.stencilBuffer, prepassOutput.normalBuffer, prepassOutput.resolvedMotionVectorsBuffer, rayCountTexture, clearCoatTexture, skyTexture,
                    shaderVariablesRaytracing, transparent);
            else
                return RenderReflectionsPerformance(renderGraph, hdCamera,
                    prepassOutput, rayCountTexture, clearCoatTexture, skyTexture,
                    shaderVariablesRaytracing, transparent);
        }
    }
}
