using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 视频抽帧小工具:打开 mp4 -> 等间隔逐帧截图保存 PNG
var videoPath = args.Length > 0 ? args[0] : throw new Exception("need video path");
var outDir = args.Length > 1 ? args[1] : Path.Combine(Path.GetDirectoryName(videoPath)!, "frames");
Directory.CreateDirectory(outDir);

var thread = new Thread(() => Run(videoPath, outDir));
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

static void Run(string videoPath, string outDir)
{
    var app = new Application();
    var me = new MediaElement { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual };
    var win = new Window { Width = 400, Height = 300, Content = me, ShowInTaskbar = false };

    bool done = false;
    int saved = 0;

    me.MediaOpened += async (_, _) =>
    {
        try
        {
            double total = me.NaturalDuration.HasTimeSpan ? me.NaturalDuration.TimeSpan.TotalSeconds : 0;
            if (total <= 0) { Console.WriteLine("ERROR: no duration"); done = true; return; }
            int count = (int)Math.Min(24, Math.Max(6, total));
            double step = total / count;
            Console.WriteLine($"DURATION={total:F1}s FRAMES={count}");
            for (int i = 0; i < count; i++)
            {
                double t = Math.Min(i * step + 0.05, Math.Max(0, total - 0.2));
                me.Position = TimeSpan.FromSeconds(t);
                me.Play();
                await Task.Delay(350);      // 等待解码渲染该帧
                me.Pause();
                await Task.Delay(100);
                try
                {
                    var rtb = new RenderTargetBitmap(
                        Math.Max(1, (int)me.ActualWidth), Math.Max(1, (int)me.ActualHeight),
                        96, 96, PixelFormats.Pbgra32);
                    rtb.Render(me);
                    var enc = new PngBitmapEncoder();
                    enc.Frames.Add(BitmapFrame.Create(rtb));
                    string f = Path.Combine(outDir, $"frame_{i:00}_{t:0000}s.png");
                    using var fs = File.Create(f);
                    enc.Save(fs);
                    saved++;
                    Console.WriteLine($"OK {f}");
                }
                catch (Exception ex) { Console.WriteLine($"SHOT_FAIL {ex.Message}"); }
            }
        }
        catch (Exception ex) { Console.WriteLine("ERROR: " + ex.Message); }
        done = true;
    };

    me.MediaFailed += (_, e) => { Console.WriteLine("MEDIA_FAIL: " + e.ErrorException?.Message); done = true; };

    win.Loaded += (_, _) =>
    {
        me.Source = new Uri(videoPath, UriKind.Absolute);
        Task.Delay(60000).ContinueWith(_ => { if (!done) { Console.WriteLine("TIMEOUT"); done = true; } });
    };

    var checkTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
    checkTimer.Tick += (_, _) =>
    {
        if (done) { checkTimer.Stop(); Console.WriteLine($"DONE saved={saved}"); app.Shutdown(); }
    };

    win.Show();
    checkTimer.Start();
    app.Run(win);
}
