using System.Windows.Forms;

namespace PasteSink;

/// <summary>
/// 端到端验证「点一下自动粘贴」用的靶子窗口：
/// 一个多行文本框，内容一变就写进指定文件，方便外部断言粘贴是否真的发生了。
/// 用法：PasteSink.exe &lt;输出文件&gt; [自动退出秒数]
/// </summary>
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var outPath = args.Length > 0 ? args[0] : "paste-sink.txt";
        var lifeSec = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 60;

        ApplicationConfiguration.Initialize();

        var form = new Form
        {
            Text = "PasteSink · 粘贴靶子",
            Width = 640,
            Height = 320,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true
        };

        var box = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            Font = new System.Drawing.Font("Consolas", 12f)
        };
        form.Controls.Add(box);

        void Dump()
        {
            try { File.WriteAllText(outPath, box.Text); } catch { }
        }

        box.TextChanged += (_, _) => Dump();

        var killer = new System.Windows.Forms.Timer { Interval = lifeSec * 1000 };
        killer.Tick += (_, _) => { Dump(); form.Close(); };
        killer.Start();

        form.Load += (_, _) =>
        {
            box.Focus();
            Dump();
        };

        form.Shown += (_, _) =>
        {
            form.Activate();
            box.Focus();
        };

        Application.Run(form);
    }
}
