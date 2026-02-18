using System;
using System.IO;
using System.Linq;
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

        #region 唤醒监听设置

        /// <summary>
        /// 是否启用声纹唤醒监听
        /// </summary>
        public bool EnableWakeup { get; set; } = false;

        /// <summary>
        /// 唤醒后自动发送文字（否则填入输入框等待手动确认）
        /// </summary>
        public bool WakeupAutoSend { get; set; } = false;

        /// <summary>
        /// 唤醒冷却时间（秒）
        /// </summary>
        public float WakeupCooldown { get; set; } = 2.0f;

        /// <summary>
        /// 唤醒词能量包络匹配阈值 (0~1)
        /// </summary>
        public float WakeWordThreshold { get; set; } = 0.55f;

        /// <summary>
        /// 是否使用 Windows 语音识别模式（否则使用自定义 Mel DTW + 外部 ASR）
        /// </summary>
        public bool UseWindowsSpeech { get; set; } = false;

        /// <summary>
        /// Windows 语音识别关键词最低置信度 (0~1)
        /// </summary>
        public float WindowsSpeechConfidence { get; set; } = 0.7f;

        /// <summary>
        /// Windows 语音识别听写模式超时（秒）
        /// </summary>
        public float DictationTimeout { get; set; } = 10.0f;

        /// <summary>
        /// Windows 语音识别文化/语言
        /// </summary>
        public string WindowsSpeechCulture { get; set; } = "zh-CN";

        #endregion

        #region 外部 ASR 设置

        /// <summary>
        /// 外部 ASR API URL
        /// </summary>
        public string AsrApiUrl { get; set; } = "";

        /// <summary>
        /// 外部 ASR API Key
        /// </summary>
        public string AsrApiKey { get; set; } = "";

        /// <summary>
        /// ASR 请求格式: multipart, base64json, rawbinary
        /// </summary>
        public string AsrRequestFormat { get; set; } = "multipart";

        /// <summary>
        /// ASR 响应文本 JSON 路径（如 "result" 或 "data.text"）
        /// </summary>
        public string AsrResponseTextPath { get; set; } = "result";

        /// <summary>
        /// ASR 语言
        /// </summary>
        public string AsrLanguage { get; set; } = "zh";

        /// <summary>
        /// ASR 请求超时（秒）
        /// </summary>
        public int AsrTimeout { get; set; } = 30;

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
                EnableWakeup = this.EnableWakeup,
                WakeupAutoSend = this.WakeupAutoSend,
                WakeupCooldown = this.WakeupCooldown,
                WakeWordThreshold = this.WakeWordThreshold,
                UseWindowsSpeech = this.UseWindowsSpeech,
                WindowsSpeechConfidence = this.WindowsSpeechConfidence,
                DictationTimeout = this.DictationTimeout,
                WindowsSpeechCulture = this.WindowsSpeechCulture,
                AsrApiUrl = this.AsrApiUrl,
                AsrApiKey = this.AsrApiKey,
                AsrRequestFormat = this.AsrRequestFormat,
                AsrResponseTextPath = this.AsrResponseTextPath,
                AsrLanguage = this.AsrLanguage,
                AsrTimeout = this.AsrTimeout,
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

            // 唤醒冷却时间
            WakeupCooldown = Math.Clamp(WakeupCooldown, 0.5f, 10.0f);

            // 唤醒词匹配阈值
            WakeWordThreshold = Math.Clamp(WakeWordThreshold, 0.1f, 0.95f);

            // Windows 语音识别设置
            WindowsSpeechConfidence = Math.Clamp(WindowsSpeechConfidence, 0.3f, 0.95f);
            DictationTimeout = Math.Clamp(DictationTimeout, 3.0f, 30.0f);
            var validCultures = new[] { "zh-CN", "en-US", "ja-JP" };
            if (!validCultures.Contains(WindowsSpeechCulture))
                WindowsSpeechCulture = "zh-CN";

            // ASR 超时
            AsrTimeout = Math.Clamp(AsrTimeout, 5, 120);

            // ASR 请求格式
            var validFormats = new[] { "multipart", "base64json", "rawbinary" };
            if (!validFormats.Contains(AsrRequestFormat))
                AsrRequestFormat = "multipart";
        }
    }
}
