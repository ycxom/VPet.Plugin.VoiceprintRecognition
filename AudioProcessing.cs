using System;
using System.Collections.Generic;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// 音频预处理工具：静音裁剪、能量峰值语音段提取。
    /// 唤醒场景下固定取最近 N 秒会混入大量静音/背景，严重拉低声纹相似度。
    /// </summary>
    public static class AudioProcessing
    {
        /// <summary>
        /// 从 PCM16 音频中提取高能量语音核心段。
        /// </summary>
        /// <param name="pcmData">16-bit PCM 小端</param>
        /// <param name="sampleRate">采样率</param>
        /// <param name="channels">声道数</param>
        /// <param name="targetSeconds">目标语音段时长（秒）</param>
        /// <param name="minSeconds">最短保留时长</param>
        /// <param name="maxSeconds">最长保留时长</param>
        /// <param name="energyThresholdRatio">相对峰值能量阈值（0~1）</param>
        /// <returns>裁剪后的 PCM；失败则返回原数据</returns>
        public static byte[] ExtractSpeechSegment(
            byte[] pcmData,
            int sampleRate = 16000,
            int channels = 1,
            float targetSeconds = 1.8f,
            float minSeconds = 0.6f,
            float maxSeconds = 3.0f,
            float energyThresholdRatio = 0.12f)
        {
            if (pcmData == null || pcmData.Length < sampleRate)
                return pcmData;

            int bytesPerSample = 2 * Math.Max(1, channels);
            int totalSamples = pcmData.Length / bytesPerSample;
            if (totalSamples < (int)(minSeconds * sampleRate))
                return pcmData;

            // 按 20ms 帧计算 RMS
            int frameSamples = Math.Max(1, sampleRate / 50);
            int frameCount = Math.Max(1, totalSamples / frameSamples);
            var frameRms = new float[frameCount];
            float peakRms = 0f;

            for (int f = 0; f < frameCount; f++)
            {
                int start = f * frameSamples;
                int end = Math.Min(totalSamples, start + frameSamples);
                double sumSq = 0;
                int n = 0;
                for (int i = start; i < end; i++)
                {
                    int byteIndex = i * bytesPerSample;
                    if (byteIndex + 1 >= pcmData.Length) break;
                    short s = (short)(pcmData[byteIndex] | (pcmData[byteIndex + 1] << 8));
                    float v = s / 32768f;
                    sumSq += v * v;
                    n++;
                }
                float rms = n > 0 ? (float)Math.Sqrt(sumSq / n) : 0f;
                frameRms[f] = rms;
                if (rms > peakRms) peakRms = rms;
            }

            if (peakRms < 1e-5f)
                return pcmData;

            float thr = peakRms * energyThresholdRatio;
            // 找到最高能量帧，向两侧扩展到低于阈值
            int peakFrame = 0;
            for (int f = 1; f < frameCount; f++)
            {
                if (frameRms[f] > frameRms[peakFrame])
                    peakFrame = f;
            }

            int left = peakFrame;
            int right = peakFrame;
            while (left > 0 && frameRms[left - 1] >= thr) left--;
            while (right < frameCount - 1 && frameRms[right + 1] >= thr) right++;

            // 在语音段两端各保留少量上下文
            int padFrames = Math.Max(1, sampleRate / frameSamples / 10); // ~100ms
            left = Math.Max(0, left - padFrames);
            right = Math.Min(frameCount - 1, right + padFrames);

            int startSample = left * frameSamples;
            int endSample = Math.Min(totalSamples, (right + 1) * frameSamples);
            int speechSamples = endSample - startSample;

            int minSamples = (int)(minSeconds * sampleRate);
            int maxSamples = (int)(maxSeconds * sampleRate);
            int targetSamples = (int)(targetSeconds * sampleRate);

            // 太短：以峰值为中心扩展到 min/target
            if (speechSamples < minSamples)
            {
                int center = (startSample + endSample) / 2;
                int half = Math.Max(minSamples, targetSamples) / 2;
                startSample = Math.Max(0, center - half);
                endSample = Math.Min(totalSamples, startSample + Math.Max(minSamples, targetSamples));
                startSample = Math.Max(0, endSample - Math.Max(minSamples, targetSamples));
                speechSamples = endSample - startSample;
            }

            // 太长：保留能量最高的 target 窗口
            if (speechSamples > maxSamples)
            {
                int windowFrames = Math.Max(1, targetSamples / frameSamples);
                windowFrames = Math.Min(windowFrames, frameCount);

                double bestSum = double.NegativeInfinity;
                int bestStartFrame = left;
                int searchStart = left;
                int searchEnd = Math.Max(left, right - windowFrames + 1);
                for (int sf = searchStart; sf <= searchEnd; sf++)
                {
                    double sum = 0;
                    int ef = Math.Min(frameCount, sf + windowFrames);
                    for (int f = sf; f < ef; f++) sum += frameRms[f];
                    if (sum > bestSum)
                    {
                        bestSum = sum;
                        bestStartFrame = sf;
                    }
                }

                startSample = bestStartFrame * frameSamples;
                endSample = Math.Min(totalSamples, startSample + Math.Min(maxSamples, targetSamples));
                speechSamples = endSample - startSample;
            }

            if (speechSamples <= 0 || speechSamples >= totalSamples)
                return pcmData;

            int startByte = startSample * bytesPerSample;
            int byteLen = speechSamples * bytesPerSample;
            if (startByte < 0 || startByte + byteLen > pcmData.Length)
                return pcmData;

            var result = new byte[byteLen];
            Buffer.BlockCopy(pcmData, startByte, result, 0, byteLen);
            return result;
        }

        /// <summary>
        /// 裁剪首尾静音（简单 RMS 门限）。
        /// </summary>
        public static byte[] TrimSilence(
            byte[] pcmData,
            int sampleRate = 16000,
            int channels = 1,
            float silenceRms = 0.008f,
            float padSeconds = 0.08f)
        {
            if (pcmData == null || pcmData.Length < 4)
                return pcmData;

            int bytesPerSample = 2 * Math.Max(1, channels);
            int totalSamples = pcmData.Length / bytesPerSample;
            int frameSamples = Math.Max(1, sampleRate / 50);
            int frameCount = Math.Max(1, totalSamples / frameSamples);

            int firstVoice = -1;
            int lastVoice = -1;

            for (int f = 0; f < frameCount; f++)
            {
                int start = f * frameSamples;
                int end = Math.Min(totalSamples, start + frameSamples);
                double sumSq = 0;
                int n = 0;
                for (int i = start; i < end; i++)
                {
                    int byteIndex = i * bytesPerSample;
                    short s = (short)(pcmData[byteIndex] | (pcmData[byteIndex + 1] << 8));
                    float v = s / 32768f;
                    sumSq += v * v;
                    n++;
                }
                float rms = n > 0 ? (float)Math.Sqrt(sumSq / n) : 0f;
                if (rms >= silenceRms)
                {
                    if (firstVoice < 0) firstVoice = f;
                    lastVoice = f;
                }
            }

            if (firstVoice < 0 || lastVoice < firstVoice)
                return pcmData;

            int padFrames = Math.Max(0, (int)(padSeconds * sampleRate / frameSamples));
            int left = Math.Max(0, firstVoice - padFrames);
            int right = Math.Min(frameCount - 1, lastVoice + padFrames);

            int startSample = left * frameSamples;
            int endSample = Math.Min(totalSamples, (right + 1) * frameSamples);
            int lenSamples = endSample - startSample;
            if (lenSamples <= 0 || lenSamples >= totalSamples)
                return pcmData;

            int startByte = startSample * bytesPerSample;
            int byteLen = lenSamples * bytesPerSample;
            var result = new byte[byteLen];
            Buffer.BlockCopy(pcmData, startByte, result, 0, byteLen);
            return result;
        }

        public static float DurationSeconds(byte[] pcmData, int sampleRate = 16000, int channels = 1)
        {
            if (pcmData == null || pcmData.Length == 0 || sampleRate <= 0) return 0f;
            int bytesPerSample = 2 * Math.Max(1, channels);
            return pcmData.Length / (float)(sampleRate * bytesPerSample);
        }
    }
}
