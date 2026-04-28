#nullable disable
using System;
using System.Drawing;

namespace DicomViewerApp
{
    public class FlowRenderer
    {
        public float[] CfdData { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int Comps { get; set; } = 1;

        // NEW: Color Box (Region of Interest) Parameters
        public double RoiTop { get; set; } = 0.0;
        public double RoiBottom { get; set; } = 1.0;
        public double RoiLeft { get; set; } = 0.0;
        public double RoiRight { get; set; } = 1.0;

        public Image GenerateFlowFrame(int frame, int maxCfdFrame, int matrixRotation, double fovRadians, int tW, int tH, double noiseGate, double maxVelocity)
        {
            if (CfdData == null) return null;

            int effW = (matrixRotation % 2 != 0) ? tH : tW;
            int effH = (matrixRotation % 2 != 0) ? tW : tH;

            double startAngle = -fovRadians / 2.0;
            double angleStep = fovRadians / (effW - 1);

            int outW = (int)(2 * effH * Math.Sin(fovRadians / 2.0)) + 40;
            int outH = effH + 40;
            int polarOriginX = outW / 2;
            int polarOriginY = 20;

            var bmp = new Bitmap(outW, outH);
            int safeFrame = Math.Max(0, Math.Min(frame, maxCfdFrame));
            int cfdOffset = safeFrame * Width * Height * Comps;

            if (maxVelocity <= 0.01) maxVelocity = 0.41;

            // Calculate pixel bounds for the Color Box
            int minY = (int)(RoiTop * effH);
            int maxY = (int)(RoiBottom * effH);
            int minX = (int)(RoiLeft * effW);
            int maxX = (int)(RoiRight * effW);

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

                        // THE COLOR BOX MASK: Skip drawing if outside the user's box!
                        if (rY < minY || rY > maxY || rX < minX || rX > maxX) continue;

                        int origX = rX, origY = rY;
                        if (matrixRotation == 1) { origX = rY; origY = effW - 1 - rX; }
                        else if (matrixRotation == 2) { origX = effW - 1 - rX; origY = effH - 1 - rY; }
                        else if (matrixRotation == 3) { origX = effH - 1 - rY; origY = rX; }

                        int cfdOrigX = (origX * Width) / tW;
                        int cfdOrigY = (origY * Height) / tH;
                        int cIdx = cfdOffset + (cfdOrigY * Width * Comps) + (cfdOrigX * Comps);

                        if (cIdx < CfdData.Length)
                        {
                            float vy = (Comps >= 3) ? CfdData[cIdx + 1] : CfdData[cIdx];

                            if (float.IsNaN(vy) || float.IsInfinity(vy)) continue;

                            double normalizedVy = vy / maxVelocity;
                            normalizedVy = Math.Max(-1.0, Math.Min(1.0, normalizedVy));

                            if (Math.Abs(normalizedVy) > noiseGate)
                            {
                                Color flowColor;
                                if (normalizedVy < 0)
                                {
                                    int red = 255;
                                    int green = (int)(255 * Math.Abs(normalizedVy));
                                    flowColor = Color.FromArgb(220, red, green, 0);
                                }
                                else
                                {
                                    int blue = 255;
                                    int green = (int)(255 * Math.Abs(normalizedVy));
                                    flowColor = Color.FromArgb(220, 0, green, blue);
                                }
                                bmp.SetPixel(x, y, flowColor);
                            }
                        }
                    }
                }
            }
            return bmp;
        }
    }
}