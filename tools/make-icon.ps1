# Generates assets/icon.ico — the static application icon (the tray icon itself is drawn at runtime).
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

Add-Type -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

public static class IcoTool
{
    public static void Make(string path)
    {
        int[] sizes = new int[] { 16, 24, 32, 48, 256 };
        var images = new byte[sizes.Length][];

        for (int i = 0; i < sizes.Length; i++)
        {
            images[i] = Render(sizes[i]);
        }

        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);
            w.Write((ushort)1);
            w.Write((ushort)sizes.Length);

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0);
                w.Write((byte)0);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(images[i].Length);
                w.Write(offset);
                offset += images[i].Length;
            }

            foreach (var image in images) w.Write(image);
        }
    }

    private static byte[] Render(int size)
    {
        using (var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                float k = size / 32f;
                float pad = 4f * k;
                var rect = new RectangleF(pad, pad, size - pad * 2f, size - pad * 2f);
                float stroke = Math.Max(1.2f, 4.5f * k);

                using (var track = new Pen(Color.FromArgb(255, 118, 130, 148), stroke))
                {
                    track.StartCap = LineCap.Round;
                    track.EndCap = LineCap.Round;
                    g.DrawEllipse(track, rect);
                }

                using (var arc = new Pen(Color.FromArgb(255, 61, 220, 151), stroke))
                {
                    arc.StartCap = LineCap.Round;
                    arc.EndCap = LineCap.Round;
                    g.DrawArc(arc, rect, -90f, 270f);
                }
            }

            var data = new byte[40 + size * size * 4 + ((size + 31) / 32) * 4 * size];

            using (var ms = new MemoryStream(data))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40);
                w.Write(size);
                w.Write(size * 2);
                w.Write((ushort)1);
                w.Write((ushort)32);
                w.Write(0);
                w.Write(size * size * 4);
                w.Write(0);
                w.Write(0);
                w.Write(0);
                w.Write(0);

                var bits = bmp.LockBits(new Rectangle(0, 0, size, size),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var row = new byte[bits.Stride];
                try
                {
                    for (int y = size - 1; y >= 0; y--)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(
                            new IntPtr(bits.Scan0.ToInt64() + (long)bits.Stride * y), row, 0, row.Length);
                        w.Write(row, 0, size * 4);
                    }
                }
                finally
                {
                    bmp.UnlockBits(bits);
                }

                w.Write(new byte[((size + 31) / 32) * 4 * size]);
            }

            return data;
        }
    }
}
'@ -ReferencedAssemblies 'System.Drawing'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$outDir = Join-Path $root 'assets'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$out = Join-Path $outDir 'icon.ico'
[IcoTool]::Make($out)
Write-Host "Icon written to $out"
