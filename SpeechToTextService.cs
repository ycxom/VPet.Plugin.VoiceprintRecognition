using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// 语音转文字服务
    /// 使用 Whisper ONNX 模型进行本地语音识别
    /// </summary>
    public class SpeechToTextService : IDisposable
    {
        private InferenceSession _encoderSession;
        private InferenceSession _decoderSession;
        private readonly VoiceprintSettings _settings;
        private bool _disposed = false;

        public bool IsInitialized => _encoderSession != null;
        public string ModelName { get; private set; }

        // 词汇表: token_id -> text
        private Dictionary<int, string> _vocabulary;

        // Whisper 参数
        private const int SAMPLE_RATE = 16000;
        private const int N_FFT = 400;
        private const int HOP_LENGTH = 160;
        private const int N_MELS = 80;
        private const int N_FRAMES = 3000; // 30 秒
        private const int N_AUDIO_SAMPLES = SAMPLE_RATE * 30; // 480000

        // 特殊 token
        private const int SOT_TOKEN = 50258;
        private const int EOT_TOKEN = 50257;
        private const int TRANSCRIBE_TOKEN = 50359;
        private const int NOTIMESTAMPS_TOKEN = 50363;
        private const int ZH_TOKEN = 50260;
        private const int EN_TOKEN = 50259;
        private const int JA_TOKEN = 50266;
        private const int KO_TOKEN = 50264;

        // Mel 滤波器组
        private float[,] _melFilters;

        // 日志回调
        private Action<string> _logInfo;
        private Action<string> _logDebug;

        public SpeechToTextService(string modelPath, VoiceprintSettings settings,
            Action<string> logInfo = null, Action<string> logDebug = null)
        {
            _settings = settings;
            _logInfo = logInfo ?? (s => Console.WriteLine($"[语音转文字] {s}"));
            _logDebug = logDebug ?? (s => Console.WriteLine($"[语音转文字][DEBUG] {s}"));

            if (string.IsNullOrEmpty(modelPath))
            {
                _logInfo("模型路径为空");
                return;
            }

            var encoderPath = modelPath.Replace(".onnx", "_encoder.onnx");
            var decoderPath = modelPath.Replace(".onnx", "_decoder.onnx");

            if (File.Exists(modelPath) || (File.Exists(encoderPath) && File.Exists(decoderPath)))
            {
                LoadModel(modelPath);
            }
            else
            {
                _logInfo($"模型文件不存在: {modelPath}");
            }
        }

        private void LoadModel(string modelPath)
        {
            try
            {
                ModelName = Path.GetFileNameWithoutExtension(modelPath);

                var options = new SessionOptions();

                if (_settings.UseGPU)
                {
                    try { options.AppendExecutionProvider_CUDA(); }
                    catch
                    {
                        try { options.AppendExecutionProvider_DML(); }
                        catch { }
                    }
                }

                options.AppendExecutionProvider_CPU();
                options.InterOpNumThreads = _settings.NumThreads;
                options.IntraOpNumThreads = _settings.NumThreads;

                var encoderPath = modelPath.Replace(".onnx", "_encoder.onnx");
                var decoderPath = modelPath.Replace(".onnx", "_decoder.onnx");

                if (File.Exists(encoderPath) && File.Exists(decoderPath))
                {
                    _encoderSession = new InferenceSession(encoderPath, options);
                    _decoderSession = new InferenceSession(decoderPath, options);
                    _logInfo($"加载编码器: {Path.GetFileName(encoderPath)}");
                    _logInfo($"加载解码器: {Path.GetFileName(decoderPath)}");
                }
                else
                {
                    _encoderSession = new InferenceSession(modelPath, options);
                    _logInfo($"加载单一模型: {Path.GetFileName(modelPath)}");
                }

                // 打印模型元数据
                LogModelMetadata();

                // 初始化 Mel 滤波器
                _melFilters = CreateMelFilterbank(N_FFT, N_MELS, SAMPLE_RATE);
                _logDebug($"Mel 滤波器组: {N_MELS} x {N_FFT / 2 + 1}");

                // 加载词汇表
                InitializeVocabulary(Path.GetDirectoryName(modelPath));

                _logInfo($"模型加载成功: {ModelName}");
            }
            catch (Exception ex)
            {
                _logInfo($"加载模型失败: {ex.Message}");
                _logDebug($"详情: {ex}");
                _encoderSession = null;
                _decoderSession = null;
            }
        }

        private void LogModelMetadata()
        {
            if (_encoderSession != null)
            {
                _logDebug($"编码器输入: {string.Join(", ", _encoderSession.InputMetadata.Select(m => $"{m.Key}={string.Join("x", m.Value.Dimensions)}"))}");
                _logDebug($"编码器输出: {string.Join(", ", _encoderSession.OutputMetadata.Select(m => $"{m.Key}={string.Join("x", m.Value.Dimensions)}"))}");
            }
            if (_decoderSession != null)
            {
                _logDebug($"解码器输入: {string.Join(", ", _decoderSession.InputMetadata.Select(m => $"{m.Key}={string.Join("x", m.Value.Dimensions)}"))}");
                _logDebug($"解码器输出: {string.Join(", ", _decoderSession.OutputMetadata.Select(m => $"{m.Key}={string.Join("x", m.Value.Dimensions)}"))}");
            }
        }

        #region 词汇表

        private void InitializeVocabulary(string modelDir)
        {
            _vocabulary = new Dictionary<int, string>();

            // 优先加载 tokens.txt (sherpa-onnx 格式: base64_text token_id)
            var tokensPath = Path.Combine(modelDir, "tokens.txt");
            if (File.Exists(tokensPath))
            {
                LoadTokensTxt(tokensPath);
                return;
            }

            // 尝试 vocab.json
            var vocabPath = Path.Combine(modelDir, "vocab.json");
            if (File.Exists(vocabPath))
            {
                LoadVocabJson(vocabPath);
                return;
            }

            _logInfo("未找到词汇表文件 (tokens.txt 或 vocab.json)");
        }

        private void LoadTokensTxt(string path)
        {
            try
            {
                var lines = File.ReadAllLines(path);
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var lastSpace = line.LastIndexOf(' ');
                    if (lastSpace <= 0) continue;

                    var base64Part = line.Substring(0, lastSpace);
                    var idPart = line.Substring(lastSpace + 1);

                    if (!int.TryParse(idPart, out int tokenId)) continue;

                    try
                    {
                        var bytes = Convert.FromBase64String(base64Part);
                        var text = System.Text.Encoding.UTF8.GetString(bytes);
                        _vocabulary[tokenId] = text;
                    }
                    catch
                    {
                        // base64 解码失败，跳过
                    }
                }

                _logInfo($"加载词汇表: {_vocabulary.Count} 个 token (tokens.txt)");
                _logDebug($"词汇表范围: 0 ~ {_vocabulary.Keys.Max()}");
            }
            catch (Exception ex)
            {
                _logInfo($"加载 tokens.txt 失败: {ex.Message}");
            }
        }

        private void LoadVocabJson(string path)
        {
            try
            {
                var json = File.ReadAllText(path);
                var vocab = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                foreach (var kvp in vocab)
                    _vocabulary[kvp.Value] = kvp.Key;
                _logInfo($"加载词汇表: {_vocabulary.Count} 个 token (vocab.json)");
            }
            catch (Exception ex)
            {
                _logInfo($"加载 vocab.json 失败: {ex.Message}");
            }
        }

        #endregion

        #region Mel 频谱图

        /// <summary>
        /// 创建 Mel 滤波器组
        /// </summary>
        private static float[,] CreateMelFilterbank(int nFft, int nMels, int sampleRate)
        {
            int nFreqs = nFft / 2 + 1; // 201
            var filters = new float[nMels, nFreqs];

            float melMin = HzToMel(0);
            float melMax = HzToMel(sampleRate / 2.0f);

            // nMels + 2 个等间距的 mel 点
            var melPoints = new float[nMels + 2];
            for (int i = 0; i < nMels + 2; i++)
                melPoints[i] = melMin + (melMax - melMin) * i / (nMels + 1);

            // 转换回 Hz 并映射到 FFT bin
            var binPoints = new float[nMels + 2];
            for (int i = 0; i < nMels + 2; i++)
                binPoints[i] = MelToHz(melPoints[i]) * nFft / sampleRate;

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

                // 归一化 (slaney norm)
                float sum = 0;
                for (int k = 0; k < nFreqs; k++) sum += filters[m, k];
                if (sum > 0)
                    for (int k = 0; k < nFreqs; k++) filters[m, k] /= sum;
            }

            return filters;
        }

        private static float HzToMel(float hz) => 2595.0f * (float)Math.Log10(1 + hz / 700.0f);
        private static float MelToHz(float mel) => 700.0f * ((float)Math.Pow(10, mel / 2595.0f) - 1);

        /// <summary>
        /// 计算 Log Mel 频谱图
        /// </summary>
        private float[] ComputeLogMelSpectrogram(float[] audio)
        {
            // 补零到 30 秒
            var padded = new float[N_AUDIO_SAMPLES];
            int copyLen = Math.Min(audio.Length, N_AUDIO_SAMPLES);
            Array.Copy(audio, padded, copyLen);

            int nFreqs = N_FFT / 2 + 1; // 201
            var mel = new float[N_MELS * N_FRAMES];

            // Hann 窗
            var window = new float[N_FFT];
            for (int i = 0; i < N_FFT; i++)
                window[i] = 0.5f * (1 - (float)Math.Cos(2 * Math.PI * i / N_FFT));

            // STFT
            for (int frame = 0; frame < N_FRAMES; frame++)
            {
                int start = frame * HOP_LENGTH;

                // 加窗
                var windowed = new float[N_FFT];
                for (int i = 0; i < N_FFT; i++)
                {
                    int idx = start + i;
                    windowed[i] = (idx < padded.Length ? padded[idx] : 0) * window[i];
                }

                // DFT (计算幅度谱)
                var powerSpec = new float[nFreqs];
                for (int k = 0; k < nFreqs; k++)
                {
                    float real = 0, imag = 0;
                    for (int n = 0; n < N_FFT; n++)
                    {
                        float angle = -2.0f * (float)Math.PI * k * n / N_FFT;
                        real += windowed[n] * (float)Math.Cos(angle);
                        imag += windowed[n] * (float)Math.Sin(angle);
                    }
                    powerSpec[k] = real * real + imag * imag;
                }

                // 应用 Mel 滤波器
                for (int m = 0; m < N_MELS; m++)
                {
                    float val = 0;
                    for (int k = 0; k < nFreqs; k++)
                        val += _melFilters[m, k] * powerSpec[k];

                    // Log10, 最小值 1e-10
                    mel[m * N_FRAMES + frame] = (float)Math.Log10(Math.Max(val, 1e-10));
                }
            }

            // 归一化: (mel - max) / ln(10) * 4, 然后 clamp >= -1, +1 -> /2
            // Whisper 标准归一化
            float maxVal = float.MinValue;
            for (int i = 0; i < mel.Length; i++)
                maxVal = Math.Max(maxVal, mel[i]);

            for (int i = 0; i < mel.Length; i++)
            {
                mel[i] = Math.Max(mel[i], maxVal - 8.0f); // clamp
                mel[i] = (mel[i] + 4.0f) / 4.0f;         // normalize to ~[-1, 1]
            }

            return mel;
        }

        #endregion

        #region 推理

        public async Task<string> TranscribeAsync(byte[] audioData)
        {
            return await Task.Run(() => Transcribe(audioData));
        }

        public string Transcribe(byte[] audioData)
        {
            if (!IsInitialized)
            {
                _logInfo("模型未初始化");
                return "[请配置语音识别模型]";
            }

            try
            {
                // 1. PCM 转浮点
                int sampleCount = audioData.Length / 2;
                float[] samples = new float[sampleCount];
                for (int i = 0; i < sampleCount; i++)
                {
                    short s = BitConverter.ToInt16(audioData, i * 2);
                    samples[i] = s / 32768.0f;
                }
                _logDebug($"音频: {sampleCount} 采样, {sampleCount / (float)SAMPLE_RATE:F2} 秒");

                // 2. Mel 频谱图
                _logDebug("计算 Mel 频谱图...");
                var mel = ComputeLogMelSpectrogram(samples);
                _logDebug($"Mel 形状: [{N_MELS}, {N_FRAMES}], 值范围: [{mel.Min():F3}, {mel.Max():F3}]");

                // 3. 编码
                _logDebug("运行编码器...");
                var encoderResult = RunEncoder(mel);
                if (encoderResult == null)
                {
                    _logInfo("编码器输出为空");
                    return "";
                }

                // 4. 解码
                _logDebug("运行解码器...");
                var tokens = RunDecoder(encoderResult);
                _logDebug($"解码 token: [{string.Join(", ", tokens.Take(20))}{(tokens.Length > 20 ? "..." : "")}]");

                // 5. Token → 文本
                var text = TokensToText(tokens);
                _logDebug($"转换文本: \"{text}\"");

                return text;
            }
            catch (Exception ex)
            {
                _logInfo($"转写失败: {ex.Message}");
                _logDebug($"详情: {ex}");
                return "";
            }
        }

        /// <summary>
        /// 编码器输出 (cross_k 和 cross_v)
        /// </summary>
        private class EncoderResult
        {
            public DenseTensor<float> CrossK; // [4, 1, 1500, 384]
            public DenseTensor<float> CrossV; // [4, 1, 1500, 384]
        }

        private EncoderResult RunEncoder(float[] mel)
        {
            if (_encoderSession == null) return null;

            try
            {
                // 输入: mel [1, 80, 3000]
                var inputName = _encoderSession.InputMetadata.Keys.First();
                var shape = new int[] { 1, N_MELS, N_FRAMES };
                var tensor = new DenseTensor<float>(new ReadOnlySpan<int>(shape));

                for (int i = 0; i < mel.Length && i < tensor.Length; i++)
                    tensor.SetValue(i, mel[i]);

                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputName, tensor)
                };

                var result = new EncoderResult();

                using (var results = _encoderSession.Run(inputs))
                {
                    foreach (var output in results)
                    {
                        var outTensor = output.AsTensor<float>();
                        var dims = outTensor.Dimensions.ToArray();
                        _logDebug($"编码器输出: {output.Name} [{string.Join(", ", dims)}]");

                        // 复制数据到新 tensor (results dispose 后原 tensor 失效)
                        var copy = new DenseTensor<float>(new ReadOnlySpan<int>(dims));
                        var srcArray = outTensor.ToArray();
                        for (int i = 0; i < srcArray.Length; i++)
                            copy.SetValue(i, srcArray[i]);

                        if (output.Name.Contains("cross_k"))
                            result.CrossK = copy;
                        else if (output.Name.Contains("cross_v"))
                            result.CrossV = copy;
                    }
                }

                if (result.CrossK == null || result.CrossV == null)
                {
                    _logInfo("编码器未输出 cross_k/cross_v");
                    return null;
                }

                return result;
            }
            catch (Exception ex)
            {
                _logInfo($"编码失败: {ex.Message}");
                _logDebug($"详情: {ex}");
                return null;
            }
        }

        private int[] RunDecoder(EncoderResult encoderResult)
        {
            if (_decoderSession == null)
            {
                _logInfo("解码器未加载");
                return new int[0];
            }

            var resultTokens = new List<int>();

            try
            {
                // 初始 prompt tokens
                int langToken = GetLanguageToken();
                var promptTokens = new long[] { SOT_TOKEN, langToken, TRANSCRIBE_TOKEN, NOTIMESTAMPS_TOKEN };
                _logDebug($"初始 prompt: [{string.Join(", ", promptTokens)}]");

                // 获取解码器输入/输出名
                var inputNames = _decoderSession.InputMetadata.Keys.ToList();
                var outputNames = _decoderSession.OutputMetadata.Keys.ToList();
                _logDebug($"解码器输入名: {string.Join(", ", inputNames)}");
                _logDebug($"解码器输出名: {string.Join(", ", outputNames)}");

                // 初始化 self attention KV cache: [4, 1, 448, 384]
                // 从元数据获取维度
                var selfKMeta = _decoderSession.InputMetadata
                    .FirstOrDefault(m => m.Key.Contains("self_k_cache"));
                var selfKDims = selfKMeta.Value?.Dimensions;
                int nLayers = selfKDims != null ? selfKDims[0] : 4;
                int cacheMaxLen = selfKDims != null ? selfKDims[2] : 448;
                int modelDim = selfKDims != null ? selfKDims[3] : 384;

                var selfKCache = new DenseTensor<float>(
                    new ReadOnlySpan<int>(new int[] { nLayers, 1, cacheMaxLen, modelDim }));
                var selfVCache = new DenseTensor<float>(
                    new ReadOnlySpan<int>(new int[] { nLayers, 1, cacheMaxLen, modelDim }));

                _logDebug($"KV cache: [{nLayers}, 1, {cacheMaxLen}, {modelDim}]");

                // 自回归解码循环
                int maxSteps = Math.Min(_settings.MaxDecodingLength, 224);
                var currentTokens = new List<long>(promptTokens);
                int offset = 0;

                for (int step = 0; step < maxSteps; step++)
                {
                    var inputs = new List<NamedOnnxValue>();

                    // 1. tokens 输入
                    var tokensName = inputNames.FirstOrDefault(n => n.Contains("token")) ?? inputNames[0];
                    if (step == 0)
                    {
                        // 第一步: 传入所有 prompt tokens
                        var tokensTensor = new DenseTensor<long>(
                            new ReadOnlySpan<int>(new int[] { 1, currentTokens.Count }));
                        for (int i = 0; i < currentTokens.Count; i++)
                            tokensTensor[0, i] = currentTokens[i];
                        inputs.Add(NamedOnnxValue.CreateFromTensor(tokensName, tokensTensor));
                    }
                    else
                    {
                        // 后续步: 只传入最新 token
                        var tokensTensor = new DenseTensor<long>(
                            new ReadOnlySpan<int>(new int[] { 1, 1 }));
                        tokensTensor[0, 0] = currentTokens[currentTokens.Count - 1];
                        inputs.Add(NamedOnnxValue.CreateFromTensor(tokensName, tokensTensor));
                    }

                    // 2. self attention KV cache
                    foreach (var inputMeta in _decoderSession.InputMetadata)
                    {
                        var name = inputMeta.Key;
                        if (name == tokensName) continue;

                        if (name.Contains("self_k_cache"))
                            inputs.Add(NamedOnnxValue.CreateFromTensor(name, selfKCache));
                        else if (name.Contains("self_v_cache"))
                            inputs.Add(NamedOnnxValue.CreateFromTensor(name, selfVCache));
                        else if (name.Contains("cross_k"))
                            inputs.Add(NamedOnnxValue.CreateFromTensor(name, encoderResult.CrossK));
                        else if (name.Contains("cross_v"))
                            inputs.Add(NamedOnnxValue.CreateFromTensor(name, encoderResult.CrossV));
                        else if (name.Contains("offset"))
                        {
                            var offsetTensor = new DenseTensor<long>(
                                new ReadOnlySpan<int>(new int[] { 1 }));
                            offsetTensor[0] = offset;
                            inputs.Add(NamedOnnxValue.CreateFromTensor(name, offsetTensor));
                        }
                    }

                    // 运行解码器
                    using (var results = _decoderSession.Run(inputs))
                    {
                        // 更新 KV cache
                        foreach (var output in results)
                        {
                            if (output.Name.Contains("self_k_cache"))
                            {
                                var t = output.AsTensor<float>();
                                var arr = t.ToArray();
                                var dims = t.Dimensions.ToArray();
                                selfKCache = new DenseTensor<float>(new ReadOnlySpan<int>(dims));
                                for (int i = 0; i < arr.Length; i++)
                                    selfKCache.SetValue(i, arr[i]);
                            }
                            else if (output.Name.Contains("self_v_cache"))
                            {
                                var t = output.AsTensor<float>();
                                var arr = t.ToArray();
                                var dims = t.Dimensions.ToArray();
                                selfVCache = new DenseTensor<float>(new ReadOnlySpan<int>(dims));
                                for (int i = 0; i < arr.Length; i++)
                                    selfVCache.SetValue(i, arr[i]);
                            }
                        }

                        // 获取 logits
                        var logitsResult = results.FirstOrDefault(r => r.Name.Contains("logit"));
                        if (logitsResult == null) logitsResult = results.First();

                        var logits = logitsResult.AsTensor<float>();
                        var logitDims = logits.Dimensions.ToArray();

                        if (step == 0)
                            _logDebug($"Logits 维度: [{string.Join(", ", logitDims)}]");

                        // 取最后一个位置的 logits: [batch, seq_len, vocab_size]
                        int vocabSize = logitDims[logitDims.Length - 1];
                        int seqLen = logitDims.Length >= 2 ? logitDims[logitDims.Length - 2] : 1;
                        int lastPos = seqLen - 1;

                        // 贪婪搜索: argmax
                        int bestToken = 0;
                        float bestScore = float.MinValue;

                        for (int v = 0; v < vocabSize; v++)
                        {
                            float score;
                            if (logitDims.Length == 3)
                                score = logits[0, lastPos, v];
                            else if (logitDims.Length == 2)
                                score = logits[lastPos, v];
                            else
                                score = logits.GetValue(lastPos * vocabSize + v);

                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestToken = v;
                            }
                        }

                        if (step == 0)
                            _logDebug($"第一个预测 token: {bestToken}, score: {bestScore:F2}");

                        if (bestToken == EOT_TOKEN)
                        {
                            _logDebug($"解码结束 (EOT), 共 {step + 1} 步");
                            break;
                        }

                        currentTokens.Add(bestToken);
                        resultTokens.Add(bestToken);

                        // 更新 offset
                        if (step == 0)
                            offset = currentTokens.Count; // prompt 长度
                        else
                            offset = currentTokens.Count;
                    }
                }

                _logDebug($"总共解码 {resultTokens.Count} 个 token");
            }
            catch (Exception ex)
            {
                _logInfo($"解码失败: {ex.Message}");
                _logDebug($"详情: {ex}");
            }

            return resultTokens.ToArray();
        }

        private int GetLanguageToken()
        {
            return _settings.Language switch
            {
                "zh" => ZH_TOKEN,
                "en" => EN_TOKEN,
                "ja" => JA_TOKEN,
                "ko" => KO_TOKEN,
                _ => ZH_TOKEN
            };
        }

        private string TokensToText(int[] tokens)
        {
            var parts = new List<string>();

            foreach (var token in tokens)
            {
                // 跳过特殊 token (>= 50257)
                if (token >= SOT_TOKEN) continue;

                if (_vocabulary.TryGetValue(token, out var text))
                {
                    // Whisper BPE: Ġ (U+0120) 代表空格前缀
                    text = text.Replace('\u0120', ' ');
                    parts.Add(text);
                }
            }

            return string.Join("", parts).Trim();
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                _encoderSession?.Dispose();
                _decoderSession?.Dispose();
                _disposed = true;
            }
        }
    }
}
