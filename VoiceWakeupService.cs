using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// Assistant-style wake service (mobile AI pipeline):
    /// always-on light KWS -> voiceprint gate -> command VAD -> on-demand ASR
    /// </summary>
    public class VoiceWakeupService
    {
        private enum WakeupState
        {
            Monitoring,
            PrimaryCandidate,
            VerifyingSpeaker,
            AwaitingCommand,
            Transcribing,
            Cooldown
        }

        private readonly VoiceprintSettings _settings;
        private readonly VoiceprintRecognizer _recognizer;
        private readonly AudioCapture _audioCapture;
        private readonly SpeechToTextService _localAsr;
        private readonly ExternalAsrService _externalAsr;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logDebug;
        private readonly string _modelsPath;
        private readonly WakeWordDetector _wakeWordDetector;

        private WakeupState _state = WakeupState.Monitoring;
        private VoiceprintVerificationResult _pendingResult;
        private Timer _commandTimer;

        private bool _isSpeaking;
        private readonly List<byte> _speechBuffer = new List<byte>();
        private int _silenceChunks;
        private int _speechChunks;
        private readonly Stopwatch _recordingStopwatch = new Stopwatch();

        private DateTime _lastWakeupTime = DateTime.MinValue;
        private bool _isMonitoring;
        private volatile bool _isProcessing;

        private List<string> _wakeWords = new List<string>();

        private OpenWakeWordEngine _oww;
        private int _owwSoftHits;
        private int _owwHardHits;
        private string _owwCandidateName;
        private float _owwCandidateScore;
        private readonly List<byte> _commandPreroll = new List<byte>();

        public bool IsMonitoring => _isMonitoring;

        public event Action<string, VoiceprintVerificationResult> WakeupDetected;
        public event Action DictationStarted;
        public event Action DictationEnded;
        public event Action<string> StatusChanged;

        public VoiceWakeupService(
            VoiceprintSettings settings,
            VoiceprintRecognizer recognizer,
            AudioCapture audioCapture,
            SpeechToTextService localAsr = null,
            ExternalAsrService externalAsr = null,
            Action<string> logInfo = null,
            Action<string> logDebug = null,
            string modelsPath = null)
        {
            _settings = settings;
            _recognizer = recognizer;
            _audioCapture = audioCapture;
            _localAsr = localAsr;
            _externalAsr = externalAsr;
            _logInfo = logInfo ?? (s => Console.WriteLine("[wake] " + s));
            _logDebug = logDebug ?? (s => Console.WriteLine("[wake][DEBUG] " + s));
            _modelsPath = modelsPath ?? "";
            _wakeWordDetector = new WakeWordDetector(_logDebug);
        }

        public void StartMonitoring()
        {
            if (_isMonitoring) return;

            if (_recognizer == null)
            {
                _logInfo("cannot start: recognizer null");
                return;
            }
            if (_audioCapture == null)
            {
                _logInfo("cannot start: audio capture null");
                return;
            }

            bool hasLocalAsr = _localAsr != null && _localAsr.IsInitialized;
            bool hasExternalAsr = _externalAsr != null && !string.IsNullOrWhiteSpace(_settings.AsrApiUrl);
            if (!hasLocalAsr && !hasExternalAsr)
            {
                _logInfo("cannot start: need Whisper or external ASR for commands");
                return;
            }

            RefreshWakeWords();
            EnsureOpenWakeWord();

            // OWW mode can start without registered names; custom mode needs wake words
            if (!_settings.UseOpenWakeWord || _oww == null || !_oww.IsInitialized)
            {
                if (_wakeWords.Count == 0)
                {
                    _logInfo("cannot start: no registered wake words (register voiceprint first)");
                    return;
                }
            }

            ResetVadState();
            ResetKwsCandidate();
            _state = WakeupState.Monitoring;
            _audioCapture.AudioDataAvailable += OnAudioDataAvailable;
            _audioCapture.StartMonitoring();
            _isMonitoring = true;
            EmitStatus("监听中");
            string mode = (_oww != null && _oww.IsInitialized && _settings.UseOpenWakeWord)
                ? "openWakeWord+voiceprint+ASR"
                : "VAD+DTW/ASR+voiceprint";
            _logInfo($"wake monitoring started [{mode}], words: {string.Join(", ", _wakeWords)}");
        }

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
            ResetKwsCandidate();
            _oww?.Reset();
            EmitStatus("已停止");
            _logInfo("wake monitoring stopped");
        }

        public void UpdateKeywords()
        {
            RefreshWakeWords();
            _logInfo("wake words updated: " + string.Join(", ", _wakeWords));
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

        private void EmitStatus(string status)
        {
            try { StatusChanged?.Invoke(status); } catch { }
        }

        private void ResetKwsCandidate()
        {
            _owwSoftHits = 0;
            _owwHardHits = 0;
            _owwCandidateName = null;
            _owwCandidateScore = 0;
            _commandPreroll.Clear();
        }

        private void OnAudioDataAvailable(object sender, byte[] audioChunk)
        {
            try
            {
                // Stage0/1: always-on KWS when OWW enabled
                if (_settings.UseOpenWakeWord && _oww != null && _oww.IsInitialized
                    && (_state == WakeupState.Monitoring || _state == WakeupState.PrimaryCandidate))
                {
                    ProcessOpenWakeWordFrame(audioChunk);
                    return;
                }

                // Command listening uses VAD in AwaitingCommand
                // Custom (non-OWW) wake also uses VAD in Monitoring
                if (_state != WakeupState.Monitoring && _state != WakeupState.AwaitingCommand)
                    return;

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
                        _logDebug($"VAD speech start RMS={rms:F4}");
                    }
                }
                else
                {
                    _speechBuffer.AddRange(audioChunk);
                    _speechChunks++;
                    if (isVoice) _silenceChunks = 0;
                    else _silenceChunks++;

                    float elapsed = (float)_recordingStopwatch.Elapsed.TotalSeconds;
                    if (elapsed >= _settings.MaxRecordingDuration)
                    {
                        _logDebug("VAD max duration");
                        DispatchSpeechEnd();
                        return;
                    }

                    float silenceDuration = _silenceChunks * 0.1f;
                    if (silenceDuration >= _settings.SilenceTimeout)
                    {
                        _logDebug($"VAD silence end {silenceDuration:F1}s");
                        DispatchSpeechEnd();
                    }
                }
            }
            catch (Exception ex)
            {
                _logInfo("audio chunk error: " + ex.Message);
            }
        }

        private void DispatchSpeechEnd()
        {
            _isSpeaking = false;
            _recordingStopwatch.Stop();
            var audioData = _speechBuffer.ToArray();
            _speechBuffer.Clear();
            _silenceChunks = 0;
            _speechChunks = 0;
            Task.Run(() => ProcessSpeechAsync(audioData));
        }

        private async Task ProcessSpeechAsync(byte[] audioData)
        {
            if (_isProcessing)
            {
                _logDebug("busy, drop segment");
                return;
            }
            _isProcessing = true;
            var sw = Stopwatch.StartNew();
            try
            {
                int bytesPerSecond = _settings.SampleRate * _settings.Channels * (_settings.BitsPerSample / 8);
                float duration = audioData.Length / (float)bytesPerSecond;
                if (duration < _settings.MinRecordingDuration)
                {
                    _logDebug($"segment too short {duration:F1}s");
                    return;
                }

                _logInfo($"speech segment {duration:F1}s state={_state}");

                if (_state == WakeupState.Monitoring)
                    await ProcessMonitoringPhase(audioData, sw);
                else if (_state == WakeupState.AwaitingCommand)
                    await ProcessCommandPhase(audioData, sw);
            }
            catch (Exception ex)
            {
                _logInfo("process speech failed: " + ex.Message);
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private void ProcessOpenWakeWordFrame(byte[] audioChunk)
        {
            if (_isProcessing) return;
            if ((DateTime.Now - _lastWakeupTime).TotalSeconds < _settings.WakeupCooldown)
                return;

            if (audioChunk != null && audioChunk.Length > 0)
            {
                _commandPreroll.AddRange(audioChunk);
                int maxPreroll = (int)(_settings.SampleRate * 2 * Math.Max(0.2f, _settings.CommandPrerollSeconds + 0.5f));
                if (_commandPreroll.Count > maxPreroll)
                    _commandPreroll.RemoveRange(0, _commandPreroll.Count - maxPreroll);
            }

            float hard = _settings.OpenWakeWordThreshold;
            float soft = _settings.UseAssistantPipeline
                ? Math.Min(_settings.OpenWakeWordSoftThreshold, hard)
                : hard;
            int patience = _settings.UseAssistantPipeline ? Math.Max(1, _settings.OpenWakeWordPatienceFrames) : 1;

            bool any = _oww.TryDetect(audioChunk, soft, out var modelName, out var score);
            if (!any)
            {
                if (_state == WakeupState.PrimaryCandidate)
                {
                    _owwSoftHits = Math.Max(0, _owwSoftHits - 1);
                    _owwHardHits = 0;
                    if (_owwSoftHits == 0)
                    {
                        _state = WakeupState.Monitoring;
                        _owwCandidateName = null;
                        EmitStatus("监听中");
                    }
                }
                return;
            }

            _logDebug($"KWS {modelName} score={score:F3} soft={soft:F2} hard={hard:F2}");

            if (_state == WakeupState.Monitoring)
            {
                _state = WakeupState.PrimaryCandidate;
                _owwSoftHits = 1;
                _owwHardHits = score >= hard ? 1 : 0;
                _owwCandidateName = modelName;
                _owwCandidateScore = score;
                EmitStatus("检测到唤醒…");
                _logInfo($"KWS stage1 candidate: {modelName} {score:F3}");
            }
            else if (_state == WakeupState.PrimaryCandidate)
            {
                if (!string.Equals(modelName, _owwCandidateName, StringComparison.OrdinalIgnoreCase))
                {
                    _owwCandidateName = modelName;
                    _owwSoftHits = 1;
                    _owwHardHits = score >= hard ? 1 : 0;
                    _owwCandidateScore = score;
                }
                else
                {
                    _owwSoftHits++;
                    if (score >= hard) _owwHardHits++;
                    if (score > _owwCandidateScore) _owwCandidateScore = score;
                }
            }

            if (_owwHardHits >= patience || (!_settings.UseAssistantPipeline && score >= hard))
            {
                _ = Task.Run(() => ConfirmWakeAndListenAsync(_owwCandidateName, _owwCandidateScore));
            }
        }

        private async Task ConfirmWakeAndListenAsync(string modelName, float score)
        {
            if (_isProcessing) return;
            if (_state != WakeupState.PrimaryCandidate && _state != WakeupState.Monitoring)
                return;

            _isProcessing = true;
            _state = WakeupState.VerifyingSpeaker;
            EmitStatus("声纹确认中…");
            try
            {
                _logInfo($"KWS stage2 confirm: {modelName} score={score:F3}");

                var audio = _audioCapture.GetRecentAudio(3.0f);
                if (audio == null && _commandPreroll.Count > 0)
                    audio = _commandPreroll.ToArray();
                if (audio != null)
                {
                    audio = AudioProcessing.ExtractSpeechSegment(
                        audio, _settings.SampleRate, _settings.Channels,
                        targetSeconds: 1.6f, minSeconds: 0.5f, maxSeconds: 2.8f);
                }

                VoiceprintVerificationResult result;
                bool needVp = _settings.EnableVoiceprintVerification
                              && _recognizer.GetRegisteredVoiceprints().Count > 0;

                if (needVp && audio != null && audio.Length >= _settings.SampleRate)
                {
                    result = await Task.Run(() => _recognizer.Verify(audio, _settings.WakeupVoiceprintThreshold));
                    _logInfo($"speaker gate ok={result.IsVerified} cos={result.Similarity:F3} thr={_settings.WakeupVoiceprintThreshold:F3} user={result.MatchedUserId}");
                    if (!result.IsVerified)
                    {
                        EmitStatus("声纹未通过");
                        _state = WakeupState.Cooldown;
                        await Task.Delay(400);
                        ResetKwsCandidate();
                        _state = WakeupState.Monitoring;
                        EmitStatus("监听中");
                        return;
                    }
                }
                else
                {
                    result = new VoiceprintVerificationResult
                    {
                        IsVerified = true,
                        Confidence = score,
                        Similarity = score,
                        MatchedUserId = modelName
                    };
                }

                EnterCommandListening(result);
            }
            catch (Exception ex)
            {
                _logInfo("confirm failed: " + ex.Message);
                ResetKwsCandidate();
                _state = WakeupState.Monitoring;
                EmitStatus("监听中");
            }
            finally
            {
                _isProcessing = false;
            }
        }

        private void EnterCommandListening(VoiceprintVerificationResult result)
        {
            _lastWakeupTime = DateTime.Now;
            _pendingResult = result;
            ResetVadState();

            if (_commandPreroll.Count > 0)
            {
                int keep = (int)(_settings.SampleRate * 2 * Math.Max(0.15f, _settings.CommandPrerollSeconds));
                var seed = _commandPreroll.Skip(Math.Max(0, _commandPreroll.Count - keep)).ToArray();
                _speechBuffer.Clear();
                _speechBuffer.AddRange(seed);
                _isSpeaking = true;
                _speechChunks = 1;
                _silenceChunks = 0;
                _recordingStopwatch.Restart();
            }

            _state = WakeupState.AwaitingCommand;
            _commandTimer?.Dispose();
            int timeoutMs = (int)(_settings.DictationTimeout * 1000);
            _commandTimer = new Timer(_ => OnCommandTimeout(), null, timeoutMs, Timeout.Infinite);
            EmitStatus("请说指令…");
            DictationStarted?.Invoke();
            _logInfo("pipeline: awaiting command");
        }

        private async Task ProcessMonitoringPhase(byte[] audioData, Stopwatch sw)
        {
            // custom path (non-OWW): voiceprint + DTW/ASR text wake word
            if ((DateTime.Now - _lastWakeupTime).TotalSeconds < _settings.WakeupCooldown)
            {
                _logDebug("cooldown drop");
                return;
            }

            var speech = AudioProcessing.ExtractSpeechSegment(
                audioData, _settings.SampleRate, _settings.Channels,
                targetSeconds: 1.8f, minSeconds: 0.5f, maxSeconds: 3.0f);

            var result = await Task.Run(() => _recognizer.Verify(speech, _settings.WakeupVoiceprintThreshold));
            _logInfo($"voiceprint {(result.IsVerified ? "ok" : "fail")} cos={result.Similarity:F3}");
            if (!result.IsVerified) return;

            bool wakeMatched = false;
            string matchedWakeWord = null;
            float dtwScore = 0f;

            var matchedVp = _recognizer.GetRegisteredVoiceprints()
                .FirstOrDefault(v => v.UserId == result.MatchedUserId);
            var envelopes = matchedVp?.WakeWordEnvelopes;
            if (envelopes != null && envelopes.Count > 0)
            {
                dtwScore = await Task.Run(() => _wakeWordDetector.Match(speech, envelopes, _settings.SampleRate));
                _logInfo($"DTW {dtwScore:F3} thr={_settings.WakeWordThreshold:F3}");
                if (dtwScore >= _settings.WakeWordThreshold)
                {
                    wakeMatched = true;
                    matchedWakeWord = matchedVp.UserName;
                }
            }

            string asrText = "";
            if (!wakeMatched)
            {
                asrText = await TranscribeAudio(speech) ?? "";
                _logInfo($"ASR \"{asrText}\"");
                matchedWakeWord = FindWakeWord(asrText);
                if (matchedWakeWord != null)
                {
                    wakeMatched = true;
                    _logInfo("ASR wake word: " + matchedWakeWord);
                }
            }

            if (!wakeMatched)
            {
                _logInfo($"no wake word (DTW={dtwScore:F3})");
                return;
            }

            string commandText = null;
            if (!string.IsNullOrWhiteSpace(asrText) && matchedWakeWord != null)
                commandText = ExtractCommandAfterWakeWord(asrText, matchedWakeWord);

            if (!string.IsNullOrWhiteSpace(commandText))
            {
                _lastWakeupTime = DateTime.Now;
                _logInfo("wake+command: " + commandText);
                WakeupDetected?.Invoke(commandText, result);
            }
            else
            {
                // seed preroll from this segment tail
                _commandPreroll.Clear();
                _commandPreroll.AddRange(speech);
                EnterCommandListening(result);
            }
        }

        private async Task ProcessCommandPhase(byte[] audioData, Stopwatch sw)
        {
            _commandTimer?.Dispose();
            _commandTimer = null;

            _state = WakeupState.Transcribing;
            EmitStatus("识别中…");
            _logInfo("command ASR...");

            var cmdAudio = AudioProcessing.TrimSilence(audioData, _settings.SampleRate, _settings.Channels);
            var text = await TranscribeAudio(cmdAudio);
            _logInfo($"command ASR \"{text}\" {sw.ElapsedMilliseconds}ms");

            ResetKwsCandidate();
            _state = WakeupState.Cooldown;
            EmitStatus("冷却中");

            if (!string.IsNullOrWhiteSpace(text))
                WakeupDetected?.Invoke(text, _pendingResult);
            else
            {
                _logInfo("empty command");
                DictationEnded?.Invoke();
            }

            _pendingResult = null;
            _ = Task.Run(async () =>
            {
                await Task.Delay(Math.Max(200, (int)(_settings.WakeupCooldown * 500)));
                if (_state == WakeupState.Cooldown)
                {
                    _state = WakeupState.Monitoring;
                    EmitStatus("监听中");
                }
            });
        }

        private void OnCommandTimeout()
        {
            _logInfo("command timeout");
            _commandTimer?.Dispose();
            _commandTimer = null;
            ResetKwsCandidate();
            _state = WakeupState.Monitoring;
            _pendingResult = null;
            EmitStatus("监听中");
            DictationEnded?.Invoke();
        }

        private async Task<string> TranscribeAudio(byte[] audioData)
        {
            if (_localAsr != null && _localAsr.IsInitialized)
            {
                try
                {
                    var text = await _localAsr.TranscribeAsync(audioData);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                catch (Exception ex)
                {
                    _logInfo("local ASR fail: " + ex.Message);
                }
            }

            if (_externalAsr != null && !string.IsNullOrWhiteSpace(_settings.AsrApiUrl))
            {
                try
                {
                    return await _externalAsr.TranscribeAsync(audioData);
                }
                catch (Exception ex)
                {
                    _logInfo("external ASR fail: " + ex.Message);
                }
            }
            return null;
        }

        private void EnsureOpenWakeWord()
        {
            if (!_settings.UseOpenWakeWord)
            {
                _oww?.Dispose();
                _oww = null;
                return;
            }

            string dir = _settings.OpenWakeWordModelDir ?? "openwakeword";
            if (!Path.IsPathRooted(dir) && !string.IsNullOrEmpty(_modelsPath))
                dir = Path.Combine(_modelsPath, dir);

            if (_oww != null && _oww.IsInitialized)
                return;

            _oww?.Dispose();
            _oww = new OpenWakeWordEngine(_logInfo, _logDebug);

            string[] files = null;
            if (!string.IsNullOrWhiteSpace(_settings.OpenWakeWordModelFile))
                files = new[] { _settings.OpenWakeWordModelFile };

            if (!_oww.Initialize(dir, files))
            {
                _logInfo("openWakeWord init failed; fallback custom VAD path");
                _oww.Dispose();
                _oww = null;
            }
        }

        private string FindWakeWord(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            foreach (var wakeWord in _wakeWords)
            {
                if (text.Contains(wakeWord))
                    return wakeWord;
            }
            return null;
        }

        private string ExtractCommandAfterWakeWord(string text, string wakeWord)
        {
            int idx = text.IndexOf(wakeWord);
            if (idx < 0) return null;
            string after = text.Substring(idx + wakeWord.Length).Trim();
            after = after.TrimStart(',', '.', '!', '?', ' ', '，', '。', '！', '？');
            return string.IsNullOrWhiteSpace(after) ? null : after;
        }

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
