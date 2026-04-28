#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using SixLabors.ImageSharp;
using SDImage = System.Drawing.Image;
using SDBitmap = System.Drawing.Bitmap;
using SDColor = System.Drawing.Color;

namespace DicomViewerApp
{
    public class DicomResult
    {
        public string MetadataText { get; set; } = "";
        public string StatusText { get; set; } = "";
        public SDImage Picture { get; set; }
        public double DefaultWC { get; set; }
        public double DefaultWW { get; set; }

        public int TotalFrames { get; set; } = 1;
        public bool IsMultiFrame { get; set; } = false;
    }

    // Renamed from Form2 to DicomEngine for professional architecture
    public class DicomEngine
    {
        public static DicomImage ActiveDicomImage;
        private static DicomDataset _activeDataset;
        private static int _totalFrames = 1;

        // ✨ FIX: 60-Frame Maximum RAM Cache for DICOM
        private const int MAX_CACHE_FRAMES = 60;
        private static Dictionary<int, SDImage> _frameCache = new Dictionary<int, SDImage>();
        private static Queue<int> _frameKeys = new Queue<int>(); // Tracks the oldest frames

        private static string _cachedFilePath = "";
        private static readonly object _imageLock = new object();

        public static DicomResult Process(string filePath, int currentSlice, int totalSlices)
        {
            var file = DicomFile.Open(filePath);
            var dataset = file.Dataset;
            _activeDataset = dataset;

            string patient = dataset.GetSingleValueOrDefault(DicomTag.PatientName, "Anonymous");
            string patientId = dataset.GetSingleValueOrDefault(DicomTag.PatientID, "N/A");
            string dob = dataset.GetSingleValueOrDefault(DicomTag.PatientBirthDate, "N/A");
            string sex = dataset.GetSingleValueOrDefault(DicomTag.PatientSex, "N/A");
            string modality = dataset.GetSingleValueOrDefault(DicomTag.Modality, "Unknown");
            string studyDate = dataset.GetSingleValueOrDefault(DicomTag.StudyDate, "N/A");
            string studyDescr = dataset.GetSingleValueOrDefault(DicomTag.StudyDescription, "N/A");
            string seriesDescr = dataset.GetSingleValueOrDefault(DicomTag.SeriesDescription, "N/A");
            string institution = dataset.GetSingleValueOrDefault(DicomTag.InstitutionName, "N/A");
            int rows = dataset.GetSingleValueOrDefault(DicomTag.Rows, 0);
            int cols = dataset.GetSingleValueOrDefault(DicomTag.Columns, 0);
            string pixelSp = dataset.GetSingleValueOrDefault(DicomTag.PixelSpacing, "N/A");
            string sliceNum = dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, "N/A");
            string sliceLoc = dataset.GetSingleValueOrDefault(DicomTag.SliceLocation, "N/A");

            _totalFrames = dataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1);
            bool isMultiFrame = _totalFrames > 1;

            string formattedText =
                "═══════════════════════════════\r\n" +
                "        DICOM METADATA         \r\n" +
                "═══════════════════════════════\r\n\r\n" +
                "── Patient ──────────────────\r\n" +
                $"  Name        : {patient}\r\n" +
                $"  ID          : {patientId}\r\n" +
                $"  DOB         : {dob}\r\n" +
                $"  Sex         : {sex}\r\n\r\n" +
                "── Study ────────────────────\r\n" +
                $"  Date        : {studyDate}\r\n" +
                $"  Modality    : {modality}\r\n" +
                $"  Description : {studyDescr}\r\n" +
                $"  Series      : {seriesDescr}\r\n" +
                $"  Institution : {institution}\r\n\r\n" +
                "── Image ────────────────────\r\n" +
                $"  Matrix      : {rows} x {cols}\r\n" +
                $"  Pixel Spc   : {pixelSp} mm\r\n" +
                $"  Instance #  : {sliceNum}\r\n" +
                $"  Slice Loc   : {sliceLoc} mm\r\n\r\n" +
                "── Series ───────────────────\r\n" +
                $"  File        : {currentSlice} / {totalSlices}\r\n" +
                $"  Frames      : {_totalFrames}\r\n" +
                $"  Name        : {Path.GetFileName(filePath)}\r\n";

            lock (_imageLock)
            {
                if (_cachedFilePath != filePath)
                {
                    ClearFrameCache();
                    _cachedFilePath = filePath;
                }

                ActiveDicomImage = new DicomImage(dataset);
                double wc = ActiveDicomImage.WindowCenter;
                double ww = ActiveDicomImage.WindowWidth;

                SDImage picture = RenderFrameInternal(0);

                return new DicomResult
                {
                    MetadataText = formattedText,
                    Picture = picture,
                    StatusText = $"Loaded: {Path.GetFileName(filePath)}  |  {modality}  |  {rows}x{cols}" +
                                   (isMultiFrame ? $"  |  {_totalFrames} frames — use slider to scroll" : ""),
                    DefaultWC = wc,
                    DefaultWW = ww,
                    TotalFrames = _totalFrames,
                    IsMultiFrame = isMultiFrame
                };
            }
        }

        public static SDImage RenderFrame(int frameIndex)
        {
            lock (_imageLock)
            {
                return RenderFrameInternal(frameIndex);
            }
        }

        private static SDImage RenderFrameInternal(int frameIndex)
        {
            if (ActiveDicomImage == null || _activeDataset == null)
                return MakeErrorBitmap("No DICOM loaded.");

            frameIndex = Math.Max(0, Math.Min(frameIndex, _totalFrames - 1));

            // Check if it's already in the cache
            if (_frameCache.TryGetValue(frameIndex, out var cached))
            {
                lock (cached)
                {
                    return (SDImage)cached.Clone();
                }
            }

            // ✨ FIX: Enforce Maximum Cache Size
            if (_frameCache.Count >= MAX_CACHE_FRAMES)
            {
                int oldestFrame = _frameKeys.Dequeue();
                if (_frameCache.TryGetValue(oldestFrame, out var oldBmp))
                {
                    oldBmp.Dispose();
                    _frameCache.Remove(oldestFrame);
                }
            }

            // Render new frame
            SDImage result = RenderWithFallback(_activeDataset, ActiveDicomImage, frameIndex);

            // Store in cache
            _frameKeys.Enqueue(frameIndex);
            _frameCache[frameIndex] = (SDImage)result.Clone();

            return result;
        }

        public static SDImage AdjustContrast(double windowCenter, double windowWidth, int frameIndex = 0)
        {
            lock (_imageLock)
            {
                if (ActiveDicomImage == null || _activeDataset == null) return null;

                ClearFrameCache(); // Must clear cache because all frames need the new contrast

                ActiveDicomImage.WindowCenter = windowCenter;
                ActiveDicomImage.WindowWidth = windowWidth;

                return RenderFrameInternal(frameIndex);
            }
        }

        private static SDImage RenderWithFallback(DicomDataset dataset, DicomImage dicomImage, int frameIndex)
        {
            try
            {
                using var rendered = dicomImage.RenderImage(frameIndex).As<SixLabors.ImageSharp.Image>();
                using var ms = new MemoryStream();
                rendered.SaveAsBmp(ms);
                ms.Position = 0;

                using var tempBmp = new SDBitmap(ms);
                return new SDBitmap(tempBmp);
            }
            catch
            {
                return FallbackFrameDecode(dataset, frameIndex);
            }
        }

        private static SDImage FallbackFrameDecode(DicomDataset dataset, int frameIndex)
        {
            try
            {
                var pixelData = DicomPixelData.Create(dataset);
                var frameBytes = pixelData.GetFrame(frameIndex).Data;

                using var ms = new MemoryStream(frameBytes);
                using var tempBmp = new SDBitmap(ms);
                return new SDBitmap(tempBmp);
            }
            catch (Exception ex)
            {
                return MakeErrorBitmap($"Cannot decode frame {frameIndex}:\n{ex.Message}");
            }
        }

        public static void ClearFrameCache()
        {
            foreach (var img in _frameCache.Values) img?.Dispose();
            _frameCache.Clear();
            _frameKeys.Clear(); // ✨ FIX: Clear the queue too
        }

        private static SDBitmap MakeErrorBitmap(string message)
        {
            var bmp = new SDBitmap(512, 256);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.Clear(SDColor.FromArgb(20, 20, 20));
            g.DrawString(message,
                new System.Drawing.Font("Consolas", 11f),
                System.Drawing.Brushes.OrangeRed,
                new System.Drawing.RectangleF(10, 10, 490, 230));
            return bmp;
        }
    }
}