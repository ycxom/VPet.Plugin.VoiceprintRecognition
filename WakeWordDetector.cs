using System;
using System.Collections.Generic;
using System.Linq;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// 唤醒词特征模板
    /// 存储 Mel 频谱特征用于 DTW 匹配
    /// </summary>
    public class WakeWordEnvelope
    {
        /// <summary>
        /// Mel 频谱特征 (扁平化 [NumFrames × NumBands])
        /// </summary>
        public float[] Features { get; set; }

        /// <summary>
        /// 帧数
        /// </summary>
        public int NumFrames { get; set; }

        /// <summary>
        /// Mel 频带数
        /// </summary>
        public int NumBands { get; set; }

        /// <summary>
        /// 录音时长 (秒)
        /// </summary>
        public float Duration { get; set; }

        /// <summary>
        /// 录入条件: normal, far, close, loud, quiet
        /// </summary>
        public string Condition { get; set; } = "normal";
    }

    /// <summary>
    /// 唤醒词检测器
    /// 基于 Mel 频谱特征 + DTW 模式匹配
    /// 通过频谱形状区分不同词语，比纯能量包络更精确
    /// </summary>
    public class WakeWordDetector
    {
        // 帧参数 (与 CAM++ 声纹模型一致)
        private const int FRAME_SIZE = 400;       // 25ms at 16kHz
        private const int HOP_SIZE = 160;         // 10ms
        private const int FFT_SIZE = 512;         // 下一个 2 的幂次
        private const int N_MELS = 20;            // Mel 频带数
        private const int DETECT_SAMPLE_RATE = 16000;
        private const float SILENCE_ENERGY_THRESHOLD = 0.005f;

        // 静态 Mel 滤波器（线程安全懒初始化）
        private static float[,] _melFilters;
        private static readonly object _filterLock = new object();

        private readonly Action<string> _logDebug;

        public WakeWordDetector(Action<string> logDebug = null)
        {
            _logDebug = logDebug ?? (s => { });
            EnsureMelFilters();
        }

        private static void EnsureMelFilters()
        {
            if (_melFilters == null)
            {
                lock (_filterLock)
                {
                    if (_melFilters == null)
                        _melFilters = CreateMelFilterbank(FFT_SIZE, N_MELS, DETECT_SAMPLE_RATE);
                }
            }
        }

        /// <summary>
        /// 从 PCM 音频提取 Mel 频谱特征模板
        /// </summary>
        public static WakeWordEnvelope ExtractEnvelope(byte[] audioData, int sampleRate = 16000, string condition = "normal")
        {
            EnsureMelFilters();

            // PCM → float
            int sampleCount = audioData.Length / 2;
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                short s = BitConverter.ToInt16(audioData, i * 2);
                samples[i] = s / 32768.0f;
            }

            int numFrames = Math.Max(1, (sampleCount - FRAME_SIZE) / HOP_SIZE + 1);
            int nFreqs = FFT_SIZE / 2 + 1;

            var features = new float[numFrames * N_MELS];
            var frameEnergies = new float[numFrames];

            // Hamming 窗
            var window = new float[FRAME_SIZE];
            for (int i = 0; i < FRAME_SIZE; i++)
                window[i] = 0.54f - 0.46f * (float)Math.Cos(2 * Math.PI * i / (FRAME_SIZE - 1));

            for (int f = 0; f < numFrames; f++)
            {
                int start = f * HOP_SIZE;

                // 加窗 + 零填充到 FFT_SIZE
                var real = new float[FFT_SIZE];
                var imag = new float[FFT_SIZE];
                for (int i = 0; i < FRAME_SIZE; i++)
                {
                    int idx = start + i;
                    real[i] = (idx < sampleCount ? samples[idx] : 0) * window[i];
                }

                // FFT (radix-2)
                FFT(real, imag, FFT_SIZE);

                // 功率谱
                var powerSpec = new float[nFreqs];
                float energy = 0;
                for (int k = 0; k < nFreqs; k++)
                {
                    powerSpec[k] = real[k] * real[k] + imag[k] * imag[k];
                    energy += powerSpec[k];
                }
                frameEnergies[f] = energy / nFreqs;

                // Mel 滤波器 → log
                for (int m = 0; m < N_MELS; m++)
                {
                    float val = 0;
                    for (int k = 0; k < nFreqs; k++)
                        val += _melFilters[m, k] * powerSpec[k];
                    features[f * N_MELS + m] = (float)Math.Log(Math.Max(val, 1e-10f));
                }
            }

            // CMVN: 减去每个频带的均值（消除音量/距离差异）
            for (int m = 0; m < N_MELS; m++)
            {
                float mean = 0;
                for (int f = 0; f < numFrames; f++)
                    mean += features[f * N_MELS + m];
                mean /= numFrames;

                for (int f = 0; f < numFrames; f++)
                    features[f * N_MELS + m] -= mean;
            }

            // 裁剪前后静音帧
            int trimStart = 0;
            while (trimStart < numFrames && frameEnergies[trimStart] < SILENCE_ENERGY_THRESHOLD)
                trimStart++;
            int trimEnd = numFrames - 1;
            while (trimEnd > trimStart && frameEnergies[trimEnd] < SILENCE_ENERGY_THRESHOLD)
                trimEnd--;

            if (trimStart > trimEnd)
                return new WakeWordEnvelope { Features = new float[N_MELS], NumFrames = 1, NumBands = N_MELS, Duration = 0, Condition = condition };

            int trimmedFrames = trimEnd - trimStart + 1;
            var trimmedFeatures = new float[trimmedFrames * N_MELS];
            Array.Copy(features, trimStart * N_MELS, trimmedFeatures, 0, trimmedFrames * N_MELS);

            return new WakeWordEnvelope
            {
                Features = trimmedFeatures,
                NumFrames = trimmedFrames,
                NumBands = N_MELS,
                Duration = sampleCount / (float)sampleRate,
                Condition = condition
            };
        }

        /// <summary>
        /// 将输入音频与所有参考模板匹配，返回最佳相似度 (0~1)
        /// </summary>
        public float Match(byte[] audioData, List<WakeWordEnvelope> references, int sampleRate = 16000)
        {
            if (references == null || references.Count == 0)
                return 0;

            var input = ExtractEnvelope(audioData, sampleRate);
            if (input.NumFrames < 3)
            {
                _logDebug("输入特征太短，跳过匹配");
                return 0;
            }

            float bestSimilarity = 0;
            string bestCondition = "";

            foreach (var reference in references)
            {
                if (reference.Features == null || reference.NumFrames < 3)
                    continue;

                // 确保维度一致
                int bands = Math.Min(input.NumBands, reference.NumBands);
                if (bands <= 0) continue;

                float similarity = ComputeDtwSimilarity(
                    input.Features, input.NumFrames,
                    reference.Features, reference.NumFrames,
                    bands);

                if (similarity > bestSimilarity)
                {
                    bestSimilarity = similarity;
                    bestCondition = reference.Condition ?? "unknown";
                }
            }

            _logDebug($"唤醒词频谱匹配: 最佳相似度={bestSimilarity:F3} (条件={bestCondition}), 输入帧={input.NumFrames}, 参考数={references.Count}");
            return bestSimilarity;
        }

        /// <summary>
        /// 使用 DTW 计算两个 Mel 频谱序列的相似度 (0~1)
        /// 代价函数为帧间欧氏距离
        /// </summary>
        public static float ComputeDtwSimilarity(float[] featA, int framesA, float[] featB, int framesB, int numBands)
        {
            int m = framesA;
            int n = framesB;

            // 长度差异过大 (>3 倍) 直接拒绝
            if (m > n * 3 || n > m * 3)
                return 0;

            // Sakoe-Chiba 带约束
            int window = Math.Max(m, n) / 3;
            window = Math.Max(window, Math.Abs(m - n) + 1);

            // 滚动数组 DTW
            var prev = new float[n + 1];
            var curr = new float[n + 1];

            for (int j = 0; j <= n; j++) prev[j] = float.MaxValue;
            prev[0] = 0;

            for (int i = 1; i <= m; i++)
            {
                for (int j = 0; j <= n; j++) curr[j] = float.MaxValue;

                for (int j = 1; j <= n; j++)
                {
                    if (Math.Abs(i - j) > window)
                        continue;

                    // 帧间欧氏距离
                    float dist = 0;
                    int offA = (i - 1) * numBands;
                    int offB = (j - 1) * numBands;
                    for (int d = 0; d < numBands; d++)
                    {
                        float diff = featA[offA + d] - featB[offB + d];
                        dist += diff * diff;
                    }
                    dist = (float)Math.Sqrt(dist);

                    float minPrev = prev[j - 1]; // 对角
                    if (prev[j] < minPrev) minPrev = prev[j]; // 上方
                    if (curr[j - 1] < minPrev) minPrev = curr[j - 1]; // 左方

                    if (minPrev == float.MaxValue)
                        continue;

                    curr[j] = dist + minPrev;
                }

                var temp = prev;
                prev = curr;
                curr = temp;
            }

            float totalCost = prev[n];
            if (totalCost == float.MaxValue)
                return 0;

            // 归一化: 路径长度 × sqrt(维度)
            float normalizedCost = totalCost / ((m + n) * (float)Math.Sqrt(numBands));

            // 转换为相似度
            float similarity = 1.0f / (1.0f + normalizedCost);

            return Math.Max(0, similarity);
        }

        #region FFT

        /// <summary>
        /// Radix-2 Cooley-Tukey FFT (原地计算)
        /// 比 O(N²) 的朴素 DFT 快约 50 倍 (N=512)
        /// </summary>
        private static void FFT(float[] real, float[] imag, int n)
        {
            int bits = (int)Math.Round(Math.Log(n, 2));

            // 位反转置换
            for (int i = 0; i < n; i++)
            {
                int j = BitReverse(i, bits);
                if (j > i)
                {
                    float tr = real[i]; real[i] = real[j]; real[j] = tr;
                    float ti = imag[i]; imag[i] = imag[j]; imag[j] = ti;
                }
            }

            // 蝶形运算
            for (int size = 2; size <= n; size *= 2)
            {
                int halfSize = size / 2;
                float angle = -2.0f * (float)Math.PI / size;
                float wR = (float)Math.Cos(angle);
                float wI = (float)Math.Sin(angle);

                for (int start = 0; start < n; start += size)
                {
                    float curR = 1, curI = 0;
                    for (int k = 0; k < halfSize; k++)
                    {
                        int even = start + k;
                        int odd = start + k + halfSize;

                        float tR = curR * real[odd] - curI * imag[odd];
                        float tI = curR * imag[odd] + curI * real[odd];

                        real[odd] = real[even] - tR;
                        imag[odd] = imag[even] - tI;
                        real[even] += tR;
                        imag[even] += tI;

                        float newR = curR * wR - curI * wI;
                        curI = curR * wI + curI * wR;
                        curR = newR;
                    }
                }
            }
        }

        private static int BitReverse(int x, int bits)
        {
            int result = 0;
            for (int i = 0; i < bits; i++)
            {
                result = (result << 1) | (x & 1);
                x >>= 1;
            }
            return result;
        }

        #endregion

        #region Mel 滤波器

        private static float[,] CreateMelFilterbank(int fftSize, int nMels, int sampleRate)
        {
            int nFreqs = fftSize / 2 + 1;
            var filters = new float[nMels, nFreqs];

            float melMin = 2595.0f * (float)Math.Log10(1 + 0f / 700.0f);
            float melMax = 2595.0f * (float)Math.Log10(1 + sampleRate / 2.0f / 700.0f);

            var melPoints = new float[nMels + 2];
            for (int i = 0; i < nMels + 2; i++)
                melPoints[i] = melMin + (melMax - melMin) * i / (nMels + 1);

            var binPoints = new float[nMels + 2];
            for (int i = 0; i < nMels + 2; i++)
            {
                float hz = 700.0f * ((float)Math.Pow(10, melPoints[i] / 2595.0f) - 1);
                binPoints[i] = hz * fftSize / sampleRate;
            }

            for (int m = 0; m < nMels; m++)
            {
                float left = binPoints[m];
                float center = binPoints[m + 1];
                float right = binPoints[m + 2];

                for (int k = 0; k < nFreqs; k++)
                {
                    if (k >= left && k <= center && center > left)
                        filters[m, k] = (k - left) / (center - left);
                    else if (k > center && k <= right && right > center)
                        filters[m, k] = (right - k) / (right - center);
                }

                // Slaney 归一化
                float sum = 0;
                for (int k = 0; k < nFreqs; k++) sum += filters[m, k];
                if (sum > 0)
                    for (int k = 0; k < nFreqs; k++) filters[m, k] /= sum;
            }

            return filters;
        }

        #endregion
    }
}
