using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// 图标生成器：由 PNG 生成多尺寸 .ico（16/32/48 BMP + 256 PNG）
// 用法: ico_make <png> <out.ico>
class IcoMaker
{
    static void Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: ico_make <png> <out.ico>"); return; }
        string src = args[0], outp = args[1];
        using (Image s = Image.FromFile(src))
        {
            int[] sizes = { 16, 32, 48 };
            MemoryStream ms = new MemoryStream();
            BinaryWriter bw = new BinaryWriter(ms);
            int count = sizes.Length + 1;
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)count);
            byte[][] dibs = new byte[sizes.Length][];
            int offset = 6 + 16 * count;
            for (int i = 0; i < sizes.Length; i++)
            {
                int sz = sizes[i];
                using (Bitmap b = new Bitmap(sz, sz))
                {
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.SmoothingMode = SmoothingMode.HighQuality;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Transparent);
                        g.DrawImage(s, 0, 0, sz, sz);
                    }
                    using (MemoryStream ims = new MemoryStream())
                    {
                        b.Save(ims, ImageFormat.Bmp);
                        byte[] all = ims.ToArray();
                        dibs[i] = new byte[all.Length - 14];
                        Array.Copy(all, 14, dibs[i], 0, dibs[i].Length);
                    }
                }
                bw.Write((byte)sz);
                bw.Write((byte)sz);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write((uint)dibs[i].Length);
                bw.Write((uint)offset);
                offset += dibs[i].Length;
            }
            byte[] png;
            using (Bitmap b = new Bitmap(256, 256))
            {
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.DrawImage(s, 0, 0, 256, 256);
                }
                using (MemoryStream ims = new MemoryStream()) { b.Save(ims, ImageFormat.Png); png = ims.ToArray(); }
            }
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((ushort)1); bw.Write((ushort)32);
            bw.Write((uint)png.Length);
            bw.Write((uint)offset);
            offset += png.Length;
            for (int i = 0; i < sizes.Length; i++) bw.Write(dibs[i]);
            bw.Write(png);
            bw.Flush();
            File.WriteAllBytes(outp, ms.ToArray());
            bw.Close();
        }
        Console.WriteLine("OK " + new FileInfo(outp).Length + " bytes");
    }
}
