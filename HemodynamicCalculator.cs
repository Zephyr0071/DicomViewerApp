#nullable disable

using System;
using System.Threading.Tasks;

namespace DicomViewerApp
{
    public class FrameMetrics
    {
        public double PeakVelocity { get; set; }
        public double MeanVelocity { get; set; }
        public int ValidDataPoints { get; set; }
        public double MaxVorticity { get; set; }
        public double TotalEnergyLoss { get; set; }
        public double[] SpatialEnergyMap { get; set; }
        public double PressureDrop { get; set; }
    }

    public static class HemodynamicCalculator
    {
        private const double BloodViscosity = 0.004;

        private class ThreadLocalMetrics
        {
            public double MaxVel = 0;
            public double SumVel = 0;
            public int ValidCount = 0;
            public double MaxVort = 0;
            public double TotalEnergy = 0;
        }

        public static FrameMetrics AnalyzeFrame(
            float[] vTheta, float[] vRadial,
            int vDepth, int vWidth, int frameIndex,
            float[] powerRaw, double powerGate)
        {
            if (vTheta == null || vRadial == null) return new FrameMetrics();

            int frameOffset = frameIndex * vDepth * vWidth;
            object lockObj = new object();

            double globalMaxVelocity = 0;
            double globalSumVelocity = 0;
            int globalValidCount = 0;
            double globalMaxVorticity = 0;
            double globalTotalEnergyLoss = 0;

            double[] spatialMap = new double[vDepth * vWidth];

            Parallel.For(1, vDepth - 1,
                () => new ThreadLocalMetrics(),
                (y, loopState, localBucket) =>
                {
                    for (int x = 1; x < vWidth - 1; x++)
                    {
                        int globalIndex = frameOffset + (y * vWidth) + x;
                        int localIndex = (y * vWidth) + x;

                        if (globalIndex >= vTheta.Length) break;

                        if (powerRaw != null && Math.Log10(Math.Max(1.0, powerRaw[globalIndex])) < powerGate)
                            continue;

                        float u = vTheta[globalIndex];
                        float v = vRadial[globalIndex];

                        if (float.IsNaN(u) || float.IsInfinity(u) || float.IsNaN(v) || float.IsInfinity(v))
                            continue;

                        double magnitude = Math.Sqrt(u * u + v * v);

                        if (magnitude > 0.02)
                        {
                            if (magnitude > localBucket.MaxVel) localBucket.MaxVel = magnitude;
                            localBucket.SumVel += magnitude;
                            localBucket.ValidCount++;

                            int right = frameOffset + (y * vWidth) + (x + 1);
                            int left = frameOffset + (y * vWidth) + (x - 1);
                            int up = frameOffset + ((y - 1) * vWidth) + x;
                            int down = frameOffset + ((y + 1) * vWidth) + x;

                            double dudx = (vTheta[right] - vTheta[left]) / 2.0;
                            double dudy = (vTheta[down] - vTheta[up]) / 2.0;
                            double dvdx = (vRadial[right] - vRadial[left]) / 2.0;
                            double dvdy = (vRadial[down] - vRadial[up]) / 2.0;

                            double vorticity = Math.Abs(dvdx - dudy);
                            if (vorticity > localBucket.MaxVort) localBucket.MaxVort = vorticity;

                            double dissipation = BloodViscosity * (
                                2.0 * Math.Pow(dudx, 2) +
                                2.0 * Math.Pow(dvdy, 2) +
                                Math.Pow(dudy + dvdx, 2)
                            );

                            if (!double.IsNaN(dissipation) && !double.IsInfinity(dissipation))
                            {
                                localBucket.TotalEnergy += dissipation;
                                spatialMap[localIndex] = dissipation;
                            }
                        }
                    }
                    return localBucket;
                },
                (localBucket) =>
                {
                    lock (lockObj)
                    {
                        if (localBucket.MaxVel > globalMaxVelocity) globalMaxVelocity = localBucket.MaxVel;
                        globalSumVelocity += localBucket.SumVel;
                        globalValidCount += localBucket.ValidCount;
                        if (localBucket.MaxVort > globalMaxVorticity) globalMaxVorticity = localBucket.MaxVort;
                        globalTotalEnergyLoss += localBucket.TotalEnergy;
                    }
                }
            );

            return new FrameMetrics
            {
                PeakVelocity = globalMaxVelocity,
                MeanVelocity = globalValidCount > 0 ? globalSumVelocity / globalValidCount : 0,
                ValidDataPoints = globalValidCount,
                MaxVorticity = globalMaxVorticity,
                TotalEnergyLoss = globalTotalEnergyLoss,
                SpatialEnergyMap = spatialMap,
                PressureDrop = 4.0 * (globalMaxVelocity * globalMaxVelocity)
            };
        }
    }
}