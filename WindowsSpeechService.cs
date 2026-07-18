using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// Windows 语音识别服务
    /// 参考 Cortana 唤醒方式：关键词语法检测唤醒词
    /// 唤醒后通过 AudioCapture VAD 录制指令音频，送 ASR 转写
    /// </summary>
    public class WindowsSpeechService : IDisposable
    {
        private enum ServiceState
        {
            Idle,
            Monitoring,         // 关键词语法监听，等待唤醒词
            AwaitingCommand     // 唤醒词已检测，VAD 录制指令中
        }

        private readonly VoiceprintSettings _settings;
        private readonly VoiceprintRecognizer _recognizer;
        private readonly AudioCapture _audioCapture;
        private readonly SpeechToTextService _localAsr;
        private readonly ExternalAsrService _externalAsr;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logDebug;

        private SpeechRecognitionEngine _speechEngine;
        private Grammar _keywordGrammar;
        private Timer _commandTimer;

        private ServiceState _state = ServiceState.Idle;
        private bool _disposed = false;
        private readonly object _stateLock = new object();

        // 唤醒词列表
        private List<string> _wakeWords = new List<string>();

        // 声纹验证结果
        private VoiceprintVerificationResult _lastVerifyResult;

        // 唤醒词防抖
        private DateTime _lastWakeupTime = DateTime.MinValue;
        private const int WAKEUP_COOLDOWN_MS = 3000;

        // 指令录制 VAD 状态
        private readonly List<byte> _commandBuffer = new List<byte>();
        private bool _commandSpeechStarted = false;
        private int _commandSilenceChunks = 0;
        private const float COMMAND_SILENCE_TIMEOUT = 2.0f; // 指令后静音超时
        private const float COMMAND_MIN_DURATION = 0.5f;    // 最短指令时长

        /// <summary>
        /// 是否正在监听
        /// </summary>
        public bool IsListening
        {
            get
            {
                lock (_stateLock)
                    return _state != ServiceState.Idle;
            }
        }

        /// <summary>
        /// 唤醒后文字结果事件
        /// </summary>
        public event Action<string, VoiceprintVerificationResult> WakeupTextReceived;

        /// <summary>
        /// 唤醒后音频就绪事件（携带指令音频，由 Plugin 送 ASR）
        /// </summary>
        public event Action<byte[], VoiceprintVerificationResult> WakeupAudioReady;

        /// <summary>
        /// 进入等待指令模式事件
        /// </summary>
        public event Action DictationStarted;

        /// <summary>
        /// 流式部分结果
        /// </summary>
        public event Action<string> DictationPartialResult;

        /// <summary>
        /// 指令超时/取消事件
        /// </summary>
        public event Action DictationEnded;

        /// <summary>
        /// 状态变更事件
        /// </summary>
        public event Action<string> StatusChanged;

        public WindowsSpeechService(
            VoiceprintSettings settings,
            VoiceprintRecognizer recognizer,
            AudioCapture audioCapture,
            SpeechToTextService localAsr = null,
            ExternalAsrService externalAsr = null,
            Action<string> logInfo = null,
            Action<string> logDebug = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
            _audioCapture = audioCapture ?? throw new ArgumentNullException(nameof(audioCapture));
            _localAsr = localAsr;
            _externalAsr = externalAsr;
            _logInfo = logInfo ?? (_ => { });
            _logDebug = logDebug ?? (_ => { });
        }

        /// <summary>
        /// 启动语音识别（关键词语法模式）
        /// </summary>
        public void Start()
        {
            lock (_stateLock)
            {
                if (_state != ServiceState.Idle)
                {
                    _logDebug("WindowsSpeech: 已在运行中");
                    return;
                }
            }

            try
            {
                var culture = new CultureInfo(_settings.WindowsSpeechCulture);

                var installedRecognizers = SpeechRecognitionEngine.InstalledRecognizers();
                var matchedRecognizer = installedRecognizers.FirstOrDefault(r => r.Culture.Name == culture.Name)
                    ?? installedRecognizers.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName);

                if (matchedRecognizer == null)
                {
                    var available = string.Join(", ", installedRecognizers.Select(r => r.Culture.Name));
                    throw new InvalidOperationException($"未找到 {culture.Name} 语音识别引擎。已安装: {(string.IsNullOrEmpty(available) ? "无" : available)}");
                }

                _logInfo($"WindowsSpeech: 使用识别器 {matchedRecognizer.Culture.Name} ({matchedRecognizer.Description})");

                RefreshWakeWords();
                if (_wakeWords.Count == 0)
                    throw new InvalidOperationException("没有已注册的声纹，无法构建唤醒词");

                _speechEngine = new SpeechRecognitionEngine(matchedRecognizer);
                _speechEngine.SpeechRecognized += OnSpeechRecognized;
                _speechEngine.SpeechHypothesized += OnSpeechHypothesized;
                _speechEngine.SpeechRecognitionRejected += OnSpeechRecognitionRejected;
                _speechEngine.RecognizeCompleted += OnRecognizeCompleted;

                _speechEngine.SetInputToDefaultAudioDevice();

                // 构建关键词语法
                LoadKeywordGrammar();

                // 低拒绝阈值让 SpeechRecognized 能触发，置信度过滤在回调中处理
                try
                {
                    _speechEngine.UpdateRecognizerSetting("CFGConfidenceRejectionThreshold", 10);
                    _logDebug("WindowsSpeech: CFGConfidenceRejectionThreshold 设为 10");
                }
                catch { /* 部分引擎不支持此设置 */ }

                // 启动 AudioCapture 监听模式（环形缓冲区）
                if (!_audioCapture.IsMonitoring && !_audioCapture.IsRecording)
                {
                    _audioCapture.StartMonitoring();
                    _logDebug("WindowsSpeech: AudioCapture 监听已启动（环形缓冲区）");
                }

                _speechEngine.RecognizeAsync(RecognizeMode.Multiple);

                lock (_stateLock)
                    _state = ServiceState.Monitoring;

                _logInfo($"WindowsSpeech: 关键词监听已启动，唤醒词: {string.Join(", ", _wakeWords)}");
                StatusChanged?.Invoke("监听中...");
            }
            catch (Exception ex)
            {
                _logInfo($"WindowsSpeech: 启动失败 - [{ex.GetType().Name}] {ex.Message}");
                if (ex.InnerException != null)
                    _logInfo($"WindowsSpeech: 内部异常 - [{ex.InnerException.GetType().Name}] {ex.InnerException.Message}");
                Cleanup();
                throw;
            }
        }

        /// <summary>
        /// 构建并加载关键词语法
        /// </summary>
        private void LoadKeywordGrammar()
        {
            if (_keywordGrammar != null)
            {
                try { _speechEngine.UnloadGrammar(_keywordGrammar); } catch { }
                _keywordGrammar = null;
            }

            var choices = new Choices(_wakeWords.ToArray());
            var gb = new GrammarBuilder(choices);
            gb.Culture = new CultureInfo(_settings.WindowsSpeechCulture);

            _keywordGrammar = new Grammar(gb)
            {
                Name = "WakeWordGrammar"
            };

            _speechEngine.LoadGrammar(_keywordGrammar);
            _logDebug($"WindowsSpeech: 关键词语法已加载: {string.Join(", ", _wakeWords)}");
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                if (_state == ServiceState.Idle)
                    return;
            }

            _logInfo("WindowsSpeech: 停止监听");
            StopCommandRecording();
            Cleanup();
            StatusChanged?.Invoke("已停止");
        }

        public void UpdateKeywords()
        {
            RefreshWakeWords();

            // 如果引擎正在运行，热更新语法
            if (_speechEngine != null && _state == ServiceState.Monitoring)
            {
                try
                {
                    _speechEngine.RecognizeAsyncCancel();
                    Thread.Sleep(100);
                    LoadKeywordGrammar();
                    _speechEngine.RecognizeAsync(RecognizeMode.Multiple);
                }
                catch (Exception ex)
                {
                    _logInfo($"WindowsSpeech: 更新语法失败 - {ex.Message}");
                }
            }

            _logInfo($"WindowsSpeech: 唤醒词已更新: {string.Join(", ", _wakeWords)}");
        }

        private void RefreshWakeWords()
        {
            _wakeWords = _recognizer.GetRegisteredVoiceprints()
                .Select(vp => vp.UserName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct()
                .ToList();
        }

        // ─── 关键词检测回调 ────────────────────────────────────

        /// <summary>
        /// 假设回调 - 仅记录日志，不触发唤醒
        /// SpeechHypothesized 是推测性的，会把任何语音强行匹配到语法中，不可靠
        /// </summary>
        private void OnSpeechHypothesized(object sender, SpeechHypothesizedEventArgs e)
        {
            string text = e.Result?.Text;
            if (string.IsNullOrEmpty(text))
                return;

            _logDebug($"WindowsSpeech: [假设] \"{text}\" (仅日志，不触发)");
        }

        /// <summary>
        /// 识别完成回调 - 唯一的唤醒触发入口
        /// SpeechHypothesized 已禁用触发，仅此回调有可靠置信度
        /// </summary>
        private void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            string text = e.Result?.Text;
            float confidence = e.Result?.Confidence ?? 0;

            ServiceState currentState;
            lock (_stateLock)
                currentState = _state;

            if (currentState != ServiceState.Monitoring)
                return;

            _logInfo($"WindowsSpeech: [识别] \"{text}\" 置信度={confidence:P0}");

            // 置信度必须达标
            if (confidence < _settings.WindowsSpeechConfidence)
            {
                _logDebug($"WindowsSpeech: 置信度不足 ({confidence:P0} < {_settings.WindowsSpeechConfidence:P0})，忽略");
                return;
            }

            // 检查是否为唤醒词
            string matchedWakeWord = null;
            foreach (var wakeWord in _wakeWords)
            {
                if (text != null && text.Contains(wakeWord))
                {
                    matchedWakeWord = wakeWord;
                    break;
                }
            }

            if (matchedWakeWord != null)
            {
                // 防抖
                var now = DateTime.UtcNow;
                if ((now - _lastWakeupTime).TotalMilliseconds < WAKEUP_COOLDOWN_MS)
                {
                    _logDebug("WindowsSpeech: 唤醒词防抖，忽略");
                    return;
                }
                _lastWakeupTime = now;

                HandleWakeWordDetected(matchedWakeWord, text, $"识别(置信度={confidence:P0})");
            }
        }

        /// <summary>
        /// 处理唤醒词检测
        /// </summary>
        private void HandleWakeWordDetected(string wakeWord, string fullText, string source)
        {
            _logInfo($"WindowsSpeech: [{source}] 检测到唤醒词 \"{wakeWord}\"，原文: \"{fullText}\"");

            // 声纹验证（取最近 5 秒音频，确保包含完整唤醒词语音）
            var audioData = _audioCapture.GetRecentAudio(5.0f);
            VoiceprintVerificationResult verifyResult = null;

            if (audioData != null && audioData.Length >= 16000 * 2)
            {
                verifyResult = _recognizer.Verify(audioData, _settings.WakeupVoiceprintThreshold);
                _logInfo($"WindowsSpeech: 声纹验证 - 通过={verifyResult.IsVerified}, 用户={verifyResult.MatchedUserId}, 置信度={verifyResult.Confidence:P1}");

                if (!verifyResult.IsVerified)
                {
                    _logInfo("WindowsSpeech: 声纹未通过，继续监听");
                    return;
                }
            }
            else
            {
                _logInfo("WindowsSpeech: 音频不足，跳过声纹验证");
            }

            // 暂停关键词识别引擎，避免干扰指令录制
            try { _speechEngine?.RecognizeAsyncCancel(); } catch { }

            // 进入指令录制状态
            _logInfo("WindowsSpeech: 进入指令录制状态...");
            _lastVerifyResult = verifyResult;

            lock (_stateLock)
                _state = ServiceState.AwaitingCommand;

            StartCommandRecording();

            StatusChanged?.Invoke("已唤醒，请说指令...");
            DictationStarted?.Invoke();
        }

        // ─── 指令录制（VAD 方式）────────────────────────────────

        /// <summary>
        /// 开始指令录制：订阅 AudioCapture 数据流做 VAD
        /// </summary>
        private void StartCommandRecording()
        {
            _commandBuffer.Clear();
            _commandSpeechStarted = false;
            _commandSilenceChunks = 0;

            // 订阅音频数据事件做 VAD
            _audioCapture.AudioDataAvailable += OnCommandAudioData;

            // 总超时保护
            _commandTimer?.Dispose();
            int timeoutMs = (int)(_settings.DictationTimeout * 1000);
            _commandTimer = new Timer(
                _ => OnCommandTimeout(),
                null,
                timeoutMs,
                Timeout.Infinite);
        }

        /// <summary>
        /// 停止指令录制
        /// </summary>
        private void StopCommandRecording()
        {
            _audioCapture.AudioDataAvailable -= OnCommandAudioData;
            _commandTimer?.Dispose();
            _commandTimer = null;
        }

        /// <summary>
        /// 指令录制 VAD：检测语音开始和结束
        /// </summary>
        private void OnCommandAudioData(object sender, byte[] audioChunk)
        {
            ServiceState currentState;
            lock (_stateLock)
                currentState = _state;

            if (currentState != ServiceState.AwaitingCommand)
                return;

            float rms = ComputeRms(audioChunk);
            bool isVoice = rms > _settings.SilenceThreshold;

            _commandBuffer.AddRange(audioChunk);

            if (!_commandSpeechStarted)
            {
                if (isVoice)
                {
                    _commandSpeechStarted = true;
                    _commandSilenceChunks = 0;
                    _logDebug($"WindowsSpeech: 指令语音开始 (RMS={rms:F4})");
                }
            }
            else
            {
                if (isVoice)
                    _commandSilenceChunks = 0;
                else
                    _commandSilenceChunks++;

                float silenceDuration = _commandSilenceChunks * 0.1f;
                if (silenceDuration >= COMMAND_SILENCE_TIMEOUT)
                {
                    _logDebug($"WindowsSpeech: 指令语音结束 (静音 {silenceDuration:F1}s)");
                    HandleCommandEnd();
                }
            }
        }

        /// <summary>
        /// 指令录制完成 - 提取音频送 ASR
        /// </summary>
        private void HandleCommandEnd()
        {
            StopCommandRecording();

            var commandAudio = _commandBuffer.ToArray();
            _commandBuffer.Clear();

            int bytesPerSecond = _audioCapture.SampleRate * (_audioCapture.BitsPerSample / 8) * _audioCapture.Channels;
            float duration = commandAudio.Length / (float)bytesPerSecond;

            _logInfo($"WindowsSpeech: 指令录制完成, {duration:F1}s, {commandAudio.Length} bytes");

            // 回到监听状态
            lock (_stateLock)
                _state = ServiceState.Monitoring;
            StatusChanged?.Invoke("监听中...");

            // 重启关键词识别
            RestartKeywordRecognition();

            if (duration >= COMMAND_MIN_DURATION && commandAudio.Length > 0)
            {
                // 送 ASR 转写
                _logInfo("WindowsSpeech: 送 ASR 转写指令音频...");
                Task.Run(() => TranscribeCommandAsync(commandAudio));
            }
            else
            {
                _logInfo("WindowsSpeech: 指令音频太短，忽略");
                DictationEnded?.Invoke();
                _lastVerifyResult = null;
            }
        }

        /// <summary>
        /// 指令超时
        /// </summary>
        private void OnCommandTimeout()
        {
            _logInfo("WindowsSpeech: 指令等待超时");

            StopCommandRecording();

            var commandAudio = _commandBuffer.ToArray();
            _commandBuffer.Clear();

            lock (_stateLock)
                _state = ServiceState.Monitoring;
            StatusChanged?.Invoke("监听中...");

            RestartKeywordRecognition();

            int bytesPerSecond = _audioCapture.SampleRate * (_audioCapture.BitsPerSample / 8) * _audioCapture.Channels;
            float duration = commandAudio.Length / (float)bytesPerSecond;

            if (_commandSpeechStarted && duration >= COMMAND_MIN_DURATION)
            {
                _logInfo($"WindowsSpeech: 超时但有音频 ({duration:F1}s)，送 ASR");
                Task.Run(() => TranscribeCommandAsync(commandAudio));
            }
            else
            {
                DictationEnded?.Invoke();
                _lastVerifyResult = null;
            }
        }

        /// <summary>
        /// 异步转写指令音频
        /// </summary>
        private async Task TranscribeCommandAsync(byte[] audioData)
        {
            try
            {
                string text = null;

                // 优先本地 Whisper
                if (_localAsr != null && _localAsr.IsInitialized)
                {
                    _logDebug("WindowsSpeech: 使用本地 Whisper 转写指令...");
                    text = await _localAsr.TranscribeAsync(audioData);
                }

                // 回退外部 ASR
                if (string.IsNullOrWhiteSpace(text) && _externalAsr != null && !string.IsNullOrWhiteSpace(_settings.AsrApiUrl))
                {
                    _logDebug("WindowsSpeech: 使用外部 ASR 转写指令...");
                    text = await _externalAsr.TranscribeAsync(audioData);
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    _logInfo($"WindowsSpeech: 指令转写结果: \"{text}\"");
                    WakeupTextReceived?.Invoke(text, _lastVerifyResult);
                }
                else
                {
                    _logInfo("WindowsSpeech: 指令转写为空");
                    DictationEnded?.Invoke();
                }
            }
            catch (Exception ex)
            {
                _logInfo($"WindowsSpeech: 指令转写失败 - {ex.Message}");
                DictationEnded?.Invoke();
            }
            finally
            {
                _lastVerifyResult = null;
            }
        }

        /// <summary>
        /// 重启关键词识别引擎
        /// </summary>
        private void RestartKeywordRecognition()
        {
            try
            {
                if (_speechEngine != null)
                {
                    _speechEngine.RecognizeAsync(RecognizeMode.Multiple);
                    _logDebug("WindowsSpeech: 关键词识别已重启");
                }
            }
            catch (Exception ex)
            {
                _logInfo($"WindowsSpeech: 重启关键词识别失败 - {ex.Message}");
            }
        }

        // ─── 辅助 ──────────────────────────────────────────────

        private void OnSpeechRecognitionRejected(object sender, SpeechRecognitionRejectedEventArgs e)
        {
            _logDebug($"WindowsSpeech: [拒绝] 候选=\"{e.Result?.Text}\" 置信度={e.Result?.Confidence:P0}");
        }

        private void OnRecognizeCompleted(object sender, RecognizeCompletedEventArgs e)
        {
            if (e.Cancelled)
            {
                _logDebug("WindowsSpeech: RecognizeCompleted (Cancelled)");
                return;
            }

            _logDebug($"WindowsSpeech: RecognizeCompleted - Result={e.Result?.Text ?? "(null)"}");

            if (e.Error != null)
                _logInfo($"WindowsSpeech: 识别引擎错误 - {e.Error.Message}");
        }

        public static List<string> GetInstalledCultures()
        {
            return SpeechRecognitionEngine.InstalledRecognizers()
                .Select(r => r.Culture.Name)
                .ToList();
        }

        /// <summary>
        /// 计算音频块 RMS 能量
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

        private void Cleanup()
        {
            try
            {
                StopCommandRecording();
                _lastVerifyResult = null;

                if (_speechEngine != null)
                {
                    try { _speechEngine.RecognizeAsyncCancel(); } catch { }

                    _speechEngine.SpeechRecognized -= OnSpeechRecognized;
                    _speechEngine.SpeechHypothesized -= OnSpeechHypothesized;
                    _speechEngine.SpeechRecognitionRejected -= OnSpeechRecognitionRejected;
                    _speechEngine.RecognizeCompleted -= OnRecognizeCompleted;
                    _speechEngine.Dispose();
                    _speechEngine = null;
                }

                _keywordGrammar = null;
            }
            catch (Exception ex)
            {
                _logDebug($"WindowsSpeech: 清理时异常 - {ex.Message}");
            }

            lock (_stateLock)
                _state = ServiceState.Idle;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                Cleanup();
                _disposed = true;
            }
        }
    }
}
