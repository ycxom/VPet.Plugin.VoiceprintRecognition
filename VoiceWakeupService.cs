using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// 声纹唤醒服务
    /// 持续监听音频流，通过 VAD 检测语音片段，
    /// 并行验证声纹 + ASR 转写检测唤醒词，通过后触发唤醒事件
    /// </summary>
    public class VoiceWakeupService
    {
        private enum WakeupState
        {
            Monitoring,       // 监听中，等待唤醒词
            AwaitingCommand   // 唤醒词已检测，等待指令
        }

        private readonly VoiceprintSettings _settings;
        private readonly VoiceprintRecognizer _recognizer;
        private readonly AudioCapture _audioCapture;
        private readonly SpeechToTextService _localAsr;
        private readonly ExternalAsrService _externalAsr;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logDebug;

        // 状态机
        private WakeupState _state = WakeupState.Monitoring;
        private VoiceprintVerificationResult _pendingResult;
        private Timer _commandTimer;

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

        // 唤醒词列表（从已注册声纹的 UserName 提取）
        private List<string> _wakeWords = new List<string>();

        /// <summary>
        /// 是否正在监听
        /// </summary>
        public bool IsMonitoring => _isMonitoring;

        /// <summary>
        /// 唤醒检测事件（文字结果 + 声纹验证结果）
        /// </summary>
        public event Action<string, VoiceprintVerificationResult> WakeupDetected;

        /// <summary>
        /// 进入等待指令模式事件
        /// </summary>
        public event Action DictationStarted;

        /// <summary>
        /// 指令超时/取消事件
        /// </summary>
        public event Action DictationEnded;

        public VoiceWakeupService(
            VoiceprintSettings settings,
            VoiceprintRecognizer recognizer,
            AudioCapture audioCapture,
            SpeechToTextService localAsr = null,
            ExternalAsrService externalAsr = null,
            Action<string> logInfo = null,
            Action<string> logDebug = null)
        {
            _settings = settings;
            _recognizer = recognizer;
            _audioCapture = audioCapture;
            _localAsr = localAsr;
            _externalAsr = externalAsr;
            _logInfo = logInfo ?? (s => Console.WriteLine($"[唤醒] {s}"));
            _logDebug = logDebug ?? (s => Console.WriteLine($"[唤醒][DEBUG] {s}"));
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

            // 检查是否有可用的 ASR 服务（本地 Whisper 或外部 API）
            bool hasLocalAsr = _localAsr != null && _localAsr.IsInitialized;
            bool hasExternalAsr = _externalAsr != null && !string.IsNullOrWhiteSpace(_settings.AsrApiUrl);
            if (!hasLocalAsr && !hasExternalAsr)
            {
                _logInfo("无法启动监听：没有可用的 ASR 服务（需要本地 Whisper 模型或外部 ASR API）");
                return;
            }
            _logInfo($"ASR 服务: {(hasLocalAsr ? "本地 Whisper" : "")}{(hasLocalAsr && hasExternalAsr ? " + " : "")}{(hasExternalAsr ? "外部 API" : "")}");

            // 刷新唤醒词列表
            RefreshWakeWords();
            if (_wakeWords.Count == 0)
            {
                _logInfo("无法启动监听：没有已注册的声纹（唤醒词为空）");
                return;
            }

            ResetVadState();
            _state = WakeupState.Monitoring;
            _audioCapture.AudioDataAvailable += OnAudioDataAvailable;
            _audioCapture.StartMonitoring();
            _isMonitoring = true;
            _logInfo($"唤醒监听已启动，唤醒词: {string.Join(", ", _wakeWords)}");
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
            _commandTimer?.Dispose();
            _commandTimer = null;
            _pendingResult = null;
            _state = WakeupState.Monitoring;
            ResetVadState();
            _logInfo("唤醒监听已停止");
        }

        /// <summary>
        /// 更新唤醒词列表
        /// </summary>
        public void UpdateKeywords()
        {
            RefreshWakeWords();
            _logInfo($"唤醒词已更新: {string.Join(", ", _wakeWords)}");
        }

        private void RefreshWakeWords()
        {
            _wakeWords = _recognizer.GetRegisteredVoiceprints()
                .Select(vp => vp.UserName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct()
                .ToList();
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
        /// 后台处理语音片段：根据当前状态执行不同逻辑
        /// </summary>
        private async Task ProcessSpeechAsync(byte[] audioData)
        {
            // 防止并发处理
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

                _logInfo($"检测到语音片段: {duration:F1}s, {audioData.Length} bytes (状态={_state})");

                if (_state == WakeupState.Monitoring)
                {
                    await ProcessMonitoringPhase(audioData, sw);
                }
                else if (_state == WakeupState.AwaitingCommand)
                {
                    await ProcessCommandPhase(audioData, sw);
                }
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
        /// 监听阶段：并行声纹验证 + ASR 转写，检测唤醒词
        /// </summary>
        private async Task ProcessMonitoringPhase(byte[] audioData, Stopwatch sw)
        {
            // 冷却期检查
            var timeSinceLastWakeup = (DateTime.Now - _lastWakeupTime).TotalSeconds;
            if (timeSinceLastWakeup < _settings.WakeupCooldown)
            {
                _logDebug($"冷却期中 (剩余 {_settings.WakeupCooldown - timeSinceLastWakeup:F1}s)，丢弃");
                return;
            }

            // 并行执行声纹验证和 ASR 转写
            var verifyTask = Task.Run(() => _recognizer.Verify(audioData, _settings.WakeupVoiceprintThreshold));
            var asrTask = TranscribeAudio(audioData);

            await Task.WhenAll(verifyTask, asrTask);

            var result = verifyTask.Result;
            var asrText = asrTask.Result ?? "";

            _logInfo($"声纹验证: {(result.IsVerified ? "通过" : "未通过")} (置信度: {result.Confidence:P1})");
            _logInfo($"ASR 识别: \"{asrText}\"");
            _logDebug($"并行处理耗时: {sw.ElapsedMilliseconds}ms");

            // 检查声纹
            if (!result.IsVerified)
            {
                _logDebug("声纹未通过，继续监听");
                return;
            }

            // 检查 ASR 文本是否包含唤醒词
            string matchedWakeWord = FindWakeWord(asrText);
            if (matchedWakeWord == null)
            {
                _logInfo($"ASR 文本不包含唤醒词，继续监听 (唤醒词: {string.Join(", ", _wakeWords)})");
                return;
            }

            _logInfo($"唤醒词匹配: \"{matchedWakeWord}\" (耗时: {sw.ElapsedMilliseconds}ms)");
            _lastWakeupTime = DateTime.Now;

            // 提取唤醒词之后的指令文本
            string commandText = ExtractCommandAfterWakeWord(asrText, matchedWakeWord);

            if (!string.IsNullOrWhiteSpace(commandText))
            {
                // 用户在同一句中说了唤醒词 + 指令
                _logInfo($"唤醒成功（含指令）: \"{commandText}\"");
                WakeupDetected?.Invoke(commandText, result);
            }
            else
            {
                // 用户只说了唤醒词，等待指令
                _logInfo("唤醒成功，等待指令...");
                _pendingResult = result;
                _state = WakeupState.AwaitingCommand;

                // 启动指令超时计时器
                _commandTimer?.Dispose();
                int timeoutMs = (int)(_settings.DictationTimeout * 1000);
                _commandTimer = new Timer(
                    _ => OnCommandTimeout(),
                    null,
                    timeoutMs,
                    Timeout.Infinite);

                DictationStarted?.Invoke();
            }
        }

        /// <summary>
        /// 指令阶段：ASR 转写指令文本
        /// </summary>
        private async Task ProcessCommandPhase(byte[] audioData, Stopwatch sw)
        {
            _commandTimer?.Dispose();
            _commandTimer = null;

            _logInfo("指令阶段: 正在 ASR 转写...");
            var text = await TranscribeAudio(audioData);
            _logInfo($"指令 ASR 结果: \"{text}\" (耗时: {sw.ElapsedMilliseconds}ms)");

            _state = WakeupState.Monitoring;

            if (!string.IsNullOrWhiteSpace(text))
            {
                WakeupDetected?.Invoke(text, _pendingResult);
            }
            else
            {
                _logInfo("指令为空，回到监听状态");
                DictationEnded?.Invoke();
            }

            _pendingResult = null;
        }

        /// <summary>
        /// 指令超时
        /// </summary>
        private void OnCommandTimeout()
        {
            _logInfo("指令等待超时，回到监听状态");
            _commandTimer?.Dispose();
            _commandTimer = null;
            _state = WakeupState.Monitoring;
            _pendingResult = null;
            DictationEnded?.Invoke();
        }

        /// <summary>
        /// 统一 ASR 转写：优先本地 Whisper，回退外部 API
        /// </summary>
        private async Task<string> TranscribeAudio(byte[] audioData)
        {
            // 优先使用本地 Whisper ONNX（离线，无需网络）
            if (_localAsr != null && _localAsr.IsInitialized)
            {
                try
                {
                    _logDebug("使用本地 Whisper 转写...");
                    var text = await _localAsr.TranscribeAsync(audioData);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                    _logDebug("本地 Whisper 返回空结果");
                }
                catch (Exception ex)
                {
                    _logInfo($"本地 Whisper 转写失败: {ex.Message}");
                }
            }

            // 回退到外部 ASR API
            if (_externalAsr != null && !string.IsNullOrWhiteSpace(_settings.AsrApiUrl))
            {
                try
                {
                    _logDebug("使用外部 ASR 转写...");
                    return await _externalAsr.TranscribeAsync(audioData);
                }
                catch (Exception ex)
                {
                    _logInfo($"外部 ASR 转写失败: {ex.Message}");
                }
            }

            _logDebug("无可用 ASR 服务");
            return null;
        }

        /// <summary>
        /// 在 ASR 文本中查找唤醒词
        /// </summary>
        private string FindWakeWord(string text)
        {
            if (string.IsNullOrEmpty(text))
                return null;

            foreach (var wakeWord in _wakeWords)
            {
                if (text.Contains(wakeWord))
                    return wakeWord;
            }
            return null;
        }

        /// <summary>
        /// 提取唤醒词之后的指令文本
        /// </summary>
        private string ExtractCommandAfterWakeWord(string text, string wakeWord)
        {
            int idx = text.IndexOf(wakeWord);
            if (idx < 0) return null;

            string after = text.Substring(idx + wakeWord.Length).Trim();

            // 去除常见的标点符号
            after = after.TrimStart('，', ',', '。', '.', '！', '!', '？', '?', ' ');

            return string.IsNullOrWhiteSpace(after) ? null : after;
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
