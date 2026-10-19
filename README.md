# KaraokeConvert

面向 Windows 的原生桌面程序，使用 FFmpeg 转换和合并影音文件。支持把文件拖进窗口，也可以通过文件选择按钮添加。输出自动保存在源文件所在目录，源文件不会被修改。

## 开始使用

1. 打开 [v1.0 发布页](https://github.com/koishi8/karaoke-convert/releases/tag/v1.0)，下载 `KaraokeConvert-Windows.zip` 并解压。
2. 在解压后的根目录双击 `KaraokeConvert.exe`。保留旁边的 `KaraokeConvert.exe.config`。
3. 确认窗口已找到 `ffmpeg.exe` 和 `ffprobe.exe`；找不到时，点击“设置 FFmpeg 位置”选择它们所在的文件夹。
4. 选择需要的转换方式，拖入文件，然后开始处理。AVI 合成需要先点击“添加合成任务”，再开始处理队列。

需要 Windows 10/11、.NET Framework 4.8，以及同一套 FFmpeg 中的 `ffmpeg.exe`、`ffprobe.exe`。程序不使用浏览器页面，不需要 Python 或本地网页服务。

发布包和源码不包含 FFmpeg。可从 [FFmpeg 官方下载页](https://ffmpeg.org/download.html) 获取 Windows 构建，将同一套构建中的 `ffmpeg.exe` 和 `ffprobe.exe` 放在软件所在目录或 `tools` 子目录，也可以使用已加入 `PATH` 的 FFmpeg，或在窗口中手动指定文件夹。MP4 兼容转码需要构建中包含 `libx264` 和 AAC 编码器。

## 三种处理方式

| 任务 | 输入 | 处理方式 | 输出位置 |
| --- | --- | --- | --- |
| FLAC 转 WAV | 一个或多个 `.flac` | 解码为 PCM，保留原始采样率、声道和有效位深 | 每个 FLAC 所在目录 |
| MKV 转 MP4 | 一个或多个 `.mkv` | 优先直接复制音视频流，速度快且不会因重新编码损失画质或音质 | 每个 MKV 所在目录 |
| AVI 合并外部音轨 | `.avi` 视频和 `.wav` / `.flac` 音频 | 复制 AVI 视频，使用指定外部音轨，丢弃 AVI 原音轨，封装成 `.mkv` | AVI 所在目录 |

在合并模式中，可以一次拖入一个 AVI 和一个音频文件，也可以分别选择，再添加到任务队列。外部 FLAC 可以直接复制；外部 WAV 可以保留为 PCM，也可以无损压缩为 FLAC。为完整保留精度，浮点、超过 24 位或超过 8 声道的 PCM 即使勾选压缩也会保持原样复制，并在日志中说明。复制速度主要由硬盘读写决定；FLAC 压缩需要额外运算时间。

### MP4 兼容转码

MP4 是容器格式，本身不代表有损。能放入 MP4 的现有音视频流会优先直接复制。

如果原始编码不能封装到 MP4，勾选“允许兼容转码”时，程序可改用 H.264 视频和 AAC 音频。此时会重新编码，耗时更长，并产生有损输出；视频采用 CRF 18、`veryfast` 预设，音频采用 AAC 256 kb/s。转码会转换到常见的 8 位 `yuv420p`，原视频的高位深或色彩信息可能发生变化。关闭这一选项时，不兼容的文件会报告失败，不会自动有损转换。

MKV 中的字幕和附件不会写入 MP4。请保留原始 MKV，需要字幕时单独处理。

## 输出与无损的含义

- 输出在输入文件目录下；合并时以 AVI 的目录为准。已有同名文件会自动避开，避免覆盖。
- FLAC 和 MKV 转换沿用源文件名；AVI 合成命名为 `文件名_无损.mkv`，重名时添加 ` (2)`、` (3)` 等编号。
- 窗口提供进度、处理结果和错误信息。处理失败或取消时，会清理本次未完成的输出。
- FLAC 转 WAV 保留的是音频采样数据，文件头、标签、封面及文件校验值无需与原文件一致。超过普通 WAV 容量限制时使用 RF64。
- AVI 合并只替换音频来源，不重新压缩视频。若原视频本来采用有损编码，复制会保留现有质量，不会恢复已丢失的细节。
- 合并不会主动截断较长的音频或视频；两者长度不同时，较短的轨道会先结束。程序不会自动校准外部音频的起始偏移。
- 严格复制或无损合并遇到当前 FFmpeg / 容器不支持的编码时，会显示错误；不会悄悄用有损编码代替。

## 从源码构建

在项目目录打开 PowerShell，运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

脚本使用 Windows .NET Framework 自带的 C# 编译器，生成 `dist\KaraokeConvert.exe` 和对应配置文件，并在 `dist` 中创建 Windows ZIP 发布包及 `SHA256SUMS.txt` 校验文件。源码构建后的程序在 `dist` 目录；运行时保留 EXE 和对应配置文件在同一目录。

主要源码位于 `src`；`build.ps1` 为构建脚本。[项目仓库](https://github.com/koishi8/karaoke-convert) 包含完整源码和测试。FFmpeg 作为独立进程执行，转换过程中不会上传媒体文件。

## 验证

已使用 FFmpeg 8.0.1 通过 14 项集成测试：16/24/32 位 FLAC 转 WAV 的 PCM 采样完全一致，直接封装保留音视频数据包，AVI 原音轨被排除且较长外部音轨不被截断；同时覆盖可选无损压缩、有损回退开关、重名保护、取消清理和写入错误处理。

FFmpeg 已加入 PATH 时，可直接运行 `powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1`。也可以指定工具位置：

```powershell
powershell -ExecutionPolicy Bypass -File .\tests\run-tests.ps1 -FFmpeg 'C:\ffmpeg\bin\ffmpeg.exe'
```

将示例路径替换为实际位置。测试自行生成短媒体样本，产物保存在 `tests\artifacts`。
