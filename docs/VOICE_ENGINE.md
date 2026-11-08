# 内置语音引擎

致电 2.0 将 GPT-SoVITS 推理放入应用的 `engines/gpt-sovits` 目录。无需另外安装 Python、启动 WebUI 或运行 API 服务。C# 通过私有标准输入/输出管道调用随附 Python，程序不为内置语音监听网络端口。

## 使用

在“设置 → 角色声音”选择“GPT-SoVITS 内置”，分别选择当前角色的 GPT `.ckpt`、SoVITS `.pth` 和参考音频，填写参考文本及其语言，然后加载或试听。每个角色保存自己的声音设置，切换角色会停止旧声音并释放旧推理进程。角色权重和参考音频是用户资产，不会被缓存清理删除，也不随公开发布包分发。

参考音频建议为约 3～10 秒的清晰单人声音，文本应与音频内容匹配。日语聊天模式将合成文本语言固定为日语，参考音频仍使用其实际语言。启用自动朗读后，回复与主动聊天都使用当前角色的声音。

检测到可用 CUDA 时使用 NVIDIA GPU，否则使用 CPU。首次加载包含模型读盘与前端初始化，耗时高于后续合成；显存不足时关闭其他占用程序后重试。取消合成、切换角色和关闭窗口会终止本应用拥有的推理进程；闲置约一分钟后自动卸载，以释放内存和显存。

“GPT-SoVITS 外接”、OpenAI-compatible 和 Custom JSON 仍可选。旧版默认本机 `9880/tts` 配置会迁移为内置方式，并保留权重、参考音频和其余角色配置；明确使用其他地址的服务保留外接模式。

## 精简范围

源码固定为 [RVC-Boss/GPT-SoVITS 的 04a44eb 提交](https://github.com/RVC-Boss/GPT-SoVITS/tree/04a44eb977586645f33594b72af13a035a2056c6)。保留直接合成调用链，移除训练器、WebUI、ASR、分离工具及训练判别器权重，不内置默认示例声音。Lightning 推理封装改为 `torch.nn.Module`，保留权重键和推理方法；普通中文使用上游已有的 pypinyin 前端，避免额外 G2PW 模型。日语词典与英语词性资源预置离线使用。

基础资源包含 BERT、HuBERT、语言识别模型，以及 v2Pro 的说话人编码器和 v3/v4/v5 的声码器、LoRA 所需底模。它们用于读取对应版本角色模型，不是多份默认角色声音。未启用额外超分。发布清单记录资源版本、来源、文件大小和 SHA-256；`variants` 表示资源配置范围，`verifiedVariants` 表示实际完成合成验证的版本，`verificationProfiles` 区分角色权重与官方基模、测试语言和设备。本机已验证 v2ProPlus 角色中日语 GPU/CPU，以及 v3/v4/v5dev/v5turbo 官方基模的 GPU 中文推理链；未逐一验证任意第三方 LoRA。不同训练分支的自定义权重仍可能不兼容。

保留 Python、Torch/CUDA 运行库及推理需要的词典；另将实际需要的 5 个微软 C++ DLL 放在引擎目录内，无需向系统安装运行库。只删除明确无运行用途的静态链接库、头文件、安装工具和编译缓存，不通过删除必要 DLL 或词典缩减体积。CUDA 运行库占主要体积，应用不会向系统安装 CUDA Toolkit。

## 缓存与进程

- 每次推理进程使用独立 `cache/voice-engine/<会话>`。TEMP、TMP、用户配置路径、HF、Torch、Numba、CUDA、分词缓存等均指向该目录，环境变量只对该子进程生效。
- 音频先写 `.part`，完成后原子提交并移入程序 `cache`，播放结束立即删除。合成失败、取消、角色切换、闲置卸载和正常退出都会清理对应会话。
- Windows Job Object 在宿主退出或异常终止时结束其推理子进程。启动清理和现有缓存清理负责回收上次异常退出留下的文件；强制断电期间无法执行删除，下一次启动再回收。
- Python 层禁止联网及写入会话目录之外；日语原生词典编译也已显式改到会话目录。程序目录不可写时会报错，不退回系统 TEMP 或 C 盘。
- 模型、参考音频、长期记忆和聊天数据不属于垃圾。外接服务自行产生的缓存不受本应用控制。Windows 自身管理的分页文件、预取或崩溃记录不在应用可完全控制的范围内。

## 构建与诊断

`scripts/build-voice-engine.ps1` 仅获取推理资源并组装项目内独立环境；`requirements-voice.lock.txt` 固定 Python 依赖。`trim-voice-source.py` 与 `prepare-voice-engine.py` 对固定版本源码作可重复补丁，记录修改前后的校验值。`seal-voice-engine.py` 生成发布清单。普通 C# 构建不触发模型下载。

发布应用时传入 `scripts/build.ps1 -VoiceEngineDirectory .build/voice-engine/gpt-sovits`。`-HardlinkVoiceEngine` 可减少同一磁盘构建副本占用；每个发布目录中的文件都是正常可独立打包的文件，不依赖原目录路径。

开发验证可运行 `scripts/smoke-embedded-voice.py --engine-root <引擎目录> --state-file <本地状态文件> --work-root <项目内全新测试目录>`。它仅使用角色声音设置与固定测试句，不发送聊天内容、访问对话服务或输出密钥；可选 `--diagnostics` 将脱敏异常保存在测试目录内。交付应用默认不开诊断日志。

上游 MIT 许可证、Python/Torch 等随包许可证及各资源来源保留在引擎内。FFmpeg 来自 [上游说明指定的单文件](https://huggingface.co/lj1995/VoiceConversionWebUI/blob/main/ffmpeg.exe)，无需安装独立服务。
