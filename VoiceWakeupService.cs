using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// 声纹唤醒服务
    /// 持续监听音频流，通过 VAD 检测语音片段，
    /// 并行验证声纹 + 能量包络匹配唤醒词，都通过后触发唤醒事件
    /// </summary>
    public class VoiceWakeupService
    {
        private readonly VoiceprintSettings _settings;
        private readonly VoiceprintRecognizer _recognizer;
        private readonly WakeWordDetector _detector;
        private readonly AudioCapture _audioCapture;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logDebug;

        // VAD 状态
        private bool _isSpeaking = false;
        private readonly List<byte> _speechBuffer = new List<byte>();
        private int _silenceChunks = 0;
        private int _speechChunks = 0;
        private readonly Stopwatch _recordingStopwatch = new Stopwatch();

        // 冷却计时
        private DateTime _lastWakeupTime = DateTime.MinValue;

        // 监听状态
        private bool _isMonitoring = false;

        // 防止并发处理
        private volatile bool _isProcessing = false;

        /// <summary>
        /// 是否正在监听
        /// </summary>
        public bool IsMonitoring => _isMonitoring;

        /// <summary>
        /// 唤醒检测事件
        /// </summary>
        public event Action<byte[], VoiceprintVerificationResult> WakeupDetected;

        public VoiceWakeupService(
            VoiceprintSettings settings,
            VoiceprintRecognizer recognizer,
            AudioCapture audioCapture,
            Action<string> logInfo = null,
            Action<string> logDebug = null)
        {
            _settings = settings;
            _recognizer = recognizer;
            _audioCapture = audioCapture;
            _logInfo = logInfo ?? (s => Console.WriteLine($"[唤醒] {s}"));
            _logDebug = logDebug ?? (s => Console.WriteLine($"[唤醒][DEBUG] {s}"));
            _detector = new WakeWordDetector(_logDebug);
        }

        /// <summary>
        /// 开始监听
        /// </summary>
        public void StartMonitoring()
        {
            if (_isMonitoring) return;

            if (_recognizer == null)
            {
                _logInfo("无法启动监听：声纹识别引擎未初始化");
                return;
            }

            if (_audioCapture == null)
            {
                _logInfo("无法启动监听：音频采集器未初始化");
                return;
            }

            // 检查是否有已注册的声纹及唤醒词包络
            var voiceprints = _recognizer.GetRegisteredVoiceprints();
            if (voiceprints.Count == 0)
            {
                _logInfo("无法启动监听：没有已注册的声纹");
                return;
            }

            bool hasEnvelopes = voiceprints.Any(v => v.WakeWordEnvelopes != null && v.WakeWordEnvelopes.Count > 0);
            if (!hasEnvelopes)
            {
                _logInfo("无法启动监听：已注册声纹缺少唤醒词特征模板（请重新注册声纹）");
                return;
            }

            ResetVadState();
            _audioCapture.AudioDataAvailable += OnAudioDataAvailable;
            _audioCapture.StartMonitoring();
            _isMonitoring = true;
            _logInfo("唤醒监听已启动");
        }

        /// <summary>
        /// 停止监听
        /// </summary>
        public void StopMonitoring()
        {
            if (!_isMonitoring) return;

            _audioCapture.AudioDataAvailable -= OnAudioDataAvailable;
            _audioCapture.StopMonitoring();
            _isMonitoring = false;
            ResetVadState();
            _logInfo("唤醒监听已停止");
        }

        private void ResetVadState()
        {
            _isSpeaking = false;
            _speechBuffer.Clear();
            _silenceChunks = 0;
            _speechChunks = 0;
            _recordingStopwatch.Reset();
        }

        /// <summary>
        /// 处理每个音频块（约 100ms）— 仅做轻量 VAD，不阻塞音频线程
        /// </summary>
        private void OnAudioDataAvailable(object sender, byte[] audioChunk)
        {
            try
            {
                float rms = ComputeRms(audioChunk);
                bool isVoice = rms > _settings.SilenceThreshold;

                if (!_isSpeaking)
                {
                    if (isVoice)
                    {
                        _isSpeaking = true;
                        _speechBuffer.Clear();
                        _speechBuffer.AddRange(audioChunk);
                        _silenceChunks = 0;
                        _speechChunks = 1;
                        _recordingStopwatch.Restart();
                        _logDebug($"VAD: 语音开始 (RMS={rms:F4})");
                    }
                }
                else
                {
                    _speechBuffer.AddRange(audioChunk);
                    _speechChunks++;

                    if (isVoice)
                        _silenceChunks = 0;
                    else
                        _silenceChunks++;

                    float elapsed = (float)_recordingStopwatch.Elapsed.TotalSeconds;
                    if (elapsed >= _settings.MaxRecordingDuration)
                    {
                        _logDebug($"VAD: 达到最长时长 ({elapsed:F1}s)，强制结束");
                        DispatchSpeechEnd();
                        return;
                    }

                    float silenceDuration = _silenceChunks * 0.1f;
                    if (silenceDuration >= _settings.SilenceTimeout)
                    {
                        _logDebug($"VAD: 静音超时 ({silenceDuration:F1}s)，语音结束");
                        DispatchSpeechEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                _logInfo($"处理音频块异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 提取音频数据后投到后台线程处理，不阻塞音频回调
        /// </summary>
        private void DispatchSpeechEnd()
        {
            _isSpeaking = false;
            _recordingStopwatch.Stop();

            var audioData = _speechBuffer.ToArray();
            _speechBuffer.Clear();
            _silenceChunks = 0;
            _speechChunks = 0;

            // 投到后台线程，立即释放音频回调线程
            Task.Run(() => ProcessSpeechAsync(audioData));
        }

        /// <summary>
        /// 后台处理语音片段：并行执行声纹验证和唤醒词能量包络匹配
        /// </summary>
        private async Task ProcessSpeechAsync(byte[] audioData)
        {
            // 防止并发处理（上一段还没处理完就来了新的）
            if (_isProcessing)
            {
                _logDebug("上一段语音仍在处理中，丢弃本段");
                return;
            }
            _isProcessing = true;

            var sw = Stopwatch.StartNew();

            try
            {
                int bytesPerSecond = _settings.SampleRate * _settings.Channels * (_settings.BitsPerSample / 8);
                float duration = audioData.Length / (float)bytesPerSecond;

                // 最短时长检查
                if (duration < _settings.MinRecordingDuration)
                {
                    _logDebug($"VAD: 片段太短 ({duration:F1}s < {_settings.MinRecordingDuration}s)，丢弃");
                    return;
                }

                // 冷却期检查
                var timeSinceLastWakeup = (DateTime.Now - _lastWakeupTime).TotalSeconds;
                if (timeSinceLastWakeup < _settings.WakeupCooldown)
                {
                    _logDebug($"冷却期中 (剩余 {_settings.WakeupCooldown - timeSinceLastWakeup:F1}s)，丢弃");
                    return;
                }

                _logInfo($"检测到语音片段: {duration:F1}s, {audioData.Length} bytes");

                // 收集所有已注册声纹的唤醒词包络
                var voiceprints = _recognizer.GetRegisteredVoiceprints();
                var allEnvelopes = voiceprints
                    .Where(v => v.WakeWordEnvelopes != null)
                    .SelectMany(v => v.WakeWordEnvelopes)
                    .ToList();

                // 并行执行声纹验证和唤醒词能量包络匹配
                var verifyTask = Task.Run(() => _recognizer.Verify(audioData));
                var matchTask = Task.Run(() => _detector.Match(audioData, allEnvelopes, _settings.SampleRate));

                await Task.WhenAll(verifyTask, matchTask);

                var result = verifyTask.Result;
                float wakeWordSimilarity = matchTask.Result;

                _logInfo($"声纹验证: {(result.IsVerified ? "通过" : "未通过")} (置信度: {result.Confidence:P1})");
                _logInfo($"唤醒词匹配: {wakeWordSimilarity:F3} (阈值: {_settings.WakeWordThreshold})");
                _logDebug($"并行处理耗时: {sw.ElapsedMilliseconds}ms");

                // 检查声纹
                if (!result.IsVerified)
                    return;

                // 检查唤醒词频谱匹配
                if (wakeWordSimilarity < _settings.WakeWordThreshold)
                {
                    _logInfo($"唤醒词频谱匹配未通过 (相似度: {wakeWordSimilarity:F3} < 阈值: {_settings.WakeWordThreshold})");
                    return;
                }

                // 声纹 + 唤醒词均通过
                _lastWakeupTime = DateTime.Now;
                _logInfo($"唤醒成功！声纹通过 + 唤醒词匹配 (相似度: {wakeWordSimilarity:F3}, 总耗时: {sw.ElapsedMilliseconds}ms)");
                WakeupDetected?.Invoke(audioData, result);
            }
            catch (Exception ex)
            {
                _logInfo($"唤醒检测失败: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
            }
        }

        /// <summary>
        /// 计算音频块的 RMS 能量
        /// </summary>
        private static float ComputeRms(byte[] audioChunk)
        {
            int sampleCount = audioChunk.Length / 2;
            if (sampleCount == 0) return 0;

            double sumSquares = 0;
            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(audioChunk, i * 2);
                float normalized = sample / 32768.0f;
                sumSquares += normalized * normalized;
            }

            return (float)Math.Sqrt(sumSquares / sampleCount);
        }
    }
}
