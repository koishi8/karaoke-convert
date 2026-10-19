using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using LosslessMedia;

internal static class IntegrationTests
{
    private static string ffmpeg;
    private static string ffprobe;
    private static string artifacts;
    private static ToolPaths tools;
    private static int passed;
    private static int failed;
    private static readonly List<string> results = new List<string>();

    private static int Main(string[] args)
    {
        string helperMode = Environment.GetEnvironmentVariable("MEDIA_TEST_HELPER_MODE");
        if (!String.IsNullOrEmpty(helperMode) && args.Length > 0 && args[0].StartsWith("-", StringComparison.Ordinal))
            return RunHelper(args, helperMode);
        try
        {
            tools = ToolPaths.Resolve(args.Length > 0 && !String.IsNullOrWhiteSpace(args[0]) ? args[0] : null);
            ffmpeg = tools.FFmpegPath;
            ffprobe = tools.FFprobePath;
            string root = Path.GetDirectoryName(typeof(IntegrationTests).Assembly.Location);
            artifacts = Path.Combine(root, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(artifacts);
            Console.WriteLine("Artifacts: " + artifacts);
            RunAll().GetAwaiter().GetResult();
            string summary = String.Format(CultureInfo.InvariantCulture, "{0} passed; {1} failed", passed, failed);
            results.Add(summary);
            File.WriteAllLines(Path.Combine(artifacts, "results.txt"), results, Encoding.UTF8);
            Console.WriteLine(summary);
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
    }

    private static async Task RunAll()
    {
        await Case("FLAC 16-bit mono to WAV preserves all PCM samples", delegate { return FlacToWav(16, 1, 44100); });
        await Case("FLAC 24-bit stereo to WAV preserves all PCM samples", delegate { return FlacToWav(24, 2, 48000); });
        await Case("FLAC 32-bit stereo to WAV preserves all PCM samples", delegate { return FlacToWav(32, 2, 96000); });
        await Case("MKV to MP4 copies H.264 and AAC packet payloads", RemuxMkv);
        await Case("AVI plus WAV copies video and replaces original audio without truncation", MergeWav);
        await Case("AVI plus FLAC copies external compressed packet payloads", MergeFlac);
        await Case("Optional FLAC compression preserves 24-bit PCM samples", MergeCompressedWav);
        await Case("32-bit PCM remains exact with optional compression enabled", Merge32BitWav);
        await Case("Incompatible MKV reports failure when fallback is disabled", NoLossyFallback);
        await Case("Incompatible MKV uses explicitly allowed H.264 fallback", LossyFallback);
        await Case("Existing output is preserved byte for byte", CollisionSafety);
        await Case("Pre-cancellation creates no output", PreCancellation);
        await Case("Running cancellation kills child and cleans temporary output", RunningCancellation);
        await Case("Ordinary write errors do not trigger lossy fallback", ErrorDoesNotFallback);
    }

    private static async Task Case(string name, Func<Task> action)
    {
        try { await action(); passed++; results.Add("PASS " + name); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; results.Add("FAIL " + name + "\n" + ex); Console.WriteLine("FAIL " + name + "\n" + ex); }
    }

    private static async Task FlacToWav(int bits, int channels, int rate)
    {
        string source = P("音频 " + bits + " 位.flac");
        AudioFixture(source, bits, channels, rate, 0.83, true);
        Dictionary<string, object> input = AudioStream(source);
        Equal(bits, Int(input, "bits_per_raw_sample"), "Fixture bit depth");
        ConversionResult result = await Convert(MediaTaskKind.FlacToWav, source, null, null, false, false);
        True(File.Exists(result.OutputPath), "Output WAV exists");
        Equal(Path.GetDirectoryName(source), Path.GetDirectoryName(result.OutputPath), "Output directory");
        Equal(".wav", Path.GetExtension(result.OutputPath).ToLowerInvariant(), "Output extension");
        Dictionary<string, object> output = AudioStream(result.OutputPath);
        Equal("pcm_s" + bits + "le", Str(output, "codec_name"), "PCM codec");
        Equal(rate, Int(output, "sample_rate"), "Sample rate");
        Equal(channels, Int(output, "channels"), "Channel count");
        Equal(AudioHash(source), AudioHash(result.OutputPath), "Decoded PCM SHA-256");
    }

    private static async Task RemuxMkv()
    {
        string input = P("源 视频 H264 AAC.mkv");
        Media(input, "testsrc2=size=160x90:rate=25:duration=1.1", "sine=frequency=523:sample_rate=48000:duration=1.1", "libx264", "aac");
        ConversionResult result = await Convert(MediaTaskKind.MkvToMp4, input, null, null, false, true);
        Equal("h264", Str(VideoStream(result.OutputPath), "codec_name"), "Video codec");
        Equal("aac", Str(AudioStream(result.OutputPath), "codec_name"), "Audio codec");
        Equal(PacketHashes(input, "v:0"), PacketHashes(result.OutputPath, "v:0"), "Video packet SHA-256 sequence");
        Equal(PacketHashes(input, "a:0"), PacketHashes(result.OutputPath, "a:0"), "Audio packet SHA-256 sequence");
        True(!result.UsedLossyEncoding, "Supported remux must not transcode");
    }

    private static string EnsureAvi()
    {
        string path = P("原始 歌曲.avi");
        if (!File.Exists(path)) Media(path, "testsrc2=size=160x90:rate=25:duration=1.0", "sine=frequency=211:sample_rate=48000:duration=1.0", "mpeg4", "pcm_s16le");
        return path;
    }

    private static async Task MergeWav()
    {
        string video = EnsureAvi();
        string audio = P("替换 长音轨 24 位.wav");
        AudioFixture(audio, 24, 2, 48000, 2.73, false);
        ConversionResult result = await Convert(MediaTaskKind.MergeAviAudio, video, audio, P("合成 PCM.mkv"), false, false);
        Equal(PacketHashes(video, "v:0"), PacketHashes(result.OutputPath, "v:0"), "AVI compressed video packet sequence");
        Equal(AudioHash(audio), AudioHash(result.OutputPath), "External audio PCM SHA-256 including full tail");
        Equal(1, Streams(result.OutputPath).Count(s => Str(s, "codec_type") == "audio"), "Output audio stream count");
        True(AudioHash(video) != AudioHash(result.OutputPath), "Original AVI audio must be excluded");
        True(Duration(result.OutputPath) >= 2.7, "Output must retain long external audio");
        Equal("pcm_s24le", Str(AudioStream(result.OutputPath), "codec_name"), "WAV codec copied");
    }

    private static async Task MergeFlac()
    {
        string audio = P("替换 无损.flac");
        AudioFixture(audio, 24, 2, 48000, 1.79, true);
        ConversionResult result = await Convert(MediaTaskKind.MergeAviAudio, EnsureAvi(), audio, P("合成 FLAC.mkv"), false, false);
        Equal(PacketHashes(audio, "a:0"), PacketHashes(result.OutputPath, "a:0"), "External FLAC compressed packet sequence");
        Equal(AudioHash(audio), AudioHash(result.OutputPath), "External FLAC PCM SHA-256");
        Equal("flac", Str(AudioStream(result.OutputPath), "codec_name"), "FLAC codec copied");
    }

    private static async Task MergeCompressedWav()
    {
        string audio = P("替换 长音轨 24 位.wav");
        if (!File.Exists(audio)) AudioFixture(audio, 24, 2, 48000, 2.73, false);
        ConversionResult result = await Convert(MediaTaskKind.MergeAviAudio, EnsureAvi(), audio, P("合成 压缩.mkv"), true, false);
        Equal("flac", Str(AudioStream(result.OutputPath), "codec_name"), "Optional compression uses FLAC");
        Equal(AudioHash(audio), AudioHash(result.OutputPath), "Compressed 24-bit PCM SHA-256");
    }

    private static async Task Merge32BitWav()
    {
        string audio = P("替换 32 位.wav");
        AudioFixture(audio, 32, 2, 96000, 1.31, false);
        ConversionResult result = await Convert(MediaTaskKind.MergeAviAudio, EnsureAvi(), audio, P("合成 32 位.mkv"), true, false);
        Equal("pcm_s32le", Str(AudioStream(result.OutputPath), "codec_name"), "32-bit PCM must remain lossless");
        Equal(AudioHash(audio), AudioHash(result.OutputPath), "32-bit PCM SHA-256");
    }

    private static string EnsureIncompatibleMkv()
    {
        string input = P("不兼容 MSMPEG4 FLAC.mkv");
        if (!File.Exists(input)) Media(input, "testsrc2=size=160x90:rate=25:duration=0.9", "sine=frequency=633:sample_rate=48000:duration=0.9", "msmpeg4v3", "flac");
        return input;
    }

    private static async Task NoLossyFallback()
    {
        string output = P("禁止有损.mp4");
        bool failedAsExpected = false;
        try { await Convert(MediaTaskKind.MkvToMp4, EnsureIncompatibleMkv(), null, output, false, false); }
        catch (Exception ex) { True(!(ex is OperationCanceledException), "Expected conversion failure"); failedAsExpected = true; }
        True(failedAsExpected, "MSMPEG4v3 cannot copy into MP4");
        True(!File.Exists(output), "Failed copy leaves no completed output");
    }

    private static async Task LossyFallback()
    {
        ConversionResult result = await Convert(MediaTaskKind.MkvToMp4, EnsureIncompatibleMkv(), null, P("允许有损.mp4"), false, true);
        Equal("h264", Str(VideoStream(result.OutputPath), "codec_name"), "Fallback video is H.264");
        True(result.UsedLossyEncoding, "Fallback must report lossy encoding");
        True(!String.IsNullOrWhiteSpace(result.Summary), "Fallback summary is present");
        True(Duration(result.OutputPath) >= 0.85, "Fallback retains media duration");
    }

    private static async Task CollisionSafety()
    {
        string input = P("音频 16 位.flac");
        if (!File.Exists(input)) AudioFixture(input, 16, 1, 44100, 0.83, true);
        string output = P("已有 输出.wav");
        byte[] sentinel = Encoding.UTF8.GetBytes("不要覆盖已有文件：preserve me");
        File.WriteAllBytes(output, sentinel);
        try
        {
            ConversionResult result = await Convert(MediaTaskKind.FlacToWav, input, null, output, false, false);
            True(!String.Equals(result.OutputPath, output, StringComparison.OrdinalIgnoreCase), "Successful collision resolution uses a new filename");
            Equal(AudioHash(input), AudioHash(result.OutputPath), "Renamed output content");
        }
        catch (IOException) { }
        catch (InvalidOperationException) { }
        Equal(System.Convert.ToBase64String(sentinel), System.Convert.ToBase64String(File.ReadAllBytes(output)), "Preexisting output bytes");
    }

    private static async Task PreCancellation()
    {
        string output = P("预取消.wav");
        using (CancellationTokenSource source = new CancellationTokenSource())
        {
            source.Cancel();
            bool cancelled = false;
            try { await new MediaEngine().RunAsync(new MediaJob { Kind = MediaTaskKind.FlacToWav, InputPath = P("音频 16 位.flac"), OutputPath = output }, tools, null, source.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            True(cancelled, "Already cancelled token must cancel the operation");
        }
        True(!File.Exists(output), "Pre-cancellation creates no output");
    }

    private static async Task RunningCancellation()
    {
        string dir = P("cancel"); Directory.CreateDirectory(dir);
        string input = Path.Combine(dir, "输入.flac");
        File.Copy(P("音频 16 位.flac"), input);
        string output = Path.Combine(dir, "输出.wav");
        string marker = Path.Combine(dir, "invocations.txt");
        Environment.SetEnvironmentVariable("MEDIA_TEST_HELPER_MODE", "slow");
        Environment.SetEnvironmentVariable("MEDIA_TEST_HELPER_MARKER", marker);
        try
        {
            ToolPaths helper = new ToolPaths { FFmpegPath = typeof(IntegrationTests).Assembly.Location, FFprobePath = ffprobe };
            using (CancellationTokenSource source = new CancellationTokenSource())
            {
                Task<ConversionResult> task = new MediaEngine().RunAsync(new MediaJob { Kind = MediaTaskKind.FlacToWav, InputPath = input, OutputPath = output, AllowLossyFallback = true }, helper, null, source.Token);
                DateTime deadline = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(marker) && DateTime.UtcNow < deadline && !task.IsCompleted) await Task.Delay(20);
                True(File.Exists(marker), "Simulated FFmpeg child actually started");
                source.Cancel();
                bool cancelled = false;
                Task completed = await Task.WhenAny(task, Task.Delay(10000));
                True(completed == task, "Cancellation must finish within 10 seconds");
                try { await task; } catch (OperationCanceledException) { cancelled = true; }
                True(cancelled, "Running operation reports cancellation");
            }
            Equal(1, File.ReadAllLines(marker).Length, "Cancellation does not launch fallback");
            string[] unexpected = Directory.GetFiles(dir).Where(f => f != input && f != marker).ToArray();
            Equal(0, unexpected.Length, "Cancelled operation removes all partial output: " + String.Join(",", unexpected));
        }
        finally { Environment.SetEnvironmentVariable("MEDIA_TEST_HELPER_MODE", null); Environment.SetEnvironmentVariable("MEDIA_TEST_HELPER_MARKER", null); }
    }

    private static async Task ErrorDoesNotFallback()
    {
        string dir = P("error"); Directory.CreateDirectory(dir);
        string marker = Path.Combine(dir, "invocations.txt");
        string input = Path.Combine(dir, "输入.mkv");
        File.Copy(EnsureIncompatibleMkv(), input);
        Environment.SetEnvironmentVariable("MEDIA_TEST_HELPER_MODE", "permission_error");
        Environment.SetEnvironmentVariable("MEDIA_TEST_HELPER_MARKER", marker);
        try
        {
            ToolPaths helper = new ToolPaths { FFmpegPath = typeof(IntegrationTests).Assembly.Location, FFprobePath = ffprobe };
            bool failure = false;
            try { await new MediaEngine().RunAsync(new MediaJob { Kind = MediaTaskKind.MkvToMp4, InputPath = input, OutputPath = Path.Combine(dir, "输出.mp4"), AllowLossyFallback = true }, helper, null, CancellationToken.None); }
            catch (Exception ex) { True(!(ex is OperationCanceledException), "Write failure is not cancellation"); failure = true; }
            True(failure, "Write error reports failure");
            True(File.Exists(marker), "Simulated FFmpeg child ran");
            Equal(1, File.ReadAllLines(marker).Length, "Permission errors must not trigger fallback");
            Equal(2, Directory.GetFiles(dir).Length, "Write error leaves no partial output");
        }
        finally { Environment.SetEnvironmentVariable("MEDIA_TEST_HELPER_MODE", null); Environment.SetEnvironmentVariable("MEDIA_TEST_HELPER_MARKER", null); }
    }

    private static int RunHelper(string[] args, string mode)
    {
        string marker = Environment.GetEnvironmentVariable("MEDIA_TEST_HELPER_MARKER");
        if (!String.IsNullOrEmpty(marker)) File.AppendAllText(marker, Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + Environment.NewLine);
        string output = args[args.Length - 1];
        if (!output.StartsWith("-", StringComparison.Ordinal) && output != "pipe:1") File.WriteAllBytes(output, new byte[1024]);
        if (mode == "permission_error") { Console.Error.WriteLine("Error opening output file: Permission denied"); return 1; }
        Console.WriteLine("out_time_us=100000"); Console.WriteLine("progress=continue"); Console.Out.Flush();
        while (true) Thread.Sleep(200);
    }

    private static Task<ConversionResult> Convert(MediaTaskKind kind, string input, string audio, string output, bool compress, bool allowLossy)
    {
        return new MediaEngine().RunAsync(new MediaJob { Kind = kind, InputPath = input, AudioPath = audio, OutputPath = output, CompressAudio = compress, AllowLossyFallback = allowLossy }, tools, null, CancellationToken.None);
    }

    private static void AudioFixture(string path, int bits, int channels, int rate, double seconds, bool flac)
    {
        string expression = channels == 1 ? "0.37*sin(2*PI*437*t)" : "0.31*sin(2*PI*437*t)|0.21*sin(2*PI*613*t)";
        string source = "aevalsrc=" + expression + ":s=" + rate + ":d=" + seconds.ToString(CultureInfo.InvariantCulture);
        List<string> args = new List<string> { "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", source };
        if (flac) args.AddRange(new[] { "-c:a", "flac", "-sample_fmt", bits == 16 ? "s16" : "s32", "-bits_per_raw_sample", bits.ToString(CultureInfo.InvariantCulture), "-strict", "experimental" });
        else args.AddRange(new[] { "-c:a", "pcm_s" + bits + "le" });
        args.Add(path); Run(ffmpeg, args.ToArray());
    }

    private static void Media(string path, string video, string audio, string videoCodec, string audioCodec)
    {
        Run(ffmpeg, "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", video, "-f", "lavfi", "-i", audio, "-c:v", videoCodec, "-c:a", audioCodec, path);
    }

    private static string AudioHash(string path) { return Run(ffmpeg, "-hide_banner", "-v", "error", "-i", path, "-map", "0:a:0", "-c:a", "pcm_s32le", "-f", "hash", "-hash", "sha256", "-").Trim(); }
    private static string PacketHashes(string path, string selector) { return Run(ffprobe, "-v", "error", "-select_streams", selector, "-show_packets", "-show_data_hash", "sha256", "-show_entries", "packet=data_hash", "-of", "csv=p=0", path).Trim(); }
    private static List<Dictionary<string, object>> Streams(string path)
    {
        string json = Run(ffprobe, "-v", "error", "-show_streams", "-of", "json", path);
        Dictionary<string, object> doc = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
        return ((IEnumerable)doc["streams"]).Cast<Dictionary<string, object>>().ToList();
    }
    private static Dictionary<string, object> AudioStream(string path) { return Streams(path).First(s => Str(s, "codec_type") == "audio"); }
    private static Dictionary<string, object> VideoStream(string path) { return Streams(path).First(s => Str(s, "codec_type") == "video"); }
    private static double Duration(string path) { return Double.Parse(Run(ffprobe, "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path).Trim(), CultureInfo.InvariantCulture); }
    private static string Str(Dictionary<string, object> dict, string key) { object value; return dict.TryGetValue(key, out value) ? System.Convert.ToString(value, CultureInfo.InvariantCulture) : ""; }
    private static int Int(Dictionary<string, object> dict, string key) { return Int32.Parse(Str(dict, key), CultureInfo.InvariantCulture); }
    private static string P(string file) { return Path.Combine(artifacts, file); }
    private static void True(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual, string message) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception(message + ": expected " + expected + "; actual " + actual); }

    private static string Run(string program, params string[] args)
    {
        using (Process process = new Process())
        {
            process.StartInfo = new ProcessStartInfo(program, String.Join(" ", args.Select(Quote))) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            process.Start();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30000)) { process.Kill(); throw new Exception("External tool timed out: " + program); }
            string output = stdout.GetAwaiter().GetResult(); string error = stderr.GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new Exception("External tool failed (" + process.ExitCode + "): " + error);
            return output;
        }
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && !value.Any(c => Char.IsWhiteSpace(c) || c == '"')) return value;
        StringBuilder result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '"') { result.Append('\\', slashes * 2 + 1); result.Append(c); slashes = 0; continue; }
            result.Append('\\', slashes); slashes = 0; result.Append(c);
        }
        result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
    }
}
