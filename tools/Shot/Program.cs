// 开发期验证工具：移动鼠标 → 等待 → 截取屏幕上半部分
// 用法: Shot.exe <out.png> <cursorX> <cursorY> <height> [waitMs]
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;

if (args.Length < 4)
{
    Console.WriteLine("usage: Shot.exe <out.png> <cursorX> <cursorY> <height> [waitMs]");
    return 1;
}

var outPath = args[0];
var cx = int.Parse(args[1]);
var cy = int.Parse(args[2]);
var height = int.Parse(args[3]);
var wait = args.Length > 4 ? int.Parse(args[4]) : 900;

var bounds = Screen.PrimaryScreen!.Bounds;
height = Math.Min(height, bounds.Height);
Console.WriteLine($"screen={bounds.Width}x{bounds.Height} scale-check");

if (cx >= 0)
{
    Cursor.Position = new Point(cx, cy);
}
Thread.Sleep(wait);

using var bmp = new Bitmap(bounds.Width, height);
using (var g = Graphics.FromImage(bmp))
{
    g.CopyFromScreen(0, 0, 0, 0, new Size(bounds.Width, height));
}
bmp.Save(outPath, ImageFormat.Png);
Console.WriteLine("saved " + outPath);
return 0;
