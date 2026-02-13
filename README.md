# VPet 声纹识别插件

使用 ONNX 模型实现本地化声纹识别，支持声纹验证和语音转文字功能。

## 功能特性

- **声纹验证**: 使用 ONNX 声纹模型进行说话人验证
- **语音转文字**: 使用 Whisper ONNX 模型进行本地语音识别
- **音频采集**: 使用 NAudio 进行麦克风录音
- **多用户支持**: 支持注册多个用户的声纹

## 目录结构

```
1103_VoiceprintRecognition/
├── info.lps              # MOD 信息文件
├── plugin/               # DLL 文件目录
│   ├── VPet.Plugin.VoiceprintRecognition.dll
│   ├── Microsoft.ML.OnnxRuntime.dll
│   ├── NAudio.dll
│   └── ...
├── models/               # ONNX 模型目录
│   ├── voiceprint.onnx   # 声纹识别模型
│   └── whisper-tiny.onnx # 语音转文字模型
└── data/                 # 数据目录
    ├── settings.json     # 设置文件
    └── voiceprints.json  # 已注册声纹数据
```

## 模型下载

### 声纹识别模型

推荐使用以下声纹模型之一：

1. **ECAPA-TDNN** (推荐)
   - 下载: [speechbrain/spkrec-ecapa-voxceleb](https://huggingface.co/speechbrain/spkrec-ecapa-voxceleb)
   - 转换为 ONNX 格式

2. **ResNetSE34V2**
   - 下载: [clovaai/voxceleb_resnetse34v2](https://github.com/clovaai/voxceleb_trainer)

3. **自定义模型**
   - 输入: 音频波形 [batch, samples] 或 Mel 频谱 [batch, n_mels, frames]
   - 输出: 特征向量 [batch, embedding_dim]

### 语音转文字模型

推荐使用 Whisper ONNX 模型：

1. **Whisper Tiny** (推荐，体积小)
   - 下载: [openai/whisper-tiny](https://huggingface.co/openai/whisper-tiny)
   - ONNX 版本: [zhuzilin/whisper-tiny-onnx](https://huggingface.co/zhuzilin/whisper-tiny-onnx)

2. **Whisper Base**
   - 更高精度，需要更多资源

### 模型转换

如果需要将 PyTorch 模型转换为 ONNX:

```python
import torch
import torch.onnx

# 加载模型
model = load_your_model()
model.eval()

# 准备示例输入
dummy_input = torch.randn(1, 16000)  # 1秒 16kHz 音频

# 导出 ONNX
torch.onnx.export(
    model,
    dummy_input,
    "voiceprint.onnx",
    input_names=['audio'],
    output_names=['embedding'],
    dynamic_axes={'audio': {1: 'samples'}}
)
```

## 使用方法

### 1. 安装插件

将编译好的 `1103_VoiceprintRecognition` 文件夹复制到 VPet 的 `mod` 目录。

### 2. 配置模型

将 ONNX 模型文件放入 `models` 目录：
- `voiceprint.onnx` - 声纹识别模型
- `whisper-tiny.onnx` - 语音转文字模型（可选）

### 3. 注册声纹

1. 打开设置 -> 声纹识别设置
2. 点击"注册新声纹"
3. 输入用户名
4. 按提示录音 3-5 秒
5. 完成注册

### 4. 使用语音输入

1. 在聊天框中点击麦克风按钮
2. 长按按钮开始录音
3. 说话完成后松开按钮
4. 系统会自动进行声纹验证和语音转文字

## 配置说明

### 设置文件 (settings.json)

```json
{
  "EnableVoiceInput": true,
  "EnableVoiceprintVerification": true,
  "RequireVoiceprintMatch": false,
  "VoiceprintModelFile": "voiceprint.onnx",
  "VoiceprintThreshold": 0.7,
  "WhisperModelFile": "whisper-tiny.onnx",
  "Language": "zh",
  "SampleRate": 16000,
  "UseGPU": false,
  "NumThreads": 4
}
```

### 参数说明

| 参数 | 说明 | 默认值 |
|------|------|--------|
| EnableVoiceInput | 启用语音输入 | true |
| EnableVoiceprintVerification | 启用声纹验证 | true |
| RequireVoiceprintMatch | 要求声纹匹配 | false |
| VoiceprintThreshold | 声纹验证阈值 (-1 到 1) | 0.7 |
| Language | 语音识别语言 | zh |
| SampleRate | 音频采样率 | 16000 |
| UseGPU | 使用 GPU 加速 | false |

## 技术细节

### 声纹验证流程

1. 音频采集 (NAudio) -> PCM 数据
2. 预处理 -> 归一化、分帧
3. ONNX 推理 -> 提取声纹特征向量
4. 余弦相似度计算 -> 与注册声纹比较
5. 阈值判断 -> 验证结果

### 语音转文字流程

1. 音频采集 -> PCM 数据
2. 预处理 -> Mel 频谱图
3. Whisper 编码器 -> 音频特征
4. Whisper 解码器 -> Token 序列
5. 词汇表映射 -> 文本输出

## 依赖项

- VPet-Simulator.Core >= 1.1.0.58
- VPet-Simulator.Windows.Interface >= 1.1.0.58
- Microsoft.ML.OnnxRuntime >= 1.16.3
- NAudio >= 2.2.1
- Panuon.WPF.UI >= 1.2.4.10

## 许可证

MIT License

## 问题反馈

如有问题，请在 GitHub Issues 中提交。
