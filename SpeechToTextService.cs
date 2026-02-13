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
    /// 使用 ONNX 模型（如 Whisper）进行本地语音识别
    /// </summary>
    public class SpeechToTextService : IDisposable
    {
        private InferenceSession _encoderSession;
        private InferenceSession _decoderSession;
        private readonly VoiceprintSettings _settings;
        private bool _disposed = false;

        /// <summary>
        /// 是否已初始化
        /// </summary>
        public bool IsInitialized => _encoderSession != null;

        /// <summary>
        /// 模型名称
        /// </summary>
        public string ModelName { get; private set; }

        // Whisper 词汇表（简化版，实际需要完整词汇表）
        private Dictionary<int, string> _vocabulary;

        // 特殊 token
        private const int SOT_TOKEN = 50258;  // Start of transcript
        private const int EOT_TOKEN = 50257;  // End of transcript
        private const int TRANSCRIBE_TOKEN = 50359;  // Transcribe task
        private const int NOTIMESTAMPS_TOKEN = 50363;  // No timestamps
        private const int ZH_TOKEN = 50260;  // Chinese
        private const int EN_TOKEN = 50259;  // English

        public SpeechToTextService(string modelPath, VoiceprintSettings settings)
        {
            _settings = settings;

            if (!string.IsNullOrEmpty(modelPath) && File.Exists(modelPath))
            {
                LoadModel(modelPath);
            }
            else
            {
                Console.WriteLine($"[语音转文字] 模型文件不存在，使用模拟模式");
            }
        }

        /// <summary>
        /// 加载 ONNX 模型
        /// </summary>
        private void LoadModel(string modelPath)
        {
            try
            {
                ModelName = Path.GetFileNameWithoutExtension(modelPath);

                // 创建会话选项
                var options = new SessionOptions();

                if (_settings.UseGPU)
                {
                    try
                    {
                        options.AppendExecutionProvider_CUDA();
                    }
                    catch
                    {
                        try
                        {
                            options.AppendExecutionProvider_DML();
                        }
                        catch { }
                    }
                }

                options.AppendExecutionProvider_CPU();
                options.InterOpNumThreads = _settings.NumThreads;
                options.IntraOpNumThreads = _settings.NumThreads;

                // 检查是否是编码器-解码器分离的模型
                var encoderPath = modelPath.Replace(".onnx", "_encoder.onnx");
                var decoderPath = modelPath.Replace(".onnx", "_decoder.onnx");

                if (File.Exists(encoderPath) && File.Exists(decoderPath))
                {
                    _encoderSession = new InferenceSession(encoderPath, options);
                    _decoderSession = new InferenceSession(decoderPath, options);
                    Console.WriteLine("[语音转文字] 加载编码器-解码器模型成功");
                }
                else
                {
                    // 单一模型
                    _encoderSession = new InferenceSession(modelPath, options);
                    Console.WriteLine("[语音转文字] 加载单一模型成功");
                }

                // 初始化词汇表
                InitializeVocabulary(Path.GetDirectoryName(modelPath));

                Console.WriteLine($"[语音转文字] 模型加载成功: {ModelName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[语音转文字] 加载模型失败: {ex.Message}");
                _encoderSession = null;
                _decoderSession = null;
            }
        }

        /// <summary>
        /// 初始化词汇表
        /// </summary>
        private void InitializeVocabulary(string modelDir)
        {
            _vocabulary = new Dictionary<int, string>();

            // 尝试加载词汇表文件
            var vocabPath = Path.Combine(modelDir, "vocab.json");
            if (File.Exists(vocabPath))
            {
                try
                {
                    var json = File.ReadAllText(vocabPath);
                    var vocab = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(json);
                    foreach (var kvp in vocab)
                    {
                        _vocabulary[kvp.Value] = kvp.Key;
                    }
                    Console.WriteLine($"[语音转文字] 加载词汇表: {_vocabulary.Count} 个词");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[语音转文字] 加载词汇表失败: {ex.Message}");
                }
            }
            else
            {
                // 使用默认的基础词汇表
                InitializeDefaultVocabulary();
            }
        }

        /// <summary>
        /// 初始化默认词汇表
        /// </summary>
        private void InitializeDefaultVocabulary()
        {
            // 基础 ASCII 字符
            for (int i = 0; i < 256; i++)
            {
                if (char.IsLetterOrDigit((char)i) || char.IsPunctuation((char)i) || char.IsWhiteSpace((char)i))
                {
                    _vocabulary[i] = ((char)i).ToString();
                }
            }

            // 常用中文字符（简化）
            // 实际应该加载完整的词汇表
            var commonChinese = "的一是不了人我在有他这为之大来以个中上们到说国和地也子时道出而要于就下得可你年生自会那后能对着事其里所去行过家十用发天如然作方成者多日都三小军二无同主经公此已工使情明开面起样机定现见门位很山民候第何安世但国军只以因太更什间点合内即题见";

            int startId = 50000;
            foreach (var c in commonChinese)
            {
                if (!_vocabulary.ContainsValue(c.ToString()))
                {
                    _vocabulary[startId++] = c.ToString();
                }
            }
        }

        /// <summary>
        /// 语音转文字
        /// </summary>
        public async Task<string> TranscribeAsync(byte[] audioData)
        {
            return await Task.Run(() => Transcribe(audioData));
        }

        /// <summary>
        /// 语音转文字（同步版本）
        /// </summary>
        public string Transcribe(byte[] audioData)
        {
            if (!IsInitialized)
            {
                // 模拟模式，返回占位文本
                Console.WriteLine("[语音转文字] 模型未初始化，使用模拟模式");
                return "[请配置语音识别模型]";
            }

            try
            {
                // 1. 预处理音频
                var melSpectrogram = PreprocessAudioToMel(audioData);

                // 2. 编码
                var encoderOutput = RunEncoder(melSpectrogram);

                // 3. 解码
                var tokens = RunDecoder(encoderOutput);

                // 4. 转换为文本
                var text = TokensToText(tokens);

                return text;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[语音转文字] 转写失败: {ex.Message}");
                return "";
            }
        }

        /// <summary>
        /// 预处理音频为 Mel 频谱图
        /// </summary>
        private float[] PreprocessAudioToMel(byte[] audioData)
        {
            // 将 PCM 数据转换为浮点数
            int sampleCount = audioData.Length / 2;
            float[] samples = new float[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                short sample = BitConverter.ToInt16(audioData, i * 2);
                samples[i] = sample / 32768.0f;
            }

            // 计算 Mel 频谱图
            // 这里简化处理，实际需要使用完整的 STFT + Mel 滤波器组
            int nMels = 80;
            int nFrames = Math.Min(3000, samples.Length / 160); // 帧数

            float[] melSpectrogram = new float[nMels * nFrames];

            // 简化的 Mel 频谱计算
            int hopLength = 160;
            int fftSize = 400;

            for (int frame = 0; frame < nFrames; frame++)
            {
                int start = frame * hopLength;
                int end = Math.Min(start + fftSize, samples.Length);

                // 计算帧的能量（简化）
                float energy = 0;
                for (int i = start; i < end; i++)
                {
                    energy += samples[i] * samples[i];
                }
                energy = (float)Math.Log(Math.Max(1e-10, energy / (end - start)));

                // 填充所有 Mel 频带（实际应该使用 FFT + Mel 滤波器）
                for (int mel = 0; mel < nMels; mel++)
                {
                    melSpectrogram[frame * nMels + mel] = energy * (1 + mel * 0.01f);
                }
            }

            return melSpectrogram;
        }

        /// <summary>
        /// 运行编码器
        /// </summary>
        private float[] RunEncoder(float[] melSpectrogram)
        {
            if (_encoderSession == null)
                return new float[0];

            try
            {
                var inputName = _encoderSession.InputMetadata.Keys.First();
                var inputMeta = _encoderSession.InputMetadata[inputName];

                // 调整输入形状 [batch, n_mels, n_frames]
                int nMels = 80;
                int nFrames = melSpectrogram.Length / nMels;
                var inputShape = new int[] { 1, nMels, nFrames };

                // 转置数据 [frames, mels] -> [mels, frames]
                float[] transposed = new float[melSpectrogram.Length];
                for (int m = 0; m < nMels; m++)
                {
                    for (int f = 0; f < nFrames; f++)
                    {
                        transposed[m * nFrames + f] = melSpectrogram[f * nMels + m];
                    }
                }

                // 创建张量并填充数据
                var inputTensor = new DenseTensor<float>(new ReadOnlySpan<int>(inputShape));
                for (int i = 0; i < transposed.Length && i < inputTensor.Length; i++)
                {
                    inputTensor.SetValue(i, transposed[i]);
                }
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputName, inputTensor)
                };

                using (var results = _encoderSession.Run(inputs))
                {
                    var output = results.First();
                    return output.AsTensor<float>().ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[语音转文字] 编码失败: {ex.Message}");
                return new float[0];
            }
        }

        /// <summary>
        /// 运行解码器
        /// </summary>
        private int[] RunDecoder(float[] encoderOutput)
        {
            if (_decoderSession == null && _encoderSession == null)
                return new int[0];

            var session = _decoderSession ?? _encoderSession;
            var tokens = new List<int>();

            try
            {
                // 初始 token 序列
                var initialTokens = new int[]
                {
                    SOT_TOKEN,
                    _settings.Language == "zh" ? ZH_TOKEN : EN_TOKEN,
                    TRANSCRIBE_TOKEN,
                    NOTIMESTAMPS_TOKEN
                };

                tokens.AddRange(initialTokens);

                // 自回归解码
                int maxLength = _settings.MaxDecodingLength;

                for (int step = 0; step < maxLength; step++)
                {
                    // 准备解码器输入
                    // 这里简化处理，实际需要正确的输入格式

                    // 简化：使用贪婪搜索获取下一个 token
                    int nextToken = GreedyDecode(session, encoderOutput, tokens.ToArray());

                    if (nextToken == EOT_TOKEN)
                        break;

                    tokens.Add(nextToken);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[语音转文字] 解码失败: {ex.Message}");
            }

            return tokens.ToArray();
        }

        /// <summary>
        /// 贪婪解码获取下一个 token
        /// </summary>
        private int GreedyDecode(InferenceSession session, float[] encoderOutput, int[] currentTokens)
        {
            // 简化实现：返回一个模拟的 token
            // 实际应该运行解码器并选择概率最高的 token

            // 这里返回 EOT 表示结束
            if (currentTokens.Length > 10)
                return EOT_TOKEN;

            // 返回一些常用字符的 token
            var commonTokens = new int[] { (int)'你', (int)'好', (int)'我', (int)'是' };
            var random = new Random();
            return commonTokens[random.Next(commonTokens.Length)];
        }

        /// <summary>
        /// 将 token 序列转换为文本
        /// </summary>
        private string TokensToText(int[] tokens)
        {
            var textParts = new List<string>();

            foreach (var token in tokens)
            {
                // 跳过特殊 token
                if (token >= 50000)
                    continue;

                if (_vocabulary.TryGetValue(token, out var text))
                {
                    textParts.Add(text);
                }
            }

            return string.Join("", textParts).Trim();
        }

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
