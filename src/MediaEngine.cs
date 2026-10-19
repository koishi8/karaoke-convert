using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace LosslessMedia
{
    public enum MediaTaskKind { FlacToWav, MkvToMp4, MergeMp4Audio }

    public sealed class MediaJob
    {
        public MediaTaskKind Kind { get; set; }
        public string InputPath { get; set; }
        public string AudioPath { get; set; }
        public string OutputPath { get; set; }
        public bool CompressAudio { get; set; }
        public bool AllowLossyFallback { get; set; }
    }

    public sealed class MediaProgress
    {
        public double? Percent { get; set; }
        public string Message { get; set; }
        public string LogLine { get; set; }
    }

    public sealed class ConversionResult
    {
        public string OutputPath { get; set; }
        public string Summary { get; set; }
        public bool UsedLossyEncoding { get; set; }
    }

    public sealed class ToolPaths
    {
        public string FFmpegPath { get; set; }
        public string FFprobePath { get; set; }

        public static ToolPaths Resolve(string folder)
        {
            List<string> candidates = new List<string>();
            if (!String.IsNullOrWhiteSpace(folder))
            {
                string configured = Path.GetFullPath(Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"')));
                if (File.Exists(configured)) configured = Path.GetDirectoryName(configured);
                candidates.Add(configured);
                candidates.Add(Path.Combine(configured, "bin"));
            }
            string application = AppDomain.CurrentDomain.BaseDirectory;
            candidates.Add(application);
            candidates.Add(Path.Combine(application, "tools"));
            candidates.Add(Path.Combine(application, "tools", "bin"));
            candidates.Add(Path.Combine(application, "tools", "ffmpeg", "bin"));
            candidates.Add(Path.Combine(application, "ffmpeg", "bin"));
            candidates.Add(Path.Combine(application, "bin"));
            string parent = Path.GetDirectoryName(application.TrimEnd(Path.DirectorySeparatorChar));
            if (!String.IsNullOrEmpty(parent)) candidates.Add(Path.Combine(parent, "tools"));
            string environmentPath = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            foreach (string entry in environmentPath.Split(Path.PathSeparator))
                if (!String.IsNullOrWhiteSpace(entry)) candidates.Add(Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')));

            string firstFfmpeg = null;
            string firstFfprobe = null;
            foreach (string candidate in candidates)
            {
                try
                {
                    string ffmpeg = Path.Combine(candidate, "ffmpeg.exe");
                    string ffprobe = Path.Combine(candidate, "ffprobe.exe");
                    bool haveFfmpeg = File.Exists(ffmpeg);
                    bool haveFfprobe = File.Exists(ffprobe);
                    if (haveFfmpeg && haveFfprobe)
                        return new ToolPaths { FFmpegPath = Path.GetFullPath(ffmpeg), FFprobePath = Path.GetFullPath(ffprobe) };
                    if (haveFfmpeg && firstFfmpeg == null) firstFfmpeg = Path.GetFullPath(ffmpeg);
                    if (haveFfprobe && firstFfprobe == null) firstFfprobe = Path.GetFullPath(ffprobe);
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }
            if (firstFfmpeg != null && firstFfprobe != null)
                return new ToolPaths { FFmpegPath = firstFfmpeg, FFprobePath = firstFfprobe };
            throw new FileNotFoundException("未找到完整的 FFmpeg 工具。请将 ffmpeg.exe 和 ffprobe.exe 放入软件旁的 tools 文件夹，或在设置中选择它们所在的文件夹。");
        }
    }

    public sealed class MediaEngine
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

        public Task<ConversionResult> RunAsync(MediaJob job, ToolPaths tools, IProgress<MediaProgress> progress, CancellationToken cancellationToken)
        {
            if (job == null) throw new ArgumentNullException("job");
            if (tools == null) throw new ArgumentNullException("tools");
            // Snapshot mutable UI state before beginning work on a worker thread.
            MediaJob snapshot = new MediaJob { Kind = job.Kind, InputPath = job.InputPath, AudioPath = job.AudioPath,
                OutputPath = job.OutputPath, CompressAudio = job.CompressAudio, AllowLossyFallback = job.AllowLossyFallback };
            ToolPaths toolSnapshot = new ToolPaths { FFmpegPath = tools.FFmpegPath, FFprobePath = tools.FFprobePath };
            return Task.Run(delegate { return Run(snapshot, toolSnapshot, progress, cancellationToken); });
        }

        public static string GenerateOutputPath(MediaJob job)
        {
            if (job == null) throw new ArgumentNullException("job");
            return GenerateOutputPath(job.InputPath, job.Kind);
        }

        public static string GenerateOutputPath(string inputPath, MediaTaskKind kind)
        {
            string full = Path.GetFullPath(inputPath);
            string extension = ExtensionFor(kind);
            string stem = Path.GetFileNameWithoutExtension(full);
            if (kind == MediaTaskKind.MergeMp4Audio) stem += "_无损合成";
            string directory = Path.GetDirectoryName(full);
            string proposed = Path.Combine(directory, stem + extension);
            int number = 1;
            while (File.Exists(proposed) || Directory.Exists(proposed) || SamePath(proposed, full))
                proposed = Path.Combine(directory, stem + " (" + (number++).ToString(Invariant) + ")" + extension);
            return proposed;
        }

        private static ConversionResult Run(MediaJob job, ToolPaths tools, IProgress<MediaProgress> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Validate(job, tools);
            if (String.IsNullOrWhiteSpace(job.OutputPath)) job.OutputPath = GenerateOutputPath(job);
            job.InputPath = Path.GetFullPath(job.InputPath);
            if (!String.IsNullOrWhiteSpace(job.AudioPath)) job.AudioPath = Path.GetFullPath(job.AudioPath);
            job.OutputPath = Path.GetFullPath(job.OutputPath);
            ValidateOutput(job);

            Report(progress, null, "正在读取媒体信息…", null);
            MediaInfo source = Probe(job.InputPath, tools, token);
            MediaInfo audio = null;
            if (job.Kind == MediaTaskKind.MergeMp4Audio) audio = Probe(job.AudioPath, tools, token);
            ValidateStreams(job, source, audio);

            List<string> notes = new List<string>();
            double duration = source.Duration;
            if (audio != null)
            {
                duration = Math.Max(source.Duration, audio.Duration);
                if (source.Duration > 0 && audio.Duration > 0 && Math.Abs(source.Duration - audio.Duration) > 0.10)
                {
                    string warning = "提示：视频时长 " + FormatDuration(source.Duration) + "，外部音频时长 " + FormatDuration(audio.Duration) + "。将完整保留两者，不截短；较短音轨结束后可能无声或无画面。";
                    notes.Add(warning);
                    Report(progress, null, warning, warning);
                }
            }

            string temp = Path.Combine(Path.GetDirectoryName(job.OutputPath), "." + Path.GetFileNameWithoutExtension(job.OutputPath) + ".partial." + Guid.NewGuid().ToString("N") + ExtensionFor(job.Kind));
            bool usedLossy = false;
            try
            {
                List<string> args = BuildArguments(job, source, audio, temp, false, notes, progress);
                Report(progress, 0, InitialMessage(job.Kind), null);
                ProcessResult conversion = Convert(tools.FFmpegPath, args, duration, progress, token, false);
                if (conversion.ExitCode != 0 && job.Kind == MediaTaskKind.MkvToMp4 && job.AllowLossyFallback && IsMp4CodecIncompatibility(conversion.Error))
                {
                    DeletePartial(temp);
                    usedLossy = true;
                    string fallback = "当前音视频编码无法直接装入 MP4，正在使用 H.264 / AAC 有损兼容转换。此步骤会重新编码，耗时更长。";
                    Report(progress, 0, fallback, fallback);
                    args = BuildArguments(job, source, audio, temp, true, notes, progress);
                    conversion = Convert(tools.FFmpegPath, args, duration, progress, token, true);
                }
                if (conversion.ExitCode != 0)
                {
                    string reason = "转换失败，未生成最终文件。";
                    if (job.Kind == MediaTaskKind.MkvToMp4 && IsMp4CodecIncompatibility(conversion.Error))
                        reason += job.AllowLossyFallback ? "兼容转换也未能完成。" : "输入编码无法无损封装到 MP4；可启用“允许有损兼容转换”，或保留 MKV 格式。";
                    throw new InvalidOperationException(reason + Environment.NewLine + Tail(conversion.Error, 4500));
                }
                token.ThrowIfCancellationRequested();
                if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                    throw new IOException("FFmpeg 未生成有效输出文件，原文件保持不变。");
                // File.Move refuses to replace a file created by another task while conversion was running.
                File.Move(temp, job.OutputPath);
                string summary = Summary(job, source, usedLossy);
                if (notes.Count > 0) summary += Environment.NewLine + String.Join(Environment.NewLine, notes.ToArray());
                Report(progress, 100, usedLossy ? "完成：已进行有损兼容转换" : "完成：无损转换成功", summary);
                return new ConversionResult { OutputPath = job.OutputPath, Summary = summary, UsedLossyEncoding = usedLossy };
            }
            finally
            {
                DeletePartial(temp);
            }
        }

        private static void Validate(MediaJob job, ToolPaths tools)
        {
            if (String.IsNullOrWhiteSpace(job.InputPath) || !File.Exists(job.InputPath))
                throw new FileNotFoundException("找不到输入文件，请重新添加文件。", job.InputPath);
            string required = job.Kind == MediaTaskKind.FlacToWav ? ".flac" : job.Kind == MediaTaskKind.MkvToMp4 ? ".mkv" : job.Kind == MediaTaskKind.MergeMp4Audio ? ".mp4" : null;
            if (required == null) throw new ArgumentException("未知的转换类型。");
            if (!String.Equals(Path.GetExtension(job.InputPath), required, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("此任务需要 " + required.ToUpperInvariant() + " 输入文件。");
            if (job.Kind == MediaTaskKind.MergeMp4Audio)
            {
                if (String.IsNullOrWhiteSpace(job.AudioPath) || !File.Exists(job.AudioPath))
                    throw new FileNotFoundException("请选择用于替换原音轨的 WAV 或 FLAC 文件。", job.AudioPath);
                string audioExtension = Path.GetExtension(job.AudioPath);
                if (!String.Equals(audioExtension, ".wav", StringComparison.OrdinalIgnoreCase) && !String.Equals(audioExtension, ".flac", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("外部音频仅支持 WAV 或 FLAC 文件。");
            }
            if (String.IsNullOrWhiteSpace(tools.FFmpegPath) || !File.Exists(tools.FFmpegPath) || String.IsNullOrWhiteSpace(tools.FFprobePath) || !File.Exists(tools.FFprobePath))
                throw new FileNotFoundException("FFmpeg 或 ffprobe 不存在，请检查工具文件夹设置。");
        }

        private static void ValidateOutput(MediaJob job)
        {
            if (!String.Equals(Path.GetExtension(job.OutputPath), ExtensionFor(job.Kind), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("输出文件扩展名应为 " + ExtensionFor(job.Kind) + "。");
            if (SamePath(job.InputPath, job.OutputPath) || (!String.IsNullOrWhiteSpace(job.AudioPath) && SamePath(job.AudioPath, job.OutputPath)))
                throw new IOException("输出文件不能覆盖任何输入文件。");
            if (!SamePath(Path.GetDirectoryName(job.InputPath), Path.GetDirectoryName(job.OutputPath)))
                throw new IOException("输出文件必须与主输入文件位于同一文件夹。");
            if (File.Exists(job.OutputPath) || Directory.Exists(job.OutputPath))
                throw new IOException("输出文件已存在，为避免覆盖，请使用新的文件名：" + job.OutputPath);
        }

        private static void ValidateStreams(MediaJob job, MediaInfo source, MediaInfo audio)
        {
            if (job.Kind == MediaTaskKind.FlacToWav)
            {
                if (source.Audio.Count == 0 || source.Audio[0].Codec != "flac")
                    throw new InvalidOperationException("输入文件没有可解码的 FLAC 音轨。");
                if (source.Audio[0].Bits <= 0 || source.Audio[0].Bits > 32)
                    throw new InvalidOperationException("无法确认 FLAC 的音频位深，已停止以避免降低音质。");
            }
            else if (source.Video.Count == 0) throw new InvalidOperationException("输入文件没有视频轨道。");
            if (audio != null)
            {
                if (audio.Audio.Count == 0) throw new InvalidOperationException("外部文件没有音频轨道。");
                foreach (StreamInfo stream in audio.Audio)
                    if (stream.Codec != "flac" && !IsLinearPcm(stream.Codec))
                        throw new InvalidOperationException("外部音频编码为 " + stream.Codec + "，不属于 FLAC 或线性 PCM 无损音频。请提供未压缩 WAV 或 FLAC。");
            }
        }

        private static List<string> BuildArguments(MediaJob job, MediaInfo source, MediaInfo audio, string target, bool lossy, List<string> notes, IProgress<MediaProgress> progress)
        {
            List<string> args = new List<string>(new string[] { "-hide_banner", "-nostdin", "-n", "-loglevel", "warning", "-progress", "pipe:1", "-nostats" });
            args.AddRange(new string[] { "-i", job.InputPath });
            if (job.Kind == MediaTaskKind.MergeMp4Audio) args.AddRange(new string[] { "-i", job.AudioPath });
            if (job.Kind == MediaTaskKind.FlacToWav)
            {
                int bits = source.Audio[0].Bits;
                string codec = bits <= 8 ? "pcm_u8" : bits <= 16 ? "pcm_s16le" : bits <= 24 ? "pcm_s24le" : "pcm_s32le";
                args.AddRange(new string[] { "-map", "0:a:0", "-map_metadata", "0", "-c:a", codec, "-rf64", "auto", "-f", "wav" });
            }
            else if (job.Kind == MediaTaskKind.MkvToMp4)
            {
                args.AddRange(new string[] { "-map", "0:v", "-map", "0:a?", "-map_metadata", "0", "-map_chapters", "0" });
                if (lossy)
                    args.AddRange(new string[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2", "-c:a", "aac", "-b:a", "256k" });
                else args.AddRange(new string[] { "-c", "copy" });
                args.AddRange(new string[] { "-movflags", "+faststart", "-f", "mp4" });
            }
            else
            {
                args.AddRange(new string[] { "-map", "0:v", "-map", "1:a", "-map_metadata", "0", "-map_chapters", "0", "-c:v", "copy", "-c:a", "copy" });
                if (job.CompressAudio)
                {
                    for (int i = 0; i < audio.Audio.Count; i++)
                    {
                        StreamInfo stream = audio.Audio[i];
                        if (stream.Codec == "flac") continue;
                        if (IsIntegerPcm(stream.Codec) && stream.Bits > 0 && stream.Bits <= 24 && stream.Channels <= 8)
                        {
                            string spec = ":a:" + i.ToString(Invariant);
                            args.AddRange(new string[] { "-c" + spec, "flac", "-compression_level" + spec, "0", "-sample_fmt" + spec, stream.Bits <= 16 ? "s16" : "s32" });
                        }
                        else
                        {
                            string note = "外部音轨 " + (i + 1).ToString(Invariant) + " 为浮点、超过 24 位或超过 8 声道的 PCM，已保持原样复制以完整保留精度。";
                            notes.Add(note);
                            Report(progress, null, note, note);
                        }
                    }
                }
                args.AddRange(new string[] { "-f", "matroska" });
            }
            args.Add(target);
            return args;
        }

        private static MediaInfo Probe(string path, ToolPaths tools, CancellationToken token)
        {
            ProcessResult result = RunProcess(tools.FFprobePath, new string[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", path }, token, null, null);
            if (result.ExitCode != 0) throw new InvalidOperationException("无法读取媒体信息：" + Path.GetFileName(path) + Environment.NewLine + Tail(result.Error, 2500));
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 64 * 1024 * 1024;
                Dictionary<string, object> root = serializer.Deserialize<Dictionary<string, object>>(result.Output);
                MediaInfo info = new MediaInfo();
                object formatObject;
                if (root.TryGetValue("format", out formatObject))
                    info.Duration = Number((Dictionary<string, object>)formatObject, "duration");
                object streamsObject;
                if (root.TryGetValue("streams", out streamsObject))
                {
                    foreach (object entry in (IEnumerable)streamsObject)
                    {
                        Dictionary<string, object> fields = (Dictionary<string, object>)entry;
                        StreamInfo stream = new StreamInfo { Codec = Value(fields, "codec_name"), Type = Value(fields, "codec_type"), SampleFormat = Value(fields, "sample_fmt"),
                            Bits = Integer(fields, "bits_per_raw_sample"), SampleRate = Integer(fields, "sample_rate"), Channels = Integer(fields, "channels") };
                        if (stream.Bits <= 0) stream.Bits = Integer(fields, "bits_per_sample");
                        if (stream.Bits <= 0) stream.Bits = BitsFromCodec(stream.Codec, stream.SampleFormat);
                        if (stream.Type == "audio") info.Audio.Add(stream);
                        if (stream.Type == "video") info.Video.Add(stream);
                        double streamDuration = Number(fields, "duration");
                        if (streamDuration > info.Duration) info.Duration = streamDuration;
                    }
                }
                return info;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("ffprobe 返回的媒体信息无法解析，请确认 FFmpeg 工具完整且版本正常。", ex);
            }
        }

        private static ProcessResult Convert(string executable, List<string> args, double duration, IProgress<MediaProgress> progress, CancellationToken token, bool lossy)
        {
            Report(progress, null, null, "FFmpeg " + JoinArguments(args));
            double lastPercent = -1;
            Stopwatch clock = Stopwatch.StartNew();
            long lastUpdate = -1000;
            return RunProcess(executable, args, token,
                delegate(string line)
                {
                    int separator = line.IndexOf('=');
                    if (separator < 0) return;
                    string key = line.Substring(0, separator);
                    double microseconds;
                    if ((key == "out_time_us" || key == "out_time_ms") && Double.TryParse(line.Substring(separator + 1), NumberStyles.Float, Invariant, out microseconds))
                    {
                        double? percent = duration > 0 ? (double?)Math.Min(99.5, Math.Max(0, microseconds / 1000000.0 / duration * 100.0)) : null;
                        if (clock.ElapsedMilliseconds - lastUpdate >= 150 || (percent.HasValue && percent.Value - lastPercent >= 1))
                        {
                            lastUpdate = clock.ElapsedMilliseconds;
                            if (percent.HasValue) lastPercent = Math.Max(lastPercent, percent.Value);
                            Report(progress, percent.HasValue ? (double?)lastPercent : null, lossy ? "正在进行有损兼容转换…" : "正在转换…", null);
                        }
                    }
                }, delegate(string line) { Report(progress, null, null, line); });
        }

        private static ProcessResult RunProcess(string executable, IEnumerable<string> args, CancellationToken token, Action<string> outputLine, Action<string> errorLine)
        {
            token.ThrowIfCancellationRequested();
            ProcessStartInfo start = new ProcessStartInfo { FileName = executable, Arguments = JoinArguments(args), UseShellExecute = false,
                CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();
            object outputLock = new object();
            object errorLock = new object();
            using (Process process = new Process { StartInfo = start })
            {
                process.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (outputLock)
                    {
                        output.AppendLine(e.Data);
                        if (outputLine != null && output.Length > 256 * 1024) output.Remove(0, output.Length - 128 * 1024);
                    }
                    if (outputLine != null) outputLine(e.Data);
                };
                process.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
                {
                    if (e.Data == null) return;
                    lock (errorLock)
                    {
                        error.AppendLine(e.Data);
                        if (error.Length > 128 * 1024) error.Remove(0, error.Length - 96 * 1024);
                    }
                    if (errorLine != null) errorLine(e.Data);
                };
                try
                {
                    if (!process.Start()) throw new InvalidOperationException("启动 FFmpeg 工具失败。");
                }
                catch (System.ComponentModel.Win32Exception ex)
                {
                    throw new InvalidOperationException("无法启动工具：" + executable + Environment.NewLine + ex.Message, ex);
                }
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                while (!process.WaitForExit(100))
                {
                    if (!token.IsCancellationRequested) continue;
                    TryKill(process);
                    process.WaitForExit();
                    token.ThrowIfCancellationRequested();
                }
                // The parameterless overload also waits for both asynchronous pipe readers.
                process.WaitForExit();
                token.ThrowIfCancellationRequested();
                return new ProcessResult { ExitCode = process.ExitCode, Output = output.ToString(), Error = error.ToString() };
            }
        }

        private static bool IsMp4CodecIncompatibility(string error)
        {
            string text = (error ?? String.Empty).ToLowerInvariant();
            string[] operationalErrors = { "permission denied", "no space left", "disk full", "input/output error", "no such file", "access is denied", "read-only file system", "file exists", "cannot allocate memory" };
            foreach (string phrase in operationalErrors) if (text.Contains(phrase)) return false;
            return text.Contains("not currently supported in container") || text.Contains("could not find tag for codec") ||
                text.Contains("codec not supported in container") || text.Contains("codec not currently supported") ||
                text.Contains("does not support codec") || text.Contains("not supported in the official mp4 specification");
        }

        public static string QuoteArgument(string argument)
        {
            // Implements the Windows CommandLineToArgvW / C runtime quoting rules.
            if (argument == null) argument = String.Empty;
            StringBuilder quoted = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { quoted.Append('\\', slashes * 2 + 1); quoted.Append('"'); slashes = 0; continue; }
                quoted.Append('\\', slashes);
                slashes = 0;
                quoted.Append(c);
            }
            quoted.Append('\\', slashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        private static string JoinArguments(IEnumerable<string> args)
        {
            List<string> quoted = new List<string>();
            foreach (string argument in args) quoted.Add(QuoteArgument(argument));
            return String.Join(" ", quoted.ToArray());
        }

        private static string Summary(MediaJob job, MediaInfo source, bool lossy)
        {
            if (job.Kind == MediaTaskKind.FlacToWav)
            {
                StreamInfo audio = source.Audio[0];
                int outputBits = audio.Bits <= 8 ? 8 : audio.Bits <= 16 ? 16 : audio.Bits <= 24 ? 24 : 32;
                return "FLAC → WAV 无损解码完成：" + audio.SampleRate.ToString(Invariant) + " Hz / " + audio.Channels.ToString(Invariant) + " 声道 / " + outputBits.ToString(Invariant) + " 位 PCM，保留原始采样精度。";
            }
            if (job.Kind == MediaTaskKind.MkvToMp4)
                return lossy ? "MKV → MP4 完成：已使用 H.264（CRF 18）和 AAC（256 kbps）进行有损兼容转换。" : "MKV → MP4 完成：所有视频、音频轨道直接复制，无重新编码；字幕和附件未写入 MP4。";
            return "MP4 + 外部音频 → MKV 完成：视频直接复制，外部音频无损保留，MP4 原音轨已排除。";
        }

        private static string InitialMessage(MediaTaskKind kind)
        {
            return kind == MediaTaskKind.FlacToWav ? "正在无损解码 FLAC…" : kind == MediaTaskKind.MkvToMp4 ? "正在尝试无损复制封装 MP4…" : "正在复制视频并合成外部无损音轨…";
        }

        private static string ExtensionFor(MediaTaskKind kind)
        {
            switch (kind)
            {
                case MediaTaskKind.FlacToWav: return ".wav";
                case MediaTaskKind.MkvToMp4: return ".mp4";
                case MediaTaskKind.MergeMp4Audio: return ".mkv";
                default: throw new ArgumentException("未知的转换类型。");
            }
        }

        private static bool IsLinearPcm(string codec)
        {
            return IsIntegerPcm(codec) || codec == "pcm_f32le" || codec == "pcm_f64le" || codec == "pcm_f32be" || codec == "pcm_f64be";
        }

        private static bool IsIntegerPcm(string codec)
        {
            return codec == "pcm_u8" || codec == "pcm_s8" || codec == "pcm_s16le" || codec == "pcm_s16be" || codec == "pcm_s24le" || codec == "pcm_s24be" ||
                codec == "pcm_s32le" || codec == "pcm_s32be" || codec == "pcm_s64le" || codec == "pcm_s64be" || codec == "pcm_u16le" || codec == "pcm_u16be" ||
                codec == "pcm_u24le" || codec == "pcm_u24be" || codec == "pcm_u32le" || codec == "pcm_u32be";
        }

        private static int BitsFromCodec(string codec, string sampleFormat)
        {
            if (codec != null && codec.StartsWith("pcm_", StringComparison.Ordinal))
            {
                string digits = String.Empty;
                foreach (char c in codec.Substring(5)) { if (Char.IsDigit(c)) digits += c; else break; }
                int bits;
                if (Int32.TryParse(digits, out bits)) return bits;
            }
            if (sampleFormat == "u8" || sampleFormat == "u8p") return 8;
            if (sampleFormat == "s16" || sampleFormat == "s16p") return 16;
            if (sampleFormat == "s32" || sampleFormat == "s32p" || sampleFormat == "flt" || sampleFormat == "fltp") return 32;
            if (sampleFormat == "s64" || sampleFormat == "s64p" || sampleFormat == "dbl" || sampleFormat == "dblp") return 64;
            return 0;
        }

        private static bool SamePath(string first, string second)
        {
            return String.Equals(Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        private static void DeletePartial(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void TryKill(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static void Report(IProgress<MediaProgress> progress, double? percent, string message, string log)
        {
            if (progress != null) progress.Report(new MediaProgress { Percent = percent, Message = message, LogLine = log });
        }

        private static string FormatDuration(double seconds)
        {
            return TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss\.fff", Invariant);
        }

        private static string Tail(string text, int length)
        {
            text = (text ?? String.Empty).Trim();
            return text.Length > length ? text.Substring(text.Length - length) : text;
        }

        private static string Value(Dictionary<string, object> fields, string key)
        {
            object value;
            return fields.TryGetValue(key, out value) && value != null ? System.Convert.ToString(value, Invariant) : String.Empty;
        }

        private static int Integer(Dictionary<string, object> fields, string key)
        {
            int number;
            return Int32.TryParse(Value(fields, key), NumberStyles.Integer, Invariant, out number) ? number : 0;
        }

        private static double Number(Dictionary<string, object> fields, string key)
        {
            double number;
            return Double.TryParse(Value(fields, key), NumberStyles.Float, Invariant, out number) && !Double.IsNaN(number) && !Double.IsInfinity(number) && number >= 0 ? number : 0;
        }

        private sealed class MediaInfo
        {
            public readonly List<StreamInfo> Audio = new List<StreamInfo>();
            public readonly List<StreamInfo> Video = new List<StreamInfo>();
            public double Duration;
        }

        private sealed class StreamInfo
        {
            public string Type;
            public string Codec;
            public string SampleFormat;
            public int Bits;
            public int SampleRate;
            public int Channels;
        }

        private sealed class ProcessResult
        {
            public int ExitCode;
            public string Output;
            public string Error;
        }
    }
}
