using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// 声纹识别插件设置
    /// </summary>
    public class VoiceprintSettings
    {
        #region 基本设置

        /// <summary>
        /// 是否启用语音输入
        /// </summary>
        public bool EnableVoiceInput { get; set; } = true;

        /// <summary>
        /// 是否启用声纹验证
        /// </summary>
        public bool EnableVoiceprintVerification { get; set; } = true;

        /// <summary>
        /// 是否要求声纹匹配（不匹配则拒绝输入）
        /// </summary>
        public bool RequireVoiceprintMatch { get; set; } = false;

        #endregion

        #region 声纹识别设置

        /// <summary>
        /// 声纹模型文件名
        /// </summary>
        public string VoiceprintModelFile { get; set; } = "voiceprint.onnx";

        /// <summary>
        /// 声纹验证阈值（余弦相似度，范围 -1 到 1）
        /// </summary>
        public float VoiceprintThreshold { get; set; } = 0.7f;

        /// <summary>
        /// 最小录音时长（秒）
        /// </summary>
        public float MinRecordingDuration { get; set; } = 1.0f;

        /// <summary>
        /// 最大录音时长（秒）
        /// </summary>
        public float MaxRecordingDuration { get; set; } = 10.0f;

        #endregion

        #region 语音转文字设置

        /// <summary>
        /// Whisper 模型文件名
        /// </summary>
        public string WhisperModelFile { get; set; } = "whisper-tiny.onnx";

        /// <summary>
        /// 语言设置
        /// </summary>
        public string Language { get; set; } = "zh";

        /// <summary>
        /// 最大解码长度
        /// </summary>
        public int MaxDecodingLength { get; set; } = 448;

        #endregion

        #region 音频设置

        /// <summary>
        /// 采样率
        /// </summary>
        public int SampleRate { get; set; } = 16000;

        /// <summary>
        /// 通道数
        /// </summary>
        public int Channels { get; set; } = 1;

        /// <summary>
        /// 位深度
        /// </summary>
        public int BitsPerSample { get; set; } = 16;

        /// <summary>
        /// 输入设备索引
        /// </summary>
        public int InputDeviceIndex { get; set; } = 0;

        /// <summary>
        /// 静音检测阈值
        /// </summary>
        public float SilenceThreshold { get; set; } = 0.01f;

        /// <summary>
        /// 静音超时（秒）- 超过此时间的静音自动停止录音
        /// </summary>
        public float SilenceTimeout { get; set; } = 2.0f;

        #endregion

        #region 性能设置

        /// <summary>
        /// 是否使用 GPU 加速
        /// </summary>
        public bool UseGPU { get; set; } = false;

        /// <summary>
        /// 推理线程数
        /// </summary>
        public int NumThreads { get; set; } = 4;

        #endregion

        #region 调试设置

        /// <summary>
        /// 是否启用调试模式
        /// </summary>
        public bool DebugMode { get; set; } = false;

        /// <summary>
        /// 是否保存录音文件（用于调试）
        /// </summary>
        public bool SaveRecordings { get; set; } = false;

        #endregion

        /// <summary>
        /// 从文件加载设置
        /// </summary>
        public static VoiceprintSettings LoadFromFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    var settings = JsonSerializer.Deserialize<VoiceprintSettings>(json);
                    return settings ?? new VoiceprintSettings();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[声纹识别] 加载设置失败: {ex.Message}");
            }

            return new VoiceprintSettings();
        }

        /// <summary>
        /// 保存设置到文件
        /// </summary>
        public void SaveToFile(string filePath)
        {
            try
            {
                string directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                string json = JsonSerializer.Serialize(this, options);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[声纹识别] 保存设置失败: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// 克隆设置
        /// </summary>
        public VoiceprintSettings Clone()
        {
            return new VoiceprintSettings
            {
                EnableVoiceInput = this.EnableVoiceInput,
                EnableVoiceprintVerification = this.EnableVoiceprintVerification,
                RequireVoiceprintMatch = this.RequireVoiceprintMatch,
                VoiceprintModelFile = this.VoiceprintModelFile,
                VoiceprintThreshold = this.VoiceprintThreshold,
                MinRecordingDuration = this.MinRecordingDuration,
                MaxRecordingDuration = this.MaxRecordingDuration,
                WhisperModelFile = this.WhisperModelFile,
                Language = this.Language,
                MaxDecodingLength = this.MaxDecodingLength,
                SampleRate = this.SampleRate,
                Channels = this.Channels,
                BitsPerSample = this.BitsPerSample,
                InputDeviceIndex = this.InputDeviceIndex,
                SilenceThreshold = this.SilenceThreshold,
                SilenceTimeout = this.SilenceTimeout,
                UseGPU = this.UseGPU,
                NumThreads = this.NumThreads,
                DebugMode = this.DebugMode,
                SaveRecordings = this.SaveRecordings
            };
        }

        /// <summary>
        /// 验证设置
        /// </summary>
        public void Validate()
        {
            // 确保阈值在有效范围内
            VoiceprintThreshold = Math.Clamp(VoiceprintThreshold, -1.0f, 1.0f);

            // 确保采样率有效
            if (SampleRate < 8000 || SampleRate > 48000)
                SampleRate = 16000;

            // 确保线程数有效
            if (NumThreads < 1)
                NumThreads = 1;
            if (NumThreads > Environment.ProcessorCount * 2)
                NumThreads = Environment.ProcessorCount;

            // 确保录音时长有效
            MinRecordingDuration = Math.Max(0.5f, MinRecordingDuration);
            MaxRecordingDuration = Math.Max(MinRecordingDuration + 1, MaxRecordingDuration);
        }
    }
}
