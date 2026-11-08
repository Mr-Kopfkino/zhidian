# 第三方组件与资源

致电的原创源码按根目录 `LICENSE` 中的 MIT 许可证提供。以下第三方组件、资源和权利声明独立保留；项目的 MIT 许可证不会改变它们的许可。源码仓库只包含项目代码、构建脚本、依赖锁定文件和必要说明，完整运行库与公共基础模型随 Release 便携包提供。

## 应用与界面

| 组件 | 用途 | 随包许可或说明 |
| --- | --- | --- |
| XP.css | Windows XP 主题标题栏渐变的 WPF 适配及界面参考 | MIT，`licenses/XP.css-LICENSE.txt`；适配范围见 `docs/THEME.md` |
| Microsoft .NET / WPF | C# 和 Windows 桌面运行时 | 便携包 `licenses/microsoft.netcore.app.runtime.win-x64-LICENSE.TXT.txt`、`licenses/microsoft.netcore.app.runtime.win-x64-THIRD-PARTY-NOTICES.TXT.txt` 和 `licenses/microsoft.windowsdesktop.app.runtime.win-x64-LICENSE.txt` |
| Microsoft Visual C++ Runtime | 内置语音所需原生 DLL | `engines/gpt-sovits/licenses/Microsoft-VC-Runtime-License.docx` |

## 内置语音

下表中的引擎内路径均相对于便携包的 `engines/gpt-sovits/`。各项的实际分发文件以 `laidian-engine.json` 为准。

| 组件 | 来源与版本记录 | 许可位置 |
| --- | --- | --- |
| GPT-SoVITS | `RVC-Boss/GPT-SoVITS`，提交 `04a44eb977586645f33594b72af13a035a2056c6` | MIT，`LICENSE`；本项目的推理精简及缓存补丁记录在 `laidian-source-trim.json` 和 `laidian-cache-patches.json` |
| Python | CPython 3.11.9 嵌入式运行时 | `runtime/LICENSE.txt` |
| PyTorch / TorchAudio | 2.6.0，CUDA 12.4 构建；版本锁定于源码 `requirements-voice.lock.txt` | `runtime/Lib/site-packages/` 中相应包及 `*.dist-info` 内的许可证；CUDA 相关条款保留在运行库中 |
| FFmpeg | 独立的未修改可执行文件，下载地址及 SHA-256 记录在 `resource-manifest.json` | `licenses/FFmpeg-LGPL-3.0.txt`、`licenses/FFmpeg-GPL-3.0.txt`；来源记录在 `licenses/SOURCES.txt`，实际构建参数可运行 `runtime/ffmpeg.exe -buildconf` 查看 |
| fastText | 语言识别代码和 `lid.176.bin` 语言识别数据 | 代码许可在 `licenses/fastText-LICENSE.txt`；数据的 CC-BY-SA-3.0 来源声明在 `licenses/SOURCES.txt` |
| BERT / HuBERT / GPT-SoVITS 公共基础资源 | 每个资源的来源、固定版本、大小和 SHA-256 记录在 `resource-manifest.json`；源码锁定表为 `scripts/voice-resources.lock.json` | 保留各资源及包中的原有声明，BERT 许可文本在 `licenses/BERT-Apache-2.0.txt`；模型来源见 `licenses/SOURCES.txt` |
| NLTK 词典及其他 Python 依赖 | 版本见源码 `requirements-voice.lock.txt` 和 `scripts/voice-dictionaries.lock.json` | 相应资源的 LICENSE/README 及 `runtime/Lib/site-packages/` 下包和 `*.dist-info` 中的许可文件 |

上游来源索引和构建脚本：`scripts/fetch-voice-licenses.py`、`scripts/build-voice-engine.ps1`、`scripts/prepare-vc-runtime.py`。完整运行包中的 `licenses/SOURCES.txt` 提供上游源码地址。重新构建或修改第三方组件时，应保留它们的版权、许可和修改记录。

## 默认角色与用户素材

默认角色五河琴里源于《约会大作战》，相关作品、角色名称及形象权利属于各自权利人。本项目并非官方软件，也不表示与原作权利人有关联。默认文字设定的资料依据与改编范围见 `docs/KOTORI.md`。

公开分发不附带官方立绘、动画音轨、声优声音模型、用户训练的角色权重或参考音频。用户导入的图片、声音及角色资源不因导入应用而改变原有权利与许可。
