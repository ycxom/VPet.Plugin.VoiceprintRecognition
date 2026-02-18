using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;
using System.Threading;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// Windows 语音识别服务
    /// 使用 System.Speech.Recognition 实现关键词唤醒 + 听写模式
    /// 模仿 Cortana 的交互方式
    /// </summary>
    public class WindowsSpeechService : IDisposable
    {
        private enum ServiceState
        {
            Idle,
            ListeningKeyword,
            VerifyingVoiceprint,
            ListeningDictation
        }

        private readonly VoiceprintSettings _settings;
        private readonly VoiceprintRecognizer _recognizer;
        private readonly AudioCapture _audioCapture;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logDebug;

        private SpeechRecognitionEngine _speechEngine;
        private Grammar _keywordGrammar;
        private DictationGrammar _dictationGrammar;
        private System.Threading.Timer _dictationTimer;

        private ServiceState _state = ServiceState.Idle;
        private bool _disposed = false;
        private readonly object _stateLock = new object();

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
        /// 状态变更事件
        /// </summary>
        public event Action<string> StatusChanged;

        public WindowsSpeechService(
            VoiceprintSettings settings,
            VoiceprintRecognizer recognizer,
            AudioCapture audioCapture,
            Action<string> logInfo = null,
            Action<string> logDebug = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
            _audioCapture = audioCapture ?? throw new ArgumentNullException(nameof(audioCapture));
            _logInfo = logInfo ?? (_ => { });
            _logDebug = logDebug ?? (_ => { });
        }

        /// <summary>
        /// 启动语音识别（关键词监听模式）
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
                // 平台诊断
                _logInfo($"WindowsSpeech: 平台检测 - IsWindows={System.OperatingSystem.IsWindows()}, OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}, Framework={System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

                // 检查加载的 System.Speech.dll 是否为运行时实现
                var speechAssembly = typeof(SpeechRecognitionEngine).Assembly;
                _logInfo($"WindowsSpeech: System.Speech 路径={speechAssembly.Location}, 大小={new System.IO.FileInfo(speechAssembly.Location).Length} 字节");

                var culture = new CultureInfo(_settings.WindowsSpeechCulture);

                // 检查是否有对应语言的识别器
                var installedRecognizers = SpeechRecognitionEngine.InstalledRecognizers();
                var matchedRecognizer = installedRecognizers.FirstOrDefault(r => r.Culture.Name == culture.Name);

                if (matchedRecognizer == null)
                {
                    // 尝试匹配语言族
                    matchedRecognizer = installedRecognizers.FirstOrDefault(
                        r => r.Culture.TwoLetterISOLanguageName == culture.TwoLetterISOLanguageName);
                }

                if (matchedRecognizer == null)
                {
                    var available = string.Join(", ", installedRecognizers.Select(r => r.Culture.Name));
                    var msg = $"未找到 {culture.Name} 语音识别引擎。已安装的语言: {(string.IsNullOrEmpty(available) ? "无" : available)}";
                    _logInfo($"WindowsSpeech: {msg}");
                    throw new InvalidOperationException(msg);
                }

                _logInfo($"WindowsSpeech: 使用识别器 {matchedRecognizer.Culture.Name} ({matchedRecognizer.Description})");

                _speechEngine = new SpeechRecognitionEngine(matchedRecognizer);
                _speechEngine.SpeechRecognized += OnSpeechRecognized;
                _speechEngine.SpeechRecognitionRejected += OnSpeechRecognitionRejected;
                _speechEngine.RecognizeCompleted += OnRecognizeCompleted;

                // 使用默认音频设备
                _speechEngine.SetInputToDefaultAudioDevice();

                // 创建听写语法（备用）
                _dictationGrammar = new DictationGrammar();

                // 构建并加载关键词语法
                if (!BuildAndLoadKeywordGrammar())
                {
                    _speechEngine.Dispose();
                    _speechEngine = null;
                    throw new InvalidOperationException("没有已注册的声纹，无法构建关键词语法");
                }

                // 启动 AudioCapture 监听模式（用于声纹验证的环形缓冲区）
                if (!_audioCapture.IsMonitoring && !_audioCapture.IsRecording)
                {
                    _audioCapture.StartMonitoring();
                    _logDebug("WindowsSpeech: AudioCapture 监听已启动（环形缓冲区）");
                }

                // 开始异步识别（持续模式）
                _speechEngine.RecognizeAsync(RecognizeMode.Multiple);

                lock (_stateLock)
                    _state = ServiceState.ListeningKeyword;

                _logInfo("WindowsSpeech: 关键词监听已启动");
                StatusChanged?.Invoke("关键词监听中...");
            }
            catch (Exception ex)
            {
                _logInfo($"WindowsSpeech: 启动失败 - [{ex.GetType().Name}] {ex.Message}");
                _logInfo($"WindowsSpeech: 堆栈 - {ex.StackTrace}");
                if (ex.InnerException != null)
                    _logInfo($"WindowsSpeech: 内部异常 - [{ex.InnerException.GetType().Name}] {ex.InnerException.Message}");
                Cleanup();
                throw;
            }
        }

        /// <summary>
        /// 停止语音识别
        /// </summary>
        public void Stop()
        {
            lock (_stateLock)
            {
                if (_state == ServiceState.Idle)
                    return;
            }

            _logInfo("WindowsSpeech: 停止监听");
            Cleanup();
            StatusChanged?.Invoke("已停止");
        }

        /// <summary>
        /// 更新关键词（声纹注册后调用）
        /// </summary>
        public void UpdateKeywords()
        {
            lock (_stateLock)
            {
                if (_state == ServiceState.Idle || _speechEngine == null)
                    return;
            }

            try
            {
                // 需要先停止再重建
                _speechEngine.RecognizeAsyncCancel();

                // 卸载旧语法
                _speechEngine.UnloadAllGrammars();

                if (BuildAndLoadKeywordGrammar())
                {
                    _speechEngine.RecognizeAsync(RecognizeMode.Multiple);
                    lock (_stateLock)
                        _state = ServiceState.ListeningKeyword;
                    _logInfo("WindowsSpeech: 关键词已更新");
                }
                else
                {
                    _logInfo("WindowsSpeech: 无关键词可用，停止监听");
                    Cleanup();
                }
            }
            catch (Exception ex)
            {
                _logInfo($"WindowsSpeech: 更新关键词失败 - {ex.Message}");
            }
        }

        /// <summary>
        /// 构建并加载关键词语法
        /// </summary>
        private bool BuildAndLoadKeywordGrammar()
        {
            var voiceprints = _recognizer.GetRegisteredVoiceprints();
            if (voiceprints.Count == 0)
            {
                _logInfo("WindowsSpeech: 没有已注册的声纹");
                return false;
            }

            var keywords = voiceprints
                .Select(vp => vp.UserName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct()
                .ToList();

            if (keywords.Count == 0)
            {
                _logInfo("WindowsSpeech: 没有有效的唤醒关键词");
                return false;
            }

            var choices = new Choices(keywords.ToArray());
            var grammarBuilder = new GrammarBuilder(choices);
            grammarBuilder.Culture = _speechEngine.RecognizerInfo.Culture;

            _keywordGrammar = new Grammar(grammarBuilder) { Name = "WakeKeywords" };
            _speechEngine.LoadGrammar(_keywordGrammar);

            _logInfo($"WindowsSpeech: 已加载 {keywords.Count} 个关键词: {string.Join(", ", keywords)}");
            return true;
        }

        /// <summary>
        /// 语音识别成功回调
        /// </summary>
        private void OnSpeechRecognized(object sender, SpeechRecognizedEventArgs e)
        {
            try
            {
                ServiceState currentState;
                lock (_stateLock)
                    currentState = _state;

                if (currentState == ServiceState.ListeningKeyword)
                {
                    HandleKeywordRecognized(e);
                }
                else if (currentState == ServiceState.ListeningDictation)
                {
                    HandleDictationRecognized(e);
                }
            }
            catch (Exception ex)
            {
                _logInfo($"WindowsSpeech: 识别回调异常 - {ex.Message}");
                SwitchToKeywordMode();
            }
        }

        /// <summary>
        /// 处理关键词识别
        /// </summary>
        private void HandleKeywordRecognized(SpeechRecognizedEventArgs e)
        {
            float confidence = e.Result.Confidence;
            string keyword = e.Result.Text;

            _logDebug($"WindowsSpeech: 关键词候选 \"{keyword}\" 置信度 {confidence:P0}");

            if (confidence < _settings.WindowsSpeechConfidence)
            {
                _logDebug($"WindowsSpeech: 置信度低于阈值 {_settings.WindowsSpeechConfidence:P0}，忽略");
                return;
            }

            _logInfo($"WindowsSpeech: 关键词命中 \"{keyword}\" 置信度 {confidence:P0}");

            lock (_stateLock)
                _state = ServiceState.VerifyingVoiceprint;
            StatusChanged?.Invoke("声纹验证中...");

            // 从 AudioCapture 环形缓冲区取音频进行声纹验证
            var audioData = _audioCapture.GetRecentAudio(3.0f);

            if (audioData == null || audioData.Length < 16000 * 2)
            {
                _logInfo("WindowsSpeech: 环形缓冲区音频不足，跳过声纹验证，直接进入听写");
                SwitchToDictationMode(null);
                return;
            }

            var verifyResult = _recognizer.Verify(audioData);
            _logInfo($"WindowsSpeech: 声纹验证 - 通过={verifyResult.IsVerified}, 用户={verifyResult.MatchedUserId}, 置信度={verifyResult.Confidence:P1}");

            if (verifyResult.IsVerified)
            {
                SwitchToDictationMode(verifyResult);
            }
            else
            {
                _logInfo("WindowsSpeech: 声纹未通过，继续关键词监听");
                lock (_stateLock)
                    _state = ServiceState.ListeningKeyword;
                StatusChanged?.Invoke("关键词监听中...");
            }
        }

        /// <summary>
        /// 切换到听写模式
        /// </summary>
        private void SwitchToDictationMode(VoiceprintVerificationResult verifyResult)
        {
            try
            {
                _speechEngine.RecognizeAsyncCancel();
                _speechEngine.UnloadAllGrammars();
                _speechEngine.LoadGrammar(_dictationGrammar);

                lock (_stateLock)
                    _state = ServiceState.ListeningDictation;

                // 启动超时计时器
                int timeoutMs = (int)(_settings.DictationTimeout * 1000);
                _dictationTimer?.Dispose();
                _dictationTimer = new System.Threading.Timer(
                    _ => OnDictationTimeout(),
                    null,
                    timeoutMs,
                    Timeout.Infinite);

                // 用闭包保存 verifyResult，在 tag 中传递
                _speechEngine.RecognizeAsync(RecognizeMode.Single);

                _logInfo("WindowsSpeech: 已切换到听写模式，请说出指令...");
                StatusChanged?.Invoke("听写中，请说话...");

                // 将 verifyResult 存到临时字段
                _lastVerifyResult = verifyResult;
            }
            catch (Exception ex)
            {
                _logInfo($"WindowsSpeech: 切换听写模式失败 - {ex.Message}");
                SwitchToKeywordMode();
            }
        }

        private VoiceprintVerificationResult _lastVerifyResult;

        /// <summary>
        /// 处理听写结果
        /// </summary>
        private void HandleDictationRecognized(SpeechRecognizedEventArgs e)
        {
            _dictationTimer?.Dispose();
            _dictationTimer = null;

            string text = e.Result.Text;
            float confidence = e.Result.Confidence;

            _logInfo($"WindowsSpeech: 听写结果 \"{text}\" 置信度 {confidence:P0}");

            if (!string.IsNullOrWhiteSpace(text))
            {
                WakeupTextReceived?.Invoke(text, _lastVerifyResult);
            }

            _lastVerifyResult = null;
            SwitchToKeywordMode();
        }

        /// <summary>
        /// 听写超时回调
        /// </summary>
        private void OnDictationTimeout()
        {
            _logInfo("WindowsSpeech: 听写超时");
            _dictationTimer?.Dispose();
            _dictationTimer = null;
            _lastVerifyResult = null;
            SwitchToKeywordMode();
        }

        /// <summary>
        /// 切换回关键词监听模式
        /// </summary>
        private void SwitchToKeywordMode()
        {
            try
            {
                if (_speechEngine == null) return;

                _speechEngine.RecognizeAsyncCancel();
                _speechEngine.UnloadAllGrammars();

                if (BuildAndLoadKeywordGrammar())
                {
                    _speechEngine.RecognizeAsync(RecognizeMode.Multiple);
                    lock (_stateLock)
                        _state = ServiceState.ListeningKeyword;
                    _logDebug("WindowsSpeech: 已切回关键词监听模式");
                    StatusChanged?.Invoke("关键词监听中...");
                }
                else
                {
                    lock (_stateLock)
                        _state = ServiceState.Idle;
                    StatusChanged?.Invoke("无关键词");
                }
            }
            catch (Exception ex)
            {
                _logInfo($"WindowsSpeech: 切回关键词模式失败 - {ex.Message}");
                lock (_stateLock)
                    _state = ServiceState.Idle;
            }
        }

        /// <summary>
        /// 识别被拒绝（没有匹配任何语法）
        /// </summary>
        private void OnSpeechRecognitionRejected(object sender, SpeechRecognitionRejectedEventArgs e)
        {
            ServiceState currentState;
            lock (_stateLock)
                currentState = _state;

            if (currentState == ServiceState.ListeningDictation)
            {
                _logDebug("WindowsSpeech: 听写未识别到内容");
            }
        }

        /// <summary>
        /// 识别完成（Single 模式下触发）
        /// </summary>
        private void OnRecognizeCompleted(object sender, RecognizeCompletedEventArgs e)
        {
            ServiceState currentState;
            lock (_stateLock)
                currentState = _state;

            // Single 模式结束后切回关键词
            if (currentState == ServiceState.ListeningDictation)
            {
                _dictationTimer?.Dispose();
                _dictationTimer = null;
                _lastVerifyResult = null;
                SwitchToKeywordMode();
            }
        }

        /// <summary>
        /// 获取已安装的语音识别语言列表
        /// </summary>
        public static List<string> GetInstalledCultures()
        {
            return SpeechRecognitionEngine.InstalledRecognizers()
                .Select(r => r.Culture.Name)
                .ToList();
        }

        private void Cleanup()
        {
            try
            {
                _dictationTimer?.Dispose();
                _dictationTimer = null;
                _lastVerifyResult = null;

                if (_speechEngine != null)
                {
                    try
                    {
                        _speechEngine.RecognizeAsyncCancel();
                    }
                    catch { }

                    _speechEngine.SpeechRecognized -= OnSpeechRecognized;
                    _speechEngine.SpeechRecognitionRejected -= OnSpeechRecognitionRejected;
                    _speechEngine.RecognizeCompleted -= OnRecognizeCompleted;
                    _speechEngine.Dispose();
                    _speechEngine = null;
                }

                _keywordGrammar = null;
                _dictationGrammar = null;
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
