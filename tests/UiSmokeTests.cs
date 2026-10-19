using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using LosslessMedia;

internal static class UiSmokeTests
{
    private static int passed;
    private static string folder;
    private static string video;
    private static string wav;
    private static string flac;
    private static string avi;

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(folder);
        video = Fixture("视频 sample.MP4");
        wav = Fixture("音轨 sample.WAV");
        flac = Fixture("音轨 sample.FLAC");
        avi = Fixture("旧格式.avi");
        try
        {
            using (MainForm form = new MainForm(new string[0]))
            {
                Drop((Control)Field(form, "mainDrop"), video, wav);
                Assert((MediaTaskKind)Field(form, "mode") == MediaTaskKind.MergeMp4Audio, "MP4 drop switches the initial audio tile to merge mode");
                Assert((string)Field(form, "videoPath") == video && (string)Field(form, "audioPath") == wav, "MP4 and WAV are paired from one drop");
                Call(form, "AddMergeJob");
                AssertJob(form, wav);
                Pass("Drop MP4 and WAV on the initial tile, then queue lossless MKV");
            }
            using (MainForm form = new MainForm(new string[0]))
            {
                Call(form, "SetMode", MediaTaskKind.MergeMp4Audio);
                Drop((Control)Field(form, "audioDrop"), flac);
                Drop((Control)Field(form, "videoDrop"), video);
                Call(form, "AddMergeJob");
                AssertJob(form, flac);
                Pass("Separate audio-first FLAC and MP4 drops pair correctly");
            }
            using (MainForm form = new MainForm(new string[0]))
            {
                Drop((Control)Field(form, "mainDrop"), avi);
                Assert(((ListView)Field(form, "queue")).Items.Count == 0, "AVI must not create a batch task");
                Call(form, "SetMode", MediaTaskKind.MergeMp4Audio);
                Drop((Control)Field(form, "videoDrop"), avi);
                Assert(Field(form, "videoPath") == null, "AVI must not be accepted as merge video");
                Assert(!((Button)Field(form, "addMergeButton")).Enabled, "Missing MP4 keeps merge button disabled");
                Pass("AVI drops are rejected after the MP4 input correction");
            }
            using (MainForm form = new MainForm(new string[0]))
            {
                Drop(form, video);
                Assert((MediaTaskKind)Field(form, "mode") == MediaTaskKind.MergeMp4Audio, "Form drop must select MP4 merging");
                Assert(!((Button)Field(form, "addMergeButton")).Enabled, "MP4 alone waits for external audio");
                Drop((Control)Field(form, "audioDrop"), wav);
                Assert(((Button)Field(form, "addMergeButton")).Enabled, "External audio enables the merge task");
                Pass("Window drop routes MP4 and requires an external audio file");
            }
            Console.WriteLine(passed + " UI checks passed; 0 failed");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static string Fixture(string name)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, "UI routing fixture; media conversion is covered by integration tests.");
        return path;
    }

    private static void AssertJob(MainForm form, string audio)
    {
        ListView list = (ListView)Field(form, "queue");
        Assert(list.Items.Count == 1, "Exactly one paired job must be queued");
        MediaJob job = (MediaJob)Field(list.Items[0].Tag, "Job");
        Assert(job.Kind == MediaTaskKind.MergeMp4Audio && job.InputPath == video && job.AudioPath == audio, "Queued job must use MP4 and the chosen external audio");
        Assert(Path.GetDirectoryName(job.OutputPath) == folder && Path.GetExtension(job.OutputPath) == ".mkv", "MKV output belongs beside the MP4");
        Assert(list.Items[0].SubItems[1].Text == "MP4 + 音频 → MKV", "Queue must label the corrected operation");
    }

    private static object Field(object target, string name)
    {
        return target.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(target);
    }

    private static void Call(object target, string name, params object[] args)
    {
        target.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, args);
    }

    private static void Drop(Control target, params string[] paths)
    {
        DataObject data = new DataObject(DataFormats.FileDrop, paths);
        DragEventArgs args = new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy);
        typeof(Control).GetMethod("OnDragDrop", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(target, new object[] { args });
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pass(string message) { passed++; Console.WriteLine("PASS " + message); }
}
