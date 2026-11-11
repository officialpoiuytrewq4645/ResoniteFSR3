// Copyright (c) 2024 Nico de Poel
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using UnityEngine;

namespace FidelityFX.FSR3
{
    /// <summary>
    /// All the compute shaders used by the FSR3 Upscaler.
    /// [ResoniteFSR3] The ScriptableObject asset wrapper was removed (shaders are loaded from an AssetBundle at runtime),
    /// and a separate sharpening variant of the accumulate pass was added because Unity 2019 has no compute shader keywords.
    /// </summary>
    [System.Serializable]
    public class Fsr3UpscalerShaders
    {
        public ComputeShader prepareInputsPass;
        public ComputeShader lumaPyramidPass;
        public ComputeShader shadingChangePyramidPass;
        public ComputeShader shadingChangePass;
        public ComputeShader prepareReactivityPass;
        public ComputeShader lumaInstabilityPass;

        /// <summary>Accumulate pass compiled without the sharpening option.</summary>
        public ComputeShader accumulatePass;

        /// <summary>Accumulate pass compiled with the sharpening option.</summary>
        public ComputeShader accumulateSharpenPass;

        public ComputeShader sharpenPass;
        public ComputeShader autoGenReactivePass;
        public ComputeShader tcrAutoGenPass;
        public ComputeShader debugViewPass;

        public Fsr3UpscalerShaders Clone()
        {
            return (Fsr3UpscalerShaders)MemberwiseClone();
        }
    }
}
