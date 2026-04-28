using System;
using System.Drawing;

namespace DicomViewerApp
{
    public class TissueRenderer
    {
        public byte[]? RawData { get; set; } // Added ?
        public int Width { get; set; }
        public int Height { get; set; }
        public double FovRadians { get; set; } = 90.0 * Math.PI / 180.0;

        public Image? GenerateFrame(int frame, int matrixRotation, double contrast, double gamma) // Added ?
        {
            if (RawData == null) return null;

            int effW = (matrixRotation % 2 != 0) ? Height : Width;
            int effH = (matrixRotation % 2 != 0) ? Width : Height;

            double startAngle = -FovRadians / 2.0;
            double angleStep = FovRadians / (effW - 1);

            int outW = (int)(2 * effH * Math.Sin(FovRadians / 2.0)) + 40;
            int outH = effH + 40;
            int polarOriginX = outW / 2;
            int polarOriginY = 20;

            var bmp = new Bitmap(outW, outH);
            int tOffset = frame * Width * Height;

            for (int y = 0; y < outH; y++)
            {
                for (int x = 0; x < outW; x++)
                {
                    double dx = x - polarOriginX, dy = y - polarOriginY;
                    double r = Math.Sqrt(dx * dx + dy * dy), theta = Math.Atan2(dx, dy);

                    if (r < effH && theta >= startAngle && theta <= -startAngle)
                    {
                        int rY = (int)r, rX = (int)((theta - startAngle) / angleStep);
                        rY = Math.Max(0, Math.Min(effH - 1, rY));
                        rX = Math.Max(0, Math.Min(effW - 1, rX));

                        int origX = rX, origY = rY;
                        if (matrixRotation == 1) { origX = rY; origY = effW - 1 - rX; }
                        else if (matrixRotation == 2) { origX = effW - 1 - rX; origY = effH - 1 - rY; }
                        else if (matrixRotation == 3) { origX = effH - 1 - rY; origY = rX; }

                        int tIdx = tOffset + (origY * Width) + origX;
                        if (tIdx >= 0 && tIdx < RawData.Length)
                        {
                            byte rawV = RawData[tIdx];

                            double norm = rawV / 255.0;
                            norm = Math.Pow(norm, gamma) * contrast;
                            byte finalV = (byte)Math.Min(255, Math.Max(0, norm * 255));

                            bmp.SetPixel(x, y, Color.FromArgb(255, finalV, finalV, finalV));
                        }
                    }
                }
            }
            return bmp;
        }
    }
}