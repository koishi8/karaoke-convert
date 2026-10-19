using System;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace LosslessMedia
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main(string[] args)
        {
            if (Environment.OSVersion.Version.Major >= 6) SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, "KaraokeConvert", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            Application.Run(new MainForm(args));
        }
    }
}
