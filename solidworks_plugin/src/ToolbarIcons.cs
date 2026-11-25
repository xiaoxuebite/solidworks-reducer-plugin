using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace ReducerSW {
    internal static class ToolbarIcons {
        internal enum Command { OneStageDesign, OneStageModel, TwoStageDesign, TwoStageModel, Import }

        static readonly Color Ink=Color.FromArgb(30,57,80);
        static readonly Color Blue=Color.FromArgb(45,120,188);
        static readonly Color Teal=Color.FromArgb(0,120,100);
        static readonly Lazy<byte[]> ApplicationIconData=new Lazy<byte[]>(CreateApplicationIcon);
        static readonly Lazy<Icon> WindowIcon=new Lazy<Icon>(() => {
            using(var stream=new MemoryStream(ApplicationIconData.Value,false))
            using(var icon=new Icon(stream))return (Icon)icon.Clone();
        });

        internal static void ApplyWindowIcon(System.Windows.Forms.Form form) {
            form.Icon=WindowIcon.Value;
        }

        internal static void Write(string directory,out string[] commands,out string[] main) {
            Directory.CreateDirectory(directory);
            int[] sizes={20,32,40,64,96,128};
            int count=Enum.GetValues(typeof(Command)).Length;
            commands=new string[sizes.Length];main=new string[sizes.Length];
            for(int i=0;i<sizes.Length;i++) {
                int size=sizes[i];
                commands[i]=Path.Combine(directory,"reducer_commands_"+size+".png");
                main[i]=Path.Combine(directory,"reducer_main_"+size+".png");
                using(var strip=new Bitmap(size*count,size))
                using(var icon=new Bitmap(size,size)) {
                    using(var g=Graphics.FromImage(strip)) {
                        g.SmoothingMode=SmoothingMode.AntiAlias;
                        for(int slot=0;slot<count;slot++) {
                            g.ResetTransform();
                            g.TranslateTransform(slot*size,0);
                            g.ScaleTransform(size/32f,size/32f);
                            DrawCommand(g,(Command)slot);
                        }
                    }
                    DrawMainIcon(icon);
                    SaveIndexed(strip,commands[i]);
                    SaveIndexed(icon,main[i]);
                }
            }
        }

        static void DrawMainIcon(Bitmap bitmap) {
            using(var g=Graphics.FromImage(bitmap)) {
                g.SmoothingMode=SmoothingMode.AntiAlias;
                g.ScaleTransform(bitmap.Width/32f,bitmap.Height/32f);
                DrawGear(g,20.8f,10.3f,8.4f,Teal);
                DrawGear(g,11.5f,20.5f,10.4f,Blue);
            }
        }

        internal static void WriteApplicationIcon(string path) {
            File.WriteAllBytes(path,ApplicationIconData.Value);
        }

        static byte[] CreateApplicationIcon() {
            int[] sizes={16,20,24,32,40,48,64,96,128,256};
            var frames=new byte[sizes.Length][];
            for(int i=0;i<sizes.Length;i++) {
                using(var bitmap=new Bitmap(sizes[i],sizes[i]))
                using(var png=new MemoryStream()) {
                    DrawMainIcon(bitmap);
                    bitmap.Save(png,ImageFormat.Png);
                    frames[i]=png.ToArray();
                }
            }
            using(var stream=new MemoryStream())
            using(var writer=new BinaryWriter(stream)) {
                writer.Write((ushort)0);
                writer.Write((ushort)1);
                writer.Write((ushort)sizes.Length);
                int offset=6+16*sizes.Length;
                for(int i=0;i<sizes.Length;i++) {
                    byte dimension=(byte)(sizes[i]%256);
                    writer.Write(dimension);
                    writer.Write(dimension);
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)32);
                    writer.Write(frames[i].Length);
                    writer.Write(offset);
                    offset+=frames[i].Length;
                }
                foreach(var frame in frames)writer.Write(frame);
                writer.Flush();
                return stream.ToArray();
            }
        }

        static PointF P(float x,float y) {return new PointF(x,y);}

        static void DrawCommand(Graphics g,Command command) {
            using(var outline=new Pen(Ink,2.3f) {LineJoin=LineJoin.Round,StartCap=LineCap.Round,EndCap=LineCap.Round}) {
                if(command==Command.Import) {
                    using(var fill=new SolidBrush(Color.FromArgb(223,236,250)))
                        g.FillPolygon(fill,new[]{P(4,19),P(10,19),P(11,22),P(21,22),P(22,19),P(28,19),P(28,28),P(4,28)});
                    g.DrawLines(outline,new[]{P(4,19),P(4,28),P(28,28),P(28,19)});
                    using(var arrow=new Pen(Blue,3.6f) {StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round}) {
                        g.DrawLine(arrow,16,4,16,18);
                        g.DrawLines(arrow,new[]{P(10,13),P(16,19),P(22,13)});
                    }
                    return;
                }
                if(command==Command.OneStageDesign||command==Command.TwoStageDesign) {
                    // A dimensioned drawing sheet.
                    var sheet=new[]{P(5,3),P(18,3),P(24,9),P(24,27),P(5,27)};
                    g.FillPolygon(Brushes.White,sheet);
                    g.DrawPolygon(outline,sheet);
                    g.DrawLines(outline,new[]{P(18,3),P(18,9),P(24,9)});
                    using(var dimension=new Pen(Blue,2f)) {
                        g.DrawEllipse(dimension,9,12,9,9);
                        g.DrawLine(dimension,8,22,20,11);
                    }
                } else {
                    // Isometric solid with three readable faces.
                    var top=new[]{P(14,3),P(26,9),P(14,15),P(2,9)};
                    var left=new[]{P(2,9),P(14,15),P(14,28),P(2,22)};
                    var right=new[]{P(14,15),P(26,9),P(26,22),P(14,28)};
                    using(var light=new SolidBrush(Color.FromArgb(213,232,251)))
                    using(var mid=new SolidBrush(Color.FromArgb(126,182,231)))
                    using(var deep=new SolidBrush(Color.FromArgb(82,142,201))) {
                        g.FillPolygon(light,top);g.FillPolygon(mid,left);g.FillPolygon(deep,right);
                    }
                    g.DrawPolygon(outline,new[]{P(14,3),P(26,9),P(26,22),P(14,28),P(2,22),P(2,9)});
                    g.DrawLines(outline,new[]{P(2,9),P(14,15),P(26,9)});
                    g.DrawLine(outline,14,15,14,28);
                }
            }
            bool two=command==Command.TwoStageDesign||command==Command.TwoStageModel;
            using(var badge=new SolidBrush(two?Teal:Blue)) {
                g.FillEllipse(Brushes.White,17,17,14,14);
                g.FillEllipse(badge,18,18,12,12);
            }
            using(var numeral=new Pen(Color.White,1.7f) {LineJoin=LineJoin.Round,StartCap=LineCap.Round,EndCap=LineCap.Round}) {
                if(two)g.DrawLines(numeral,new[]{P(21.6f,22),P(23,21),P(25.4f,21),P(26.2f,22.5f),P(21.6f,27),P(26.3f,27)});
                else g.DrawLines(numeral,new[]{P(22.2f,22.4f),P(24.2f,21),P(24.2f,27)});
            }
        }

        static void DrawGear(Graphics g,float x,float y,float radius,Color color) {
            var points=new PointF[32];
            for(int i=0;i<points.Length;i++) {
                double angle=(i+0.5)*Math.PI/16;
                float r=i%4==1||i%4==2?radius:radius*0.76f;
                points[i]=P(x+r*(float)Math.Cos(angle),y+r*(float)Math.Sin(angle));
            }
            using(var path=new GraphicsPath(FillMode.Alternate))
            using(var brush=new SolidBrush(color)) {
                path.AddPolygon(points);
                float bore=radius*0.34f;
                path.AddEllipse(x-bore,y-bore,2*bore,2*bore);
                g.FillPath(brush,path);
            }
        }

        // CommandManager expects 8-bit icon strips. Reserve index 0 for the
        // transparent background and use a fixed RGB palette for the glyphs.
        static void SaveIndexed(Bitmap source,string path) {
            using(var indexed=new Bitmap(source.Width,source.Height,PixelFormat.Format8bppIndexed)) {
                var palette=indexed.Palette;
                palette.Entries[0]=Color.FromArgb(0,192,192,192);
                for(int r=0;r<6;r++)
                    for(int g=0;g<6;g++)
                        for(int b=0;b<6;b++)
                            palette.Entries[1+r*36+g*6+b]=Color.FromArgb(r*51,g*51,b*51);
                indexed.Palette=palette;
                var data=indexed.LockBits(new Rectangle(0,0,indexed.Width,indexed.Height),ImageLockMode.WriteOnly,indexed.PixelFormat);
                try {
                    var row=new byte[indexed.Width];
                    for(int y=0;y<indexed.Height;y++) {
                        for(int x=0;x<row.Length;x++) {
                            Color pixel=source.GetPixel(x,y);
                            row[x]=pixel.A<128?(byte)0:(byte)(1+(pixel.R+25)/51*36+(pixel.G+25)/51*6+(pixel.B+25)/51);
                        }
                        Marshal.Copy(row,0,IntPtr.Add(data.Scan0,y*data.Stride),row.Length);
                    }
                } finally {indexed.UnlockBits(data);}
                indexed.Save(path,ImageFormat.Png);
            }
        }
    }
}
