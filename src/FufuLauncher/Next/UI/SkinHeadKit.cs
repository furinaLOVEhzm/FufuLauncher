// Copyright © FufuLauncher
//
// 3D 玩家头颅渲染 —— 软件像素光栅化 + Z-buffer(底层完整重做 v3)。
// 参考「3D 皮肤层 Skin Layers 3D」模组思路并推进一层:皮肤外凸层(头发/帽子)逐像素体素化 ——
// 每个不透明外凸层像素都是自皮肤表面升起的一只独立小方块(外凸深度 Extrude),
// 头发等部位的方块结构成为真实几何,颗粒感清晰突出;
// 渲染正确性由 Z-buffer 逐像素深度测试保证,不依赖画家序猜测:
// 1. 正交投影(偏航 34°/俯仰 26°),深度 = 视线轴分量,大者近;
// 2. 零插值:UV 反解后最近邻采样;零混合:alpha<128 整像素跳过 —— 无黑边、无灰雾;
// 3. 立体感只来自 MC 原生方向光模型(亮度 RGB 直乘,无任何遮罩/描边/阴影):
//    立方体面 顶 1.0 / 正脸 0.85 / 侧面 0.7;体素侧壁按世界朝向受光 +y 1.0 / ±z 0.8 / ±x 0.6;
// 4. 基底头颅为平面立方体面,外凸层为逐像素小方块,透明处透出基底形成分层纵深。

using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FufuLauncher.Services;

using Vec = (double X, double Y, double Z);

namespace FufuLauncher.Next.UI;

public static class SkinHeadKit
{
    // 视角参数:偏航 34°(露出正脸+右侧面)、俯仰 26°(微俯视,露出顶面)
    private static readonly double Alpha = 34 * Math.PI / 180;
    private static readonly double Beta = 26 * Math.PI / 180;
    private static readonly double CosA = Math.Cos(Alpha), SinA = Math.Sin(Alpha);
    private static readonly double CosB = Math.Cos(Beta), SinB = Math.Sin(Beta);

    private const double Scale = 10.0;    // 每纹理像素的光栅化边长(输出分辨率)
    private const double Extrude = 0.6;   // 外凸层逐像素体素的外凸深度(纹理像素单位)

    // MC 原生方向光面亮度(RGB 直乘,无遮罩):立方体可见三面
    private const double ShTop = 1.0, ShFront = 0.85, ShSide = 0.70;
    // 体素侧壁亮度(按世界朝向):顶向 +y / 南北向 ±z / 东西向 ±x
    private const double ShWallUp = 1.0, ShWallZ = 0.80, ShWallX = 0.60;

    /// <summary>获取账号头颅图像:皮肤获取链(缓存 → 正版 → 随机生成)由 SkinService 兜底,永不失败</summary>
    public static async Task<ImageSource?> GetHeadImageAsync(string uuid, AccountType type)
    {
        try
        {
            var bytes = await SkinService.GetSkinBytesAsync(uuid, type);
            return BuildHead(bytes);
        }
        catch (Exception ex)
        {
            App.WriteAppLog($"[皮肤] 头颅渲染失败:{ex.Message}");
            return null;
        }
    }

    /// <summary>正交投影:先绕 Y 偏航、再绕 X 俯视,输出画布坐标(y 向下,纹理像素单位)</summary>
    private static (double X, double Y) Project(double x, double y, double z)
    {
        double x1 = x * CosA - z * SinA;
        double z1 = x * SinA + z * CosA;
        double y1 = y * CosB - z1 * SinB;
        return (x1, -y1);
    }

    /// <summary>视线深度:越大越靠近相机(Z-buffer 比较量)</summary>
    private static double Depth(double x, double y, double z)
    {
        double z1 = x * SinA + z * CosA;
        return y * SinB + z1 * CosB;
    }

    private static Vec Add(Vec a, Vec b) => (a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    private static Vec Mul(Vec a, double k) => (a.X * k, a.Y * k, a.Z * k);

    /// <summary>由皮肤 PNG 字节光栅化 3D 方块头颅(基底平面 + 外凸层逐像素体素,Z-buffer 深度测试)</summary>
    public static ImageSource BuildHead(byte[] skinBytes)
    {
        var skin = LoadSkinPixels(skinBytes, out int skinW, out int _);
        const double h = 4.0;            // 头颅半边长(纹理像素单位,头为 8×8×8)
        double ext = h + Extrude;        // 外凸体素最外 extent,用于包围盒

        // 画布包围盒(±ext 立方体 8 顶点投影)
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var sx in new[] { -ext, ext })
            foreach (var sy in new[] { -ext, ext })
                foreach (var sz in new[] { -ext, ext })
                {
                    var p = Project(sx, sy, sz);
                    minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
                    maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
                }
        int W = (int)Math.Ceiling((maxX - minX) * Scale);
        int H = (int)Math.Ceiling((maxY - minY) * Scale);
        var buf = new int[W * H];                       // Bgra32 打包,0 = 透明
        var zbuf = new float[W * H];
        Array.Fill(zbuf, float.NegativeInfinity);

        // 屏幕投影(归一化原点)
        (double X, double Y) SP(Vec p)
        {
            var q = Project(p.X, p.Y, p.Z);
            return (q.X - minX, q.Y - minY);
        }

        // 面亮度 RGB 直乘(保留色相饱和,无灰雾)
        int Shade(int c, double f)
        {
            if (f >= 1.0) return c;
            int b = (int)((c & 0xFF) * f);
            int g = (int)(((c >> 8) & 0xFF) * f);
            int r = (int)(((c >> 16) & 0xFF) * f);
            return (c & unchecked((int)0xFF000000)) | (r << 16) | (g << 8) | b;
        }

        // 外凸层某面 (u,v) 像素是否不透明(面外视为透明)
        bool Opaque(int uvX, int uvY, int u, int v) =>
            u >= 0 && u < 8 && v >= 0 && v < 8 &&
            (uint)skin[(uvY + v) * skinW + uvX + u] >= 0x80000000u;

        // 平行四边形光栅化:原点 o、u/v 方向(单位向量×长度)、采样回调返回 0 表示跳过;Z-buffer 深度测试
        void Quad(Vec o, Vec ud, double ulen, Vec vd, double vlen, Func<double, double, int> sample)
        {
            var p0 = SP(o);
            var pu = SP(Add(o, Mul(ud, ulen)));
            var pv = SP(Add(o, Mul(vd, vlen)));
            double duX = (pu.X - p0.X) / ulen, duY = (pu.Y - p0.Y) / ulen;
            double dvX = (pv.X - p0.X) / vlen, dvY = (pv.Y - p0.Y) / vlen;
            double det = duX * dvY - duY * dvX;
            if (Math.Abs(det) < 1e-9) return;

            double z0 = Depth(o.X, o.Y, o.Z);
            double zu = Depth(o.X + ud.X * ulen, o.Y + ud.Y * ulen, o.Z + ud.Z * ulen);
            double zv = Depth(o.X + vd.X * vlen, o.Y + vd.Y * vlen, o.Z + vd.Z * vlen);

            double fx0 = Math.Min(p0.X, Math.Min(p0.X + duX * ulen, Math.Min(p0.X + dvX * vlen, p0.X + duX * ulen + dvX * vlen)));
            double fx1 = Math.Max(p0.X, Math.Max(p0.X + duX * ulen, Math.Max(p0.X + dvX * vlen, p0.X + duX * ulen + dvX * vlen)));
            double fy0 = Math.Min(p0.Y, Math.Min(p0.Y + duY * ulen, Math.Min(p0.Y + dvY * vlen, p0.Y + duY * ulen + dvY * vlen)));
            double fy1 = Math.Max(p0.Y, Math.Max(p0.Y + duY * ulen, Math.Max(p0.Y + dvY * vlen, p0.Y + duY * ulen + dvY * vlen)));
            int x0 = Math.Max(0, (int)Math.Floor(fx0 * Scale)), x1 = Math.Min(W - 1, (int)Math.Ceiling(fx1 * Scale));
            int y0 = Math.Max(0, (int)Math.Floor(fy0 * Scale)), y1 = Math.Min(H - 1, (int)Math.Ceiling(fy1 * Scale));

            for (int py = y0; py <= y1; py++)
            {
                double cy = (py + 0.5) / Scale - p0.Y;
                for (int px = x0; px <= x1; px++)
                {
                    double cx = (px + 0.5) / Scale - p0.X;
                    // 反解仿射:屏幕偏移 = u*du + v*dv → 面内坐标 (u,v)
                    double u = (cx * dvY - cy * dvX) / det;
                    double v = (duX * cy - duY * cx) / det;
                    if (u < 0 || u >= ulen || v < 0 || v >= vlen) continue;

                    int i = py * W + px;
                    float z = (float)(z0 + (zu - z0) * (u / ulen) + (zv - z0) * (v / vlen));
                    if (z < zbuf[i]) continue;

                    int c = sample(u, v);
                    if (c == 0) continue;
                    zbuf[i] = z;
                    buf[i] = c;
                }
            }
        }

        // 纹理面采样:最近邻,透明返回 0
        int TexSample(int uvX, int uvY, double shade, double u, double v)
        {
            int c = skin[(uvY + (int)v) * skinW + uvX + (int)u];
            if ((uint)c < 0x80000000u) return 0;
            return Shade(c, shade);
        }

        // ---- 1. 基底头颅三个可见面(平面)----
        Quad((-h, h, -h), (1, 0, 0), 8, (0, 0, 1), 8, (u, v) => TexSample(8, 0, ShTop, u, v));    // 顶面:u→+X, v→+Z
        Quad((h, h, h), (0, 0, -1), 8, (0, -1, 0), 8, (u, v) => TexSample(0, 8, ShSide, u, v));    // 右侧面:u→-Z, v→-Y
        Quad((-h, h, h), (1, 0, 0), 8, (0, -1, 0), 8, (u, v) => TexSample(8, 8, ShFront, u, v));  // 正脸:u→+X, v→-Y

        // ---- 2. 外凸层(Skin Layers 3D 思路):逐像素体素 ----
        // 面配置:UV 原点、面原点、u/v/法线方向、面亮度;可见侧壁 = 朝相机方向的邻居为透明
        //   顶面 法线+y:相机侧壁朝 +x(u+1)/ +z(v+1)
        //   右侧 法线+x:相机侧壁朝 +z(u-1)/ +y(v-1)
        //   正脸 法线+z:相机侧壁朝 +x(u+1)/ +y(v-1)
        var layers = new (int UvX, int UvY, Vec Fo, Vec U, Vec V, Vec N, double FaceSh,
                          int UNb, double UWallSh, int VNb, double VWallSh)[]
        {
            (40, 0, (-h, h, -h), (1, 0, 0), (0, 0, 1), (0, 1, 0), ShTop, +1, ShWallX, +1, ShWallZ),
            (32, 8, (h, h, h), (0, 0, -1), (0, -1, 0), (1, 0, 0), ShSide, -1, ShWallZ, -1, ShWallUp),
            (40, 8, (-h, h, h), (1, 0, 0), (0, -1, 0), (0, 0, 1), ShFront, +1, ShWallX, -1, ShWallUp)
        };

        foreach (var ly in layers)
        {
            // 2a. 体素侧壁:不透明像素的相机侧邻居为透明(或越界)时,绘制朝相机的壁面
            for (int v0 = 0; v0 < 8; v0++)
                for (int u0 = 0; u0 < 8; u0++)
                {
                    int c = skin[(ly.UvY + v0) * skinW + ly.UvX + u0];
                    if ((uint)c < 0x80000000u) continue;

                    if (!Opaque(ly.UvX, ly.UvY, u0 + ly.UNb, v0))
                    {
                        var edge = Add(ly.Fo, Mul(ly.U, ly.UNb > 0 ? u0 + 1 : u0));
                        int wc = Shade(c, ly.UWallSh);
                        Quad(edge, ly.V, 1, ly.N, Extrude, (_, _) => wc);
                    }
                    if (!Opaque(ly.UvX, ly.UvY, u0, v0 + ly.VNb))
                    {
                        var edge = Add(ly.Fo, Mul(ly.V, ly.VNb > 0 ? v0 + 1 : v0));
                        int wc = Shade(c, ly.VWallSh);
                        Quad(edge, ly.U, 1, ly.N, Extrude, (_, _) => wc);
                    }
                }

            // 2b. 体素顶面:整面平移 Extrude 后一次性光栅化(逐像素最近邻采样,透明跳过)
            var fo = Add(ly.Fo, Mul(ly.N, Extrude));
            Quad(fo, ly.U, 8, ly.V, 8, (u, v) => TexSample(ly.UvX, ly.UvY, ly.FaceSh, u, v));
        }

        return ToBitmapSource(buf, W, H);
    }

    /// <summary>皮肤 PNG → Bgra32 像素数组(int 打包:高位 alpha)</summary>
    private static int[] LoadSkinPixels(byte[] bytes, out int width, out int height)
    {
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(bytes))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
        }
        var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        width = conv.PixelWidth;
        height = conv.PixelHeight;
        var px = new int[width * height];
        conv.CopyPixels(px, width * 4, 0);
        return px;
    }

    /// <summary>像素缓冲 → 冻结 BitmapSource(Bgra32)</summary>
    private static BitmapSource ToBitmapSource(int[] buf, int width, int height)
    {
        var bytes = new byte[buf.Length * 4];
        Buffer.BlockCopy(buf, 0, bytes, 0, bytes.Length);
        var src = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
        src.Freeze();
        return src;
    }
}
