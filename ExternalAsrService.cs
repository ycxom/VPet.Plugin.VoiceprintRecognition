using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VPet.Plugin.VoiceprintRecognition
{
    /// <summary>
    /// 通用外部 ASR API 客户端
    /// 支持 multipart、base64json、rawbinary 三种请求格式
    /// </summary>
    public class ExternalAsrService : IDisposable
    {
        private readonly VoiceprintSettings _settings;
        private readonly HttpClient _httpClient;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logDebug;

        public ExternalAsrService(VoiceprintSettings settings,
            Action<string> logInfo = null, Action<string> logDebug = null)
        {
            _settings = settings;
            _logInfo = logInfo ?? (s => Console.WriteLine($"[ASR] {s}"));
            _logDebug = logDebug ?? (s => Console.WriteLine($"[ASR][DEBUG] {s}"));

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(settings.AsrTimeout)
            };
        }

        /// <summary>
        /// 将 PCM 数据转换为 WAV 格式
        /// </summary>
        private byte[] PcmToWav(byte[] pcmData)
        {
            int sampleRate = _settings.SampleRate;
            int channels = _settings.Channels;
            int bitsPerSample = _settings.BitsPerSample;
            int byteRate = sampleRate * channels * bitsPerSample / 8;
            int blockAlign = channels * bitsPerSample / 8;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // RIFF header
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + pcmData.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));

            // fmt chunk
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16); // chunk size
            writer.Write((short)1); // PCM format
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write((short)blockAlign);
            writer.Write((short)bitsPerSample);

            // data chunk
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(pcmData.Length);
            writer.Write(pcmData);

            writer.Flush();
            return ms.ToArray();
        }

        /// <summary>
        /// 异步转录音频
        /// </summary>
        public async Task<string> TranscribeAsync(byte[] pcmAudioData)
        {
            if (string.IsNullOrWhiteSpace(_settings.AsrApiUrl))
            {
                _logInfo("ASR API URL 未配置");
                return null;
            }

            try
            {
                var wavData = PcmToWav(pcmAudioData);
                _logDebug($"PCM → WAV: {pcmAudioData.Length} → {wavData.Length} bytes");

                HttpResponseMessage response;

                switch (_settings.AsrRequestFormat.ToLower())
                {
                    case "multipart":
                        response = await SendMultipartAsync(wavData);
                        break;
                    case "base64json":
                        response = await SendBase64JsonAsync(wavData);
                        break;
                    case "rawbinary":
                        response = await SendRawBinaryAsync(wavData);
                        break;
                    default:
                        _logInfo($"不支持的请求格式: {_settings.AsrRequestFormat}");
                        return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync();
                    _logInfo($"ASR API 返回错误: {response.StatusCode} - {errorBody}");
                    return null;
                }

                var responseBody = await response.Content.ReadAsStringAsync();
                _logDebug($"ASR 响应: {responseBody}");

                return ExtractTextFromResponse(responseBody);
            }
            catch (TaskCanceledException)
            {
                _logInfo($"ASR 请求超时 ({_settings.AsrTimeout}s)");
                return null;
            }
            catch (Exception ex)
            {
                _logInfo($"ASR 调用失败: {ex.Message}");
                _logDebug($"详情: {ex}");
                return null;
            }
        }

        private async Task<HttpResponseMessage> SendMultipartAsync(byte[] wavData)
        {
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(wavData);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", "audio.wav");

            if (!string.IsNullOrWhiteSpace(_settings.AsrLanguage))
                content.Add(new StringContent(_settings.AsrLanguage), "language");

            var request = new HttpRequestMessage(HttpMethod.Post, _settings.AsrApiUrl) { Content = content };
            AddAuthHeader(request);

            _logDebug("发送 multipart 请求...");
            return await _httpClient.SendAsync(request);
        }

        private async Task<HttpResponseMessage> SendBase64JsonAsync(byte[] wavData)
        {
            var base64Audio = Convert.ToBase64String(wavData);
            var jsonBody = JsonSerializer.Serialize(new
            {
                audio = base64Audio,
                format = "wav",
                language = _settings.AsrLanguage ?? "zh"
            });

            var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, _settings.AsrApiUrl) { Content = content };
            AddAuthHeader(request);

            _logDebug("发送 base64json 请求...");
            return await _httpClient.SendAsync(request);
        }

        private async Task<HttpResponseMessage> SendRawBinaryAsync(byte[] wavData)
        {
            var content = new ByteArrayContent(wavData);
            content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

            var request = new HttpRequestMessage(HttpMethod.Post, _settings.AsrApiUrl) { Content = content };
            AddAuthHeader(request);

            _logDebug("发送 rawbinary 请求...");
            return await _httpClient.SendAsync(request);
        }

        private void AddAuthHeader(HttpRequestMessage request)
        {
            if (!string.IsNullOrWhiteSpace(_settings.AsrApiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AsrApiKey);
            }
        }

        /// <summary>
        /// 从 JSON 响应中根据配置路径提取文本
        /// 支持 "result"、"data.text"、"response.alternatives[0].transcript" 等路径
        /// </summary>
        private string ExtractTextFromResponse(string responseBody)
        {
            try
            {
                using var doc = JsonDocument.Parse(responseBody);
                var root = doc.RootElement;

                var path = _settings.AsrResponseTextPath ?? "result";
                var parts = path.Split('.');

                JsonElement current = root;
                foreach (var part in parts)
                {
                    // 处理数组索引，如 "alternatives[0]"
                    int bracketIdx = part.IndexOf('[');
                    if (bracketIdx >= 0)
                    {
                        var propName = part.Substring(0, bracketIdx);
                        var indexStr = part.Substring(bracketIdx + 1, part.Length - bracketIdx - 2);

                        if (!string.IsNullOrEmpty(propName))
                        {
                            if (!current.TryGetProperty(propName, out current))
                            {
                                _logInfo($"JSON 路径 '{propName}' 不存在");
                                return null;
                            }
                        }

                        if (int.TryParse(indexStr, out int arrayIndex))
                        {
                            if (current.ValueKind == JsonValueKind.Array && arrayIndex < current.GetArrayLength())
                            {
                                current = current[arrayIndex];
                            }
                            else
                            {
                                _logInfo($"JSON 数组索引 [{arrayIndex}] 无效");
                                return null;
                            }
                        }
                    }
                    else
                    {
                        if (!current.TryGetProperty(part, out current))
                        {
                            _logInfo($"JSON 路径 '{part}' 不存在");
                            return null;
                        }
                    }
                }

                var text = current.ValueKind == JsonValueKind.String
                    ? current.GetString()
                    : current.GetRawText();

                _logDebug($"提取文本: {text}");
                return text?.Trim();
            }
            catch (Exception ex)
            {
                _logInfo($"解析 ASR 响应失败: {ex.Message}");
                _logDebug($"响应内容: {responseBody}");
                return null;
            }
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
