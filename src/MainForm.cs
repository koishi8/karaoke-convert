using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LosslessMedia
{
    public sealed class MainForm : Form
    {
        private static readonly Color Ink = Color.FromArgb(29, 39, 59);
        private static readonly Color Muted = Color.FromArgb(105, 118, 139);
        private static readonly Color Accent = Color.FromArgb(43, 95, 217);
        private readonly List<QueueEntry> jobs = new List<QueueEntry>();
        private readonly StringBuilder logs = new StringBuilder();
        private readonly ModeButton[] modes = new ModeButton[3];
        private readonly Panel inputHost = new Panel();
        private readonly Label description = new Label();
        private readonly Label toolsLabel = new Label();
        private readonly Label footer = new Label();
        private readonly Label currentLabel = new Label();
        private readonly Label overallLabel = new Label();
        private readonly ListView queue = new ListView();
        private readonly ProgressBar progressBar = new ProgressBar();
        private readonly Button startButton = MakeButton("开始处理", true);
        private readonly Button cancelButton = MakeButton("取消当前批次", false);
        private readonly CheckBox compressBox = new CheckBox();
        private readonly CheckBox fallbackBox = new CheckBox();
        private readonly Button addMergeButton = MakeButton("添加合成任务", true);
        private readonly ToolTip tips = new ToolTip();
        private DropTile mainDrop;
        private DropTile videoDrop;
        private DropTile audioDrop;
        private string videoPath;
        private string audioPath;
        private string toolFolder;
        private ToolPaths toolPaths;
        private MediaTaskKind mode;
        private bool running;
        private bool closeWhenStopped;
        private CancellationTokenSource cancellation;
        private readonly string[] initialPaths;

        public MainForm(string[] args)
        {
            initialPaths = args;
            Text = "KaraokeConvert · FFmpeg 桌面工具";
            Font = new Font("Microsoft YaHei UI", 9F);
            ForeColor = Ink;
            BackColor = Color.FromArgb(245, 247, 251);
            ClientSize = new Size(1100, 710);
            MinimumSize = new Size(930, 650);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            AllowDrop = true;
            DragEnter += FilesDragEnter;
            DragDrop += delegate(object sender, DragEventArgs e) { ImportPaths(GetFiles(e)); };
            BuildLayout();
            LoadSettings();
            ResolveTools();
            SetMode(MediaTaskKind.FlacToWav);
            Shown += delegate
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
                if (Width > area.Width || Height > area.Height)
                    Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
                Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), area.Top + Math.Max(0, (area.Height - Height) / 2));
                if (initialPaths.Length > 0) ImportPaths(initialPaths);
            };
            FormClosing += OnClosing;
        }

        private void BuildLayout()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(26, 14, 26, 10);
            root.ColumnCount = 1;
            root.RowCount = 6;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 83));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 224));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 101));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            Controls.Add(root);

            TableLayoutPanel header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 244));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            Label title = new Label { Text = "KaraokeConvert", Font = new Font(Font.FontFamily, 23, FontStyle.Bold), Dock = DockStyle.Fill, AutoSize = false, Margin = Padding.Empty };
            Label subtitle = new Label { Text = "本地处理  /  拖入即读  /  原目录输出", ForeColor = Muted, Dock = DockStyle.Fill, Margin = new Padding(2, 4, 0, 0) };
            Button configure = MakeButton("设置 FFmpeg 位置", false);
            configure.Size = new Size(164, 33);
            configure.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            configure.Click += ChooseTools;
            toolsLabel.Dock = DockStyle.Fill;
            toolsLabel.TextAlign = ContentAlignment.MiddleRight;
            toolsLabel.Font = new Font(Font.FontFamily, 8.5F);
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(subtitle, 0, 1);
            header.Controls.Add(configure, 1, 0);
            header.Controls.Add(toolsLabel, 1, 1);
            root.Controls.Add(header, 0, 0);

            TableLayoutPanel modeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 0, 0, 8) };
            string[] titles = { "FLAC  →  WAV", "MKV  →  MP4", "MP4  +  音频  →  MKV" };
            string[] subs = { "保留采样率、声道和位深", "优先极速封装，可选兼容转码", "替换原音轨，保留无损音质" };
            for (int i = 0; i < 3; i++)
            {
                modeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.333F));
                ModeButton button = new ModeButton(titles[i], subs[i]);
                button.Dock = DockStyle.Fill;
                button.Margin = new Padding(i == 0 ? 0 : 6, 0, i == 2 ? 0 : 6, 0);
                int index = i;
                button.Click += delegate { SetMode((MediaTaskKind)index); };
                modes[i] = button;
                modeRow.Controls.Add(button, i, 0);
            }
            root.Controls.Add(modeRow, 0, 1);

            TableLayoutPanel input = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, 0, 12) };
            input.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            input.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            input.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            description.Dock = DockStyle.Fill;
            description.ForeColor = Muted;
            description.Font = new Font(Font.FontFamily, 9F);
            description.TextAlign = ContentAlignment.MiddleLeft;
            input.Controls.Add(description, 0, 0);
            inputHost.Dock = DockStyle.Fill;
            inputHost.Margin = Padding.Empty;
            input.Controls.Add(inputHost, 0, 1);
            Panel options = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            compressBox.Text = "无损压缩音轨为 FLAC（更省空间，速度稍慢）";
            compressBox.AutoSize = true;
            compressBox.Location = new Point(0, 12);
            fallbackBox.Text = "允许兼容转码：不兼容时转 H.264 / AAC（会有损，速度较慢）";
            fallbackBox.AutoSize = true;
            fallbackBox.Checked = true;
            fallbackBox.Location = new Point(0, 12);
            fallbackBox.CheckedChanged += delegate
            {
                foreach (QueueEntry entry in jobs.Where(e => e.State == "等待" && e.Job.Kind == MediaTaskKind.MkvToMp4))
                    entry.Job.AllowLossyFallback = fallbackBox.Checked;
            };
            addMergeButton.Size = new Size(145, 34);
            addMergeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            options.Resize += delegate { addMergeButton.Location = new Point(options.ClientSize.Width - 145, 5); };
            addMergeButton.Click += delegate { AddMergeJob(); };
            options.Controls.Add(compressBox);
            options.Controls.Add(fallbackBox);
            options.Controls.Add(addMergeButton);
            input.Controls.Add(options, 0, 2);
            root.Controls.Add(input, 0, 2);

            TableLayoutPanel queueArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            queueArea.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            queueArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Panel toolbar = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            Label queueTitle = new Label { Text = "任务队列", Font = new Font(Font.FontFamily, 11F, FontStyle.Bold), Location = new Point(0, 6), AutoSize = true };
            FlowLayoutPanel actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            Button remove = MakeButton("移除所选", false);
            Button clear = MakeButton("清空已结束", false);
            Button log = MakeButton("查看日志", false);
            foreach (Button button in new[] { remove, clear, log }) { button.Size = new Size(100, 30); button.Margin = new Padding(6, 0, 0, 0); actions.Controls.Add(button); }
            remove.Click += delegate { RemoveSelected(); };
            clear.Click += delegate { ClearFinished(); };
            log.Click += delegate { ShowLog(); };
            toolbar.Controls.Add(queueTitle);
            toolbar.Controls.Add(actions);
            queueArea.Controls.Add(toolbar, 0, 0);
            queue.Dock = DockStyle.Fill;
            queue.Margin = Padding.Empty;
            queue.View = View.Details;
            queue.FullRowSelect = true;
            queue.HideSelection = false;
            queue.BorderStyle = BorderStyle.FixedSingle;
            queue.BackColor = Color.White;
            queue.Font = new Font(Font.FontFamily, 9F);
            queue.Columns.Add("状态", 108);
            queue.Columns.Add("操作", 140);
            queue.Columns.Add("源文件", 250);
            queue.Columns.Add("输出文件（原目录）", 350);
            queue.ShowItemToolTips = true;
            queue.DoubleClick += delegate { OpenSelectedOutput(); };
            queue.Resize += delegate { if (queue.ClientSize.Width > 550) queue.Columns[3].Width = Math.Max(230, queue.ClientSize.Width - 506); };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开输出所在文件夹", null, delegate { OpenSelectedOutput(); });
            menu.Items.Add("复制任务详情", null, delegate { CopySelected(); });
            menu.Items.Add("移除所选任务", null, delegate { RemoveSelected(); });
            queue.ContextMenuStrip = menu;
            queueArea.Controls.Add(queue, 0, 1);
            root.Controls.Add(queueArea, 0, 3);

            TableLayoutPanel bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = new Padding(0, 10, 0, 0) };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 310));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            currentLabel.Text = "准备就绪，添加文件后即可开始";
            currentLabel.AutoEllipsis = true;
            currentLabel.Dock = DockStyle.Fill;
            currentLabel.TextAlign = ContentAlignment.MiddleLeft;
            currentLabel.Margin = Padding.Empty;
            progressBar.Dock = DockStyle.Fill;
            progressBar.Height = 13;
            progressBar.Margin = new Padding(0, 3, 0, 3);
            overallLabel.Dock = DockStyle.Fill;
            overallLabel.ForeColor = Muted;
            overallLabel.Margin = new Padding(0, 5, 0, 0);
            FlowLayoutPanel runActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            startButton.Size = new Size(148, 43);
            cancelButton.Size = new Size(143, 43);
            startButton.Margin = new Padding(7, 3, 0, 0);
            cancelButton.Margin = new Padding(0, 3, 0, 0);
            cancelButton.Enabled = false;
            startButton.Click += async delegate { await RunQueueAsync(); };
            cancelButton.Click += delegate { CancelBatch(); };
            runActions.Controls.Add(startButton);
            runActions.Controls.Add(cancelButton);
            bottom.Controls.Add(currentLabel, 0, 0);
            bottom.Controls.Add(progressBar, 0, 1);
            bottom.Controls.Add(overallLabel, 0, 2);
            bottom.Controls.Add(runActions, 1, 0);
            bottom.SetRowSpan(runActions, 3);
            root.Controls.Add(bottom, 0, 4);
            footer.Dock = DockStyle.Fill;
            footer.ForeColor = Muted;
            footer.Font = new Font(Font.FontFamily, 8F);
            footer.Text = "原文件不会被修改  ·  重名自动编号  ·  双击任务可打开输出位置";
            footer.TextAlign = ContentAlignment.MiddleLeft;
            root.Controls.Add(footer, 0, 5);
        }

        private void SetMode(MediaTaskKind newMode)
        {
            mode = newMode;
            for (int i = 0; i < modes.Length; i++) { modes[i].Selected = i == (int)mode; modes[i].Invalidate(); }
            foreach (Control control in inputHost.Controls.Cast<Control>().ToArray()) control.Dispose();
            inputHost.Controls.Clear();
            compressBox.Visible = addMergeButton.Visible = mode == MediaTaskKind.MergeMp4Audio;
            fallbackBox.Visible = mode == MediaTaskKind.MkvToMp4;
            if (mode == MediaTaskKind.MergeMp4Audio)
            {
                description.Text = "直接复制 MP4 视频，仅使用外部音轨；不裁剪两者时长。输出到 MP4 所在目录。";
                TableLayoutPanel pair = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
                pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                videoDrop = new DropTile("拖入 MP4 视频", "或点击选择视频", false);
                audioDrop = new DropTile("拖入 WAV / FLAC 音频", "原视频中的音轨将被忽略", false);
                videoDrop.Margin = new Padding(0, 0, 7, 0);
                audioDrop.Margin = new Padding(7, 0, 0, 0);
                videoDrop.Dock = audioDrop.Dock = DockStyle.Fill;
                videoDrop.FilesDropped += delegate(string[] files) { SetMergeFiles(files); };
                audioDrop.FilesDropped += delegate(string[] files) { SetMergeFiles(files); };
                videoDrop.Click += delegate { BrowseMerge(true); };
                audioDrop.Click += delegate { BrowseMerge(false); };
                pair.Controls.Add(videoDrop, 0, 0);
                pair.Controls.Add(audioDrop, 1, 0);
                inputHost.Controls.Add(pair);
                UpdateMergeTiles();
            }
            else
            {
                bool flac = mode == MediaTaskKind.FlacToWav;
                description.Text = flac ? "FLAC 无损解码为 PCM WAV，保留源位深、采样率和声道。大文件自动使用 RF64。" : "优先复制全部视频和音轨；MP4 不保留 MKV 字幕与附件。可允许不兼容时进行有损转码。";
                mainDrop = new DropTile(flac ? "将 FLAC 文件拖到这里" : "将 MKV 文件拖到这里", "也可点击选择文件  ·  支持批量导入", true);
                mainDrop.Dock = DockStyle.Fill;
                mainDrop.FilesDropped += delegate(string[] files) { ImportPaths(files); };
                mainDrop.Click += delegate { BrowseBatch(); };
                inputHost.Controls.Add(mainDrop);
            }
        }

        private void BrowseBatch()
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "添加待处理文件";
                dialog.Filter = mode == MediaTaskKind.FlacToWav ? "FLAC 音频 (*.flac)|*.flac" : "MKV 视频 (*.mkv)|*.mkv";
                dialog.Multiselect = true;
                if (dialog.ShowDialog(this) == DialogResult.OK) AddBatch(dialog.FileNames);
            }
        }

        private void BrowseMerge(bool video)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = video ? "选择 MP4 视频" : "选择外部音轨";
                dialog.Filter = video ? "MP4 视频 (*.mp4)|*.mp4" : "无损音频 (*.wav;*.flac)|*.wav;*.flac";
                if (dialog.ShowDialog(this) == DialogResult.OK) SetMergeFiles(dialog.FileNames);
            }
        }

        private void ImportPaths(string[] files)
        {
            if (files == null || files.Length == 0) return;
            bool containsMp4 = files.Any(p => string.Equals(Path.GetExtension(p), ".mp4", StringComparison.OrdinalIgnoreCase));
            if (containsMp4 || mode == MediaTaskKind.MergeMp4Audio)
            {
                SetMode(MediaTaskKind.MergeMp4Audio);
                SetMergeFiles(files);
            }
            else
            {
                string ext = Path.GetExtension(files[0]).ToLowerInvariant();
                if (ext == ".flac") SetMode(MediaTaskKind.FlacToWav);
                else if (ext == ".mkv") SetMode(MediaTaskKind.MkvToMp4);
                AddBatch(files);
            }
        }

        private void AddBatch(string[] files)
        {
            if (mode == MediaTaskKind.MergeMp4Audio) { SetMergeFiles(files); return; }
            string expected = mode == MediaTaskKind.FlacToWav ? ".flac" : ".mkv";
            int added = 0;
            int rejected = 0;
            foreach (string file in files)
            {
                if (!File.Exists(file) || !string.Equals(Path.GetExtension(file), expected, StringComparison.OrdinalIgnoreCase)) { rejected++; continue; }
                if (AddJob(new MediaJob { Kind = mode, InputPath = Path.GetFullPath(file), AllowLossyFallback = fallbackBox.Checked })) added++;
            }
            overallLabel.Text = "已添加 " + added + " 个任务" + (rejected > 0 ? "；已跳过 " + rejected + " 个格式不符的项目" : "");
            if (rejected > 0) AppendLog("当前模式仅接受 " + expected + " 文件，目录请展开后拖入文件。");
            if (mainDrop != null && !mainDrop.IsDisposed) mainDrop.Detail = "已添加 " + added + " 个任务  ·  可继续拖入";
        }

        private void SetMergeFiles(string[] files)
        {
            string[] videos = files.Where(p => File.Exists(p) && Path.GetExtension(p).Equals(".mp4", StringComparison.OrdinalIgnoreCase)).ToArray();
            string[] audios = files.Where(p => File.Exists(p) && (Path.GetExtension(p).Equals(".wav", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".flac", StringComparison.OrdinalIgnoreCase))).ToArray();
            if (videos.Length > 1 || audios.Length > 1)
            {
                MessageBox.Show(this, "一次请选一个 MP4 和一个外部音频文件，避免配错音轨。可分别添加多组合成任务。", "选择合成文件", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (videos.Length == 1) videoPath = Path.GetFullPath(videos[0]);
            if (audios.Length == 1) audioPath = Path.GetFullPath(audios[0]);
            if (videos.Length + audios.Length == 0) overallLabel.Text = "请拖入 MP4 视频或 WAV / FLAC 音频文件。";
            UpdateMergeTiles();
        }

        private void UpdateMergeTiles()
        {
            if (videoDrop != null && !videoDrop.IsDisposed)
            {
                videoDrop.Title = videoPath == null ? "拖入 MP4 视频" : Path.GetFileName(videoPath);
                videoDrop.Detail = videoPath == null ? "或点击选择视频" : videoPath;
                tips.SetToolTip(videoDrop, videoPath);
            }
            if (audioDrop != null && !audioDrop.IsDisposed)
            {
                audioDrop.Title = audioPath == null ? "拖入 WAV / FLAC 音频" : Path.GetFileName(audioPath);
                audioDrop.Detail = audioPath == null ? "原视频中的音轨将被忽略" : audioPath;
                tips.SetToolTip(audioDrop, audioPath);
            }
            addMergeButton.Enabled = !running && videoPath != null && audioPath != null;
        }

        private void AddMergeJob()
        {
            if (videoPath == null || audioPath == null) return;
            if (AddJob(new MediaJob { Kind = MediaTaskKind.MergeMp4Audio, InputPath = videoPath, AudioPath = audioPath, CompressAudio = compressBox.Checked }))
            {
                overallLabel.Text = "合成任务已加入队列";
                videoPath = audioPath = null;
                UpdateMergeTiles();
            }
        }

        private bool AddJob(MediaJob job)
        {
            if (jobs.Any(e => (e.State == "等待" || e.State == "处理中") && e.Job.Kind == job.Kind && string.Equals(e.Job.InputPath, job.InputPath, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Job.AudioPath, job.AudioPath, StringComparison.OrdinalIgnoreCase))) return false;
            job.OutputPath = NextOutput(job);
            QueueEntry entry = new QueueEntry { Job = job, State = "等待" };
            string name = Path.GetFileName(job.InputPath);
            if (job.AudioPath != null) name += " + " + Path.GetFileName(job.AudioPath);
            ListViewItem item = new ListViewItem(new[] { "等待", KindName(job.Kind), name, job.OutputPath });
            item.Tag = entry;
            item.ToolTipText = job.InputPath + (job.AudioPath == null ? "" : "\r\n外部音频：" + job.AudioPath) + "\r\n输出：" + job.OutputPath;
            item.BackColor = jobs.Count % 2 == 0 ? Color.White : Color.FromArgb(249, 251, 254);
            entry.Item = item;
            jobs.Add(entry);
            queue.Items.Add(item);
            item.EnsureVisible();
            return true;
        }

        private string NextOutput(MediaJob job)
        {
            string extension = job.Kind == MediaTaskKind.FlacToWav ? ".wav" : job.Kind == MediaTaskKind.MkvToMp4 ? ".mp4" : ".mkv";
            string stem = Path.GetFileNameWithoutExtension(job.InputPath) + (job.Kind == MediaTaskKind.MergeMp4Audio ? "_无损" : "");
            string directory = Path.GetDirectoryName(job.InputPath);
            string candidate = Path.Combine(directory, stem + extension);
            int suffix = 2;
            while (File.Exists(candidate) || Directory.Exists(candidate) || jobs.Any(e => string.Equals(e.Job.OutputPath, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                candidate = Path.Combine(directory, stem + " (" + suffix++ + ")" + extension);
            }
            return candidate;
        }

        private async Task RunQueueAsync()
        {
            if (running) return;
            if (toolPaths == null)
            {
                MessageBox.Show(this, "请先选择同时包含 ffmpeg.exe 和 ffprobe.exe 的文件夹。", "需要 FFmpeg", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ChooseTools(this, EventArgs.Empty);
                if (toolPaths == null) return;
            }
            List<QueueEntry> pending = jobs.Where(e => e.State == "等待").ToList();
            if (pending.Count == 0) { overallLabel.Text = "队列中没有等待的任务，请先添加文件。"; return; }
            running = true;
            cancellation = new CancellationTokenSource();
            startButton.Enabled = false;
            cancelButton.Enabled = true;
            addMergeButton.Enabled = false;
            int completed = 0, failed = 0, finished = 0;
            progressBar.Value = 0;
            try
            {
                foreach (QueueEntry entry in pending)
                {
                    if (cancellation.IsCancellationRequested) break;
                    if (!jobs.Contains(entry)) continue;
                    SetState(entry, "处理中", Accent);
                    entry.Item.EnsureVisible();
                    currentLabel.Text = "正在处理：" + Path.GetFileName(entry.Job.InputPath);
                    AppendLog("\r\n========== " + KindName(entry.Job.Kind) + " ==========\r\n输入：" + entry.Job.InputPath + (entry.Job.AudioPath == null ? "" : "\r\n外部音轨：" + entry.Job.AudioPath) + "\r\n输出：" + entry.Job.OutputPath);
                    progressBar.Style = ProgressBarStyle.Marquee;
                    IProgress<MediaProgress> progress = new Progress<MediaProgress>(delegate(MediaProgress p)
                    {
                        if (!running || entry.State != "处理中") return;
                        if (p.Percent.HasValue)
                        {
                            progressBar.Style = ProgressBarStyle.Continuous;
                            progressBar.Value = (int)Math.Max(0, Math.Min(100, p.Percent.Value));
                            entry.Item.SubItems[0].Text = progressBar.Value + "%";
                        }
                        if (!string.IsNullOrEmpty(p.Message)) currentLabel.Text = p.Message;
                        if (!string.IsNullOrEmpty(p.LogLine)) AppendLog(p.LogLine);
                    });
                    try
                    {
                        // A name may have appeared since the task was queued; choose again safely.
                        if (File.Exists(entry.Job.OutputPath) || Directory.Exists(entry.Job.OutputPath))
                        {
                            entry.Job.OutputPath = NextOutput(entry.Job);
                            entry.Item.SubItems[3].Text = entry.Job.OutputPath;
                        }
                        ConversionResult result = await new MediaEngine().RunAsync(entry.Job, toolPaths, progress, cancellation.Token);
                        entry.Job.OutputPath = result.OutputPath;
                        entry.Item.SubItems[3].Text = result.OutputPath;
                        SetState(entry, result.UsedLossyEncoding ? "完成·有损" : "完成·无损", Color.FromArgb(25, 128, 94));
                        entry.Detail = result.Summary;
                        entry.Item.ToolTipText += "\r\n" + result.Summary;
                        AppendLog(result.Summary);
                        completed++;
                    }
                    catch (OperationCanceledException)
                    {
                        SetState(entry, "已取消", Muted);
                        entry.Detail = "用户取消，临时输出已清理。";
                        AppendLog(entry.Detail);
                        break;
                    }
                    catch (Exception ex)
                    {
                        SetState(entry, "失败", Color.FromArgb(188, 53, 65));
                        entry.Detail = ex.Message;
                        entry.Item.ToolTipText += "\r\n失败：" + ex.Message;
                        AppendLog("失败：" + ex.Message);
                        failed++;
                    }
                    finished++;
                    overallLabel.Text = "本批次 " + finished + " / " + pending.Count + "  ·  成功 " + completed + "  ·  失败 " + failed;
                }
                currentLabel.Text = cancellation.IsCancellationRequested ? "批次已取消；等待中的任务可继续处理" : failed > 0 ? "批次结束，失败原因可在日志中查看" : "处理完成，双击任务打开输出位置";
                overallLabel.Text = "成功 " + completed + "  ·  失败 " + failed + "  ·  等待 " + jobs.Count(e => e.State == "等待");
            }
            finally
            {
                running = false;
                cancellation.Dispose();
                cancellation = null;
                startButton.Enabled = true;
                cancelButton.Enabled = false;
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Value = completed > 0 && failed == 0 && finished == pending.Count ? 100 : 0;
                UpdateMergeTiles();
                if (closeWhenStopped) Close();
            }
        }

        private void CancelBatch()
        {
            if (cancellation == null) return;
            cancellation.Cancel();
            cancelButton.Enabled = false;
            currentLabel.Text = "正在停止并清理临时输出…";
        }

        private void SetState(QueueEntry entry, string state, Color color)
        {
            entry.State = state;
            entry.Item.SubItems[0].Text = state;
            entry.Item.ForeColor = color;
        }

        private void RemoveSelected()
        {
            foreach (ListViewItem item in queue.SelectedItems.Cast<ListViewItem>().ToArray())
            {
                QueueEntry entry = (QueueEntry)item.Tag;
                if (entry.State == "处理中") continue;
                jobs.Remove(entry);
                queue.Items.Remove(item);
            }
        }

        private void ClearFinished()
        {
            foreach (QueueEntry entry in jobs.Where(e => e.State != "等待" && e.State != "处理中").ToArray())
            {
                jobs.Remove(entry);
                queue.Items.Remove(entry.Item);
            }
        }

        private void OpenSelectedOutput()
        {
            if (queue.SelectedItems.Count == 0) return;
            QueueEntry entry = (QueueEntry)queue.SelectedItems[0].Tag;
            string path = File.Exists(entry.Job.OutputPath) ? entry.Job.OutputPath : entry.Job.InputPath;
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }

        private void CopySelected()
        {
            if (queue.SelectedItems.Count == 0) return;
            QueueEntry entry = (QueueEntry)queue.SelectedItems[0].Tag;
            Clipboard.SetText("输入：" + entry.Job.InputPath + "\r\n输出：" + entry.Job.OutputPath + "\r\n状态：" + entry.State + "\r\n" + entry.Detail);
        }

        private void AppendLog(string text)
        {
            if (logs.Length > 600000) logs.Remove(0, logs.Length - 400000);
            logs.AppendLine(DateTime.Now.ToString("HH:mm:ss") + "  " + text);
        }

        private void ShowLog()
        {
            using (Form window = new Form { Text = "处理日志 · KaraokeConvert", Size = new Size(920, 600), StartPosition = FormStartPosition.CenterParent, Font = Font, MinimizeBox = false })
            {
                TextBox box = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, BackColor = Color.FromArgb(247, 249, 252), Font = new Font("Consolas", 10F), Text = logs.Length == 0 ? "暂无处理日志。" : logs.ToString() };
                Button copy = MakeButton("复制全部日志", true);
                copy.Dock = DockStyle.Bottom;
                copy.Height = 38;
                copy.Click += delegate { if (box.TextLength > 0) Clipboard.SetText(box.Text); };
                window.Controls.Add(box);
                window.Controls.Add(copy);
                window.ShowDialog(this);
            }
        }

        private void ChooseTools(object sender, EventArgs e)
        {
            if (running) { overallLabel.Text = "任务结束后可更改 FFmpeg 位置。"; return; }
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择同时包含 ffmpeg.exe 和 ffprobe.exe 的文件夹（通常为 bin 文件夹）";
                dialog.ShowNewFolderButton = false;
                if (!string.IsNullOrEmpty(toolFolder) && Directory.Exists(toolFolder)) dialog.SelectedPath = toolFolder;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string selected = dialog.SelectedPath;
                    bool exactPair = File.Exists(Path.Combine(selected, "ffmpeg.exe")) && File.Exists(Path.Combine(selected, "ffprobe.exe"));
                    bool binPair = File.Exists(Path.Combine(selected, "bin", "ffmpeg.exe")) && File.Exists(Path.Combine(selected, "bin", "ffprobe.exe"));
                    if (!exactPair && !binPair)
                        throw new FileNotFoundException("所选文件夹（或其中的 bin 文件夹）必须同时包含 ffmpeg.exe 和 ffprobe.exe。");
                    ToolPaths chosen = ToolPaths.Resolve(dialog.SelectedPath);
                    toolPaths = chosen;
                    toolFolder = dialog.SelectedPath;
                    UpdateToolsLabel();
                    try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg-location.txt"), toolFolder, Encoding.UTF8); }
                    catch (Exception ex) { AppendLog("FFmpeg 位置本次有效，配置未保存：" + ex.Message); }
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "FFmpeg 位置无效", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            }
        }

        private void LoadSettings()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg-location.txt");
                if (File.Exists(path)) toolFolder = File.ReadAllText(path, Encoding.UTF8).Trim();
            }
            catch (Exception ex) { AppendLog("读取配置失败：" + ex.Message); }
        }

        private void ResolveTools()
        {
            try { toolPaths = ToolPaths.Resolve(toolFolder); }
            catch (Exception ex) { AppendLog(ex.Message); toolPaths = null; }
            UpdateToolsLabel();
        }

        private void UpdateToolsLabel()
        {
            toolsLabel.Text = toolPaths == null ? "●  未找到 FFmpeg，请设置位置" : "●  FFmpeg 已就绪";
            toolsLabel.ForeColor = toolPaths == null ? Color.FromArgb(178, 105, 26) : Color.FromArgb(25, 128, 94);
            tips.SetToolTip(toolsLabel, toolPaths == null ? "需同时安装 ffmpeg 和 ffprobe" : toolPaths.FFmpegPath);
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!running) return;
            e.Cancel = true;
            if (closeWhenStopped) return;
            if (MessageBox.Show(this, "仍有任务正在处理。取消当前批次并关闭窗口？", "关闭软件", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                closeWhenStopped = true;
                CancelBatch();
            }
        }

        private static string KindName(MediaTaskKind kind)
        {
            return kind == MediaTaskKind.FlacToWav ? "FLAC → WAV" : kind == MediaTaskKind.MkvToMp4 ? "MKV → MP4" : "MP4 + 音频 → MKV";
        }

        private static Button MakeButton(string text, bool primary)
        {
            Button button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Color.White, ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(219, 225, 235);
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(35, 80, 188) : Color.FromArgb(235, 241, 250);
            return button;
        }

        private static void FilesDragEnter(object sender, DragEventArgs e) { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; }
        private static string[] GetFiles(DragEventArgs e) { return e.Data.GetData(DataFormats.FileDrop) as string[] ?? new string[0]; }

        private sealed class QueueEntry
        {
            public MediaJob Job;
            public ListViewItem Item;
            public string State;
            public string Detail;
        }
    }

    internal sealed class ModeButton : Button
    {
        private readonly string title;
        private readonly string subtitle;
        public bool Selected { get; set; }
        public ModeButton(string titleText, string subtitleText)
        {
            title = titleText;
            subtitle = subtitleText;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            AccessibleName = title;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color border = Selected ? Color.FromArgb(43, 95, 217) : Color.FromArgb(219, 225, 235);
            e.Graphics.Clear(Selected ? Color.FromArgb(234, 241, 255) : Color.White);
            using (Pen pen = new Pen(border, Selected ? 2 : 1)) e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
            using (Font titleFont = new Font(Font.FontFamily, 11F, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, title, titleFont, new Rectangle(17, 10, Width - 25, 25), Selected ? border : Color.FromArgb(29, 39, 59), TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, subtitle, Font, new Rectangle(17, 37, Width - 25, 23), Color.FromArgb(105, 118, 139), TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(5, 5, Width - 10, Height - 10));
        }
    }

    internal sealed class DropTile : Control
    {
        private string title;
        private string detail;
        private bool hovered;
        private readonly bool wide;
        public event Action<string[]> FilesDropped;
        public string Title { get { return title; } set { title = value; AccessibleName = value; Invalidate(); } }
        public string Detail { get { return detail; } set { detail = value; Invalidate(); } }
        public DropTile(string titleText, string detailText, bool wideTile)
        {
            Title = titleText;
            detail = detailText;
            wide = wideTile;
            AllowDrop = true;
            Cursor = Cursors.Hand;
            TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable, true);
            DragEnter += delegate(object sender, DragEventArgs e)
            {
                hovered = e.Data.GetDataPresent(DataFormats.FileDrop);
                e.Effect = hovered ? DragDropEffects.Copy : DragDropEffects.None;
                Invalidate();
            };
            DragLeave += delegate { hovered = false; Invalidate(); };
            DragDrop += delegate(object sender, DragEventArgs e)
            {
                hovered = false;
                Invalidate();
                Action<string[]> handler = FilesDropped;
                if (handler != null) handler(e.Data.GetData(DataFormats.FileDrop) as string[] ?? new string[0]);
            };
            MouseEnter += delegate { hovered = true; Invalidate(); };
            MouseLeave += delegate { hovered = false; Invalidate(); };
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = true; OnClick(EventArgs.Empty); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(hovered ? Color.FromArgb(236, 242, 255) : Color.FromArgb(251, 253, 255));
            using (Pen border = new Pen(hovered ? Color.FromArgb(43, 95, 217) : Color.FromArgb(177, 192, 214), 1))
            {
                border.DashStyle = DashStyle.Dash;
                e.Graphics.DrawRectangle(border, 1, 1, Width - 3, Height - 3);
            }
            int center = Height / 2;
            int iconX = wide ? Width / 2 - 165 : 28;
            int textX = wide ? Width / 2 - 115 : 79;
            int textWidth = wide ? Width - textX - 15 : Width - 100;
            using (Pen icon = new Pen(Color.FromArgb(43, 95, 217), 2))
            {
                e.Graphics.DrawRectangle(icon, iconX, center - 23, 28, 38);
                e.Graphics.DrawLine(icon, iconX + 7, center - 9, iconX + 21, center - 9);
                e.Graphics.DrawLine(icon, iconX + 7, center - 1, iconX + 21, center - 1);
                e.Graphics.DrawLine(icon, iconX + 7, center + 7, iconX + 17, center + 7);
            }
            using (Font heading = new Font(Font.FontFamily, 12F, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, title, heading, new Rectangle(textX, center - 26, textWidth, 30), Color.FromArgb(29, 39, 59), TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(e.Graphics, detail, Font, new Rectangle(textX, center + 7, textWidth, 29), Color.FromArgb(105, 118, 139), TextFormatFlags.SingleLine | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(5, 5, Width - 10, Height - 10));
        }
    }
}
