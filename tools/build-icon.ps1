$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
public static class AxiomIconBuilder {
    public static void Build(string directory) {
        Directory.CreateDirectory(directory);
        var sizes = new[] {16,24,32,48,64,128,256};
        var images = new byte[sizes.Length][];
        for (int i=0;i<sizes.Length;i++) {
            int size=sizes[i];
            using(var large=new Bitmap(size*4,size*4,PixelFormat.Format32bppArgb)) {
                using(var g=Graphics.FromImage(large)) {
                    g.SmoothingMode=SmoothingMode.AntiAlias; g.ScaleTransform(size/64f,size/64f);
                    using(var tile=new GraphicsPath()) {
                        tile.AddArc(8,8,112,112,180,90); tile.AddArc(136,8,112,112,270,90);
                        tile.AddArc(136,136,112,112,0,90); tile.AddArc(8,136,112,112,90,90); tile.CloseFigure();
                        using(var fill=new LinearGradientBrush(new Point(8,8),new Point(248,248),Color.FromArgb(50,122,101),Color.FromArgb(28,81,70))) g.FillPath(fill,tile);
                    }
                    using(var a=new GraphicsPath(FillMode.Alternate)) {
                        a.AddPolygon(new[] {new Point(47,199),new Point(108,62),new Point(142,62),new Point(207,199),new Point(166,199),new Point(155,171),new Point(94,171),new Point(83,199)});
                        a.AddPolygon(new[] {new Point(106,140),new Point(144,140),new Point(125,91)});
                        using(var fill=new SolidBrush(Color.FromArgb(245,250,247))) g.FillPath(fill,a);
                    }
                    using(var dot=new SolidBrush(Color.FromArgb(184,221,152))) g.FillEllipse(dot,178,52,24,24);
                }
                using(var bitmap=new Bitmap(size,size,PixelFormat.Format32bppArgb)) {
                    using(var g=Graphics.FromImage(bitmap)) { g.InterpolationMode=InterpolationMode.HighQualityBicubic; g.DrawImage(large,0,0,size,size); }
                    using(var png=new MemoryStream()) { bitmap.Save(png,ImageFormat.Png); images[i]=png.ToArray(); }
                    if(size==256) bitmap.Save(Path.Combine(directory,"Axiom.png"),ImageFormat.Png);
                }
            }
        }
        using(var writer=new BinaryWriter(File.Create(Path.Combine(directory,"Axiom.ico")))) {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset=6+16*sizes.Length;
            for(int i=0;i<sizes.Length;i++) { writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)(sizes[i]==256?0:sizes[i])); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(images[i].Length); writer.Write(offset); offset+=images[i].Length; }
            foreach(var bytes in images) writer.Write(bytes);
        }
    }
}
'@
[AxiomIconBuilder]::Build((Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'))
