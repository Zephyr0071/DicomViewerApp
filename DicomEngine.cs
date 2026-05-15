#nullable enable

using FellowOakDicom;
using FellowOakDicom.Imaging;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Windows.Media.Media3D;
using SDBitmap = System.Drawing.Bitmap;
using SDColor = System.Drawing.Color;
using SDImage = System.Drawing.Image;

namespace DicomViewerApp
{
    public class DicomResult
    {
        public string MetadataText { get; set; } = "";
        public string StatusText { get; set; } = "";
        public SDImage? Picture { get; set; }
        public double DefaultWC { get; set; }
        public double DefaultWW { get; set; }
        public int TotalFrames { get; set; } = 1;
        public bool IsMultiFrame { get; set; } = false;
    }

    public class DicomEngine
    {
        public static DicomImage? ActiveDicomImage;
        private static DicomDataset? _activeDataset;
        private static int _totalFrames = 1;
        private static byte[]? _currentProjectBlob = null;
        private static int _projectWidth = 512, _projectHeight = 512;
        private static float _projectMaxVal = 1.0f;

        public static void Reset()
        {
            ActiveDicomImage = null;
            _activeDataset = null;
            _currentProjectBlob = null;
        }

        // ✨ THE 3D DECIMATOR: Safely shrinks massive 1GB .itsf files
        public static MeshGeometry3D BuildNative3DMesh(string filePath)
        {
            byte[]? pointData = null;
            byte[]? cellData = null;
            byte[]? velocityData = null;

            using (ZipArchive archive = ZipFile.OpenRead(filePath))
            {
                var pEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".point") && e.Name.StartsWith("Volume"));
                var cEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".cells4") && e.Name.StartsWith("Volume"));
                var vEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".U"));

                if (pEntry != null) { using var ms = new MemoryStream(); pEntry.Open().CopyTo(ms); pointData = ms.ToArray(); }
                if (cEntry != null) { using var ms = new MemoryStream(); cEntry.Open().CopyTo(ms); cellData = ms.ToArray(); }
                if (vEntry != null) { using var ms = new MemoryStream(); vEntry.Open().CopyTo(ms); velocityData = ms.ToArray(); }
            }

            if (pointData == null || cellData == null) throw new Exception("Invalid 3D Geometry Data.");

            var mesh = new MeshGeometry3D();
            int totalOriginalPoints = pointData.Length / 12;

            // Dynamically skip data to fit inside GPU RAM safely
            int triangleStep = (cellData.Length > 200_000_000) ? 8 : (cellData.Length > 50_000_000 ? 4 : 1);

            int estNewTriangles = cellData.Length / (16 * triangleStep);
            var positions = new Point3DCollection();
            var texCoords = new System.Windows.Media.PointCollection();
            var indices = new System.Windows.Media.Int32Collection();

            int[] indexMap = new int[totalOriginalPoints];
            Array.Fill(indexMap, -1);
            int currentNewIndex = 0;

            for (int i = 0; i < cellData.Length - 15; i += 16 * triangleStep)
            {
                int p1 = BitConverter.ToInt32(cellData, i);
                int p2 = BitConverter.ToInt32(cellData, i + 4);
                int p3 = BitConverter.ToInt32(cellData, i + 8);
                int p4 = BitConverter.ToInt32(cellData, i + 12);

                // Drop any cell containing a boundary marker or out-of-bounds index
                if (p1 < 0 || p1 >= totalOriginalPoints || p2 < 0 || p2 >= totalOriginalPoints ||
                    p3 < 0 || p3 >= totalOriginalPoints || p4 < 0 || p4 >= totalOriginalPoints)
                    continue;

                int[] pts = { p1, p2, p3, p4 };
                foreach (int p in pts)
                {
                    if (indexMap[p] == -1)
                    {
                        indexMap[p] = currentNewIndex++;
                        positions.Add(new System.Windows.Media.Media3D.Point3D(
                            BitConverter.ToSingle(pointData, p * 12),
                            BitConverter.ToSingle(pointData, p * 12 + 4),
                            BitConverter.ToSingle(pointData, p * 12 + 8)
                        ));

                        float vel = 0;
                        if (velocityData != null && (p * 4) + 3 < velocityData.Length)
                            vel = Math.Abs(BitConverter.ToSingle(velocityData, p * 4));

                        texCoords.Add(new System.Windows.Point(Math.Min(1.0f, vel / 1.5f), 0));
                    }
                }

                indices.Add(indexMap[p1]); indices.Add(indexMap[p3]); indices.Add(indexMap[p2]);
                indices.Add(indexMap[p1]); indices.Add(indexMap[p2]); indices.Add(indexMap[p4]);
                indices.Add(indexMap[p1]); indices.Add(indexMap[p4]); indices.Add(indexMap[p3]);
                indices.Add(indexMap[p2]); indices.Add(indexMap[p3]); indices.Add(indexMap[p4]);
            }

            mesh.Positions = positions;
            mesh.TextureCoordinates = texCoords;
            mesh.TriangleIndices = indices;

            mesh.Freeze();
            return mesh;
        }

        public static DicomResult Process(string filePath, int currentIndex, int totalCount)
        {
            _currentProjectBlob = null;

            if (filePath.EndsWith(".itsp", StringComparison.OrdinalIgnoreCase))
                return ProcessItsp(filePath);

            try
            {
                var file = DicomFile.Open(filePath);
                _activeDataset = file.Dataset;
                ActiveDicomImage = new DicomImage(_activeDataset);
                _totalFrames = _activeDataset.GetSingleValueOrDefault(DicomTag.NumberOfFrames, 1);

                return new DicomResult
                {
                    MetadataText = $"Loaded DICOM: {Path.GetFileName(filePath)}\r\nFrames: {_totalFrames}",
                    Picture = RenderFrame(0),
                    DefaultWC = ActiveDicomImage.WindowCenter,
                    DefaultWW = ActiveDicomImage.WindowWidth,
                    TotalFrames = _totalFrames,
                    IsMultiFrame = _totalFrames > 1,
                    StatusText = "DICOM Loaded Successfully"
                };
            }
            catch (Exception ex)
            {
                return new DicomResult { MetadataText = $"DICOM Error: {ex.Message}", StatusText = "Error" };
            }
        }

        private static DicomResult ProcessItsp(string filePath)
        {
            byte[]? dataBlob = null;
            string metadataLog = "── iTFLOW .ITSP PROJECT ──\r\n\r\n";
            var bmp = new SDBitmap(512, 512);

            try
            {
                using (ZipArchive archive = ZipFile.OpenRead(filePath))
                {
                    var geoEntry = archive.Entries.OrderByDescending(e => e.Length).FirstOrDefault();
                    if (geoEntry != null)
                    {
                        using (var ms = new MemoryStream()) { geoEntry.Open().CopyTo(ms); dataBlob = ms.ToArray(); }
                        metadataLog += $"Data Blob Found: {geoEntry.Name}\r\n";
                    }
                }

                if (dataBlob != null && dataBlob.Length > 1024)
                {
                    int w = 512, h = 512;
                    int bytesPerSlice = w * h * 2;
                    int totalSlices = (dataBlob.Length - 512) / bytesPerSlice;
                    short maxFound = 1000;

                    metadataLog += $"Matrix: {w}x{h}\r\nSlices/Frames: {totalSlices}\r\n";
                    _currentProjectBlob = dataBlob;
                    _projectWidth = w; _projectHeight = h; _projectMaxVal = maxFound;

                    return new DicomResult
                    {
                        MetadataText = metadataLog,
                        Picture = RenderItspSlice(dataBlob, 0, w, h, maxFound),
                        StatusText = "ITSP Loaded",
                        TotalFrames = totalSlices,
                        IsMultiFrame = true
                    };
                }
            }
            catch (Exception ex) { metadataLog += "Error: " + ex.Message; }

            return new DicomResult { MetadataText = metadataLog, Picture = bmp, TotalFrames = 1 };
        }

        private static SDBitmap RenderItspSlice(byte[] blob, int frameIndex, int w, int h, float maxVal)
        {
            var bmp = new SDBitmap(w, h);
            int sliceStart = 512 + (frameIndex * w * h * 2);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = sliceStart + (y * w + x) * 2;
                    if (idx + 1 < blob.Length)
                    {
                        short val = BitConverter.ToInt16(blob, idx);
                        int intensity = Math.Clamp((int)(((val + 1000f) / 2000f) * 255f), 0, 255);
                        bmp.SetPixel(x, y, SDColor.FromArgb(intensity, intensity, intensity));
                    }
                }
            }
            return bmp;
        }

        public static SDImage? RenderFrame(int frameIndex)
        {
            if (_currentProjectBlob != null) return RenderItspSlice(_currentProjectBlob, frameIndex, _projectWidth, _projectHeight, _projectMaxVal);
            if (ActiveDicomImage == null) return null;

            return ActiveDicomImage.RenderImage(frameIndex).As<SDBitmap>();
        }

        public static SDImage? AdjustContrast(double wc, double ww, int frame)
        {
            if (ActiveDicomImage == null) return null;
            ActiveDicomImage.WindowCenter = wc; ActiveDicomImage.WindowWidth = ww;
            return RenderFrame(frame);
        }
    }
}