#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace DicomViewerApp
{
    public class ItsfMesh
    {
        public float[] Positions = Array.Empty<float>();
        public float[] Normals = Array.Empty<float>();
        public uint[] Indices = Array.Empty<uint>();

        public Dictionary<string, float[]> ScalarFields = new();
        public string ActiveScalar = "";

        public string SourcePath = "";
        public string FormatDetected = "Unknown";
        public string MetaInfo = "";

        public int VertexCount => Positions.Length / 3;
        public int TriangleCount => Indices.Length / 3;

        public (float min, float max) ActiveScalarRange()
        {
            if (!ScalarFields.TryGetValue(ActiveScalar, out var f) || f.Length == 0) return (0, 1);
            float mn = float.MaxValue, mx = float.MinValue;
            foreach (var v in f) { if (v < mn) mn = v; if (v > mx) mx = v; }
            return (mn, mx == mn ? mn + 1 : mx);
        }
    }

    public static class ItsfParser
    {
        // ✨ PARAVIEW EXTRACTOR: Rapidly hashes triangle coordinates to find out if they are internal or external
        private struct FaceKey : IEquatable<FaceKey>
        {
            public readonly int A, B, C;
            public FaceKey(int a, int b, int c)
            {
                if (a > b) { int t = a; a = b; b = t; }
                if (b > c) { int t = b; b = c; c = t; }
                if (a > b) { int t = a; a = b; b = t; }
                A = a; B = b; C = c;
            }
            public bool Equals(FaceKey other) => A == other.A && B == other.B && C == other.C;
            public override int GetHashCode() => HashCode.Combine(A, B, C);
        }

        public static ItsfMesh Load(string path)
        {
            var mesh = new ItsfMesh { SourcePath = path };

            byte[] raw;
            try { raw = File.ReadAllBytes(path); }
            catch (Exception ex) { mesh.MetaInfo = "Read error: " + ex.Message; return FallbackSphere(mesh); }

            if (raw.Length >= 4 && raw[0] == 0x50 && raw[1] == 0x4B)
                return ParseZip(path, raw, mesh);

            if (TryParseBinaryStl(raw, mesh)) return mesh;
            if (raw.Length > 6 && Encoding.ASCII.GetString(raw, 0, 5) == "solid" && TryParseAsciiStl(raw, mesh)) return mesh;
            if (raw.Length > 4 && Encoding.ASCII.GetString(raw, 0, 3) == "ply" && TryParsePly(raw, mesh)) return mesh;
            if (TryParseRawFloats(raw, mesh)) return mesh;

            mesh.MetaInfo = $"Format not recognised ({raw.Length / 1024} KB). Showing placeholder mesh.";
            return FallbackSphere(mesh);
        }

        private static ItsfMesh ParseZip(string path, byte[] raw, ItsfMesh mesh)
        {
            try
            {
                using var zip = ZipFile.OpenRead(path);

                var pEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".point", StringComparison.OrdinalIgnoreCase) && e.Name.StartsWith("Volume"));
                var cEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".cells4", StringComparison.OrdinalIgnoreCase) && e.Name.StartsWith("Volume"));
                var vEntry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".U", StringComparison.OrdinalIgnoreCase));

                if (pEntry != null && cEntry != null)
                {
                    byte[] pointData, cellData;
                    byte[]? velocityData = null;

                    using (var ms = new MemoryStream()) { pEntry.Open().CopyTo(ms); pointData = ms.ToArray(); }
                    using (var ms = new MemoryStream()) { cEntry.Open().CopyTo(ms); cellData = ms.ToArray(); }
                    if (vEntry != null) { using var ms = new MemoryStream(); vEntry.Open().CopyTo(ms); velocityData = ms.ToArray(); }

                    int totalOriginalPoints = pointData.Length / 12;
                    int validCells = cellData.Length / 16;

                    // ✨ PASS 1: Scan the 1GB file and map every single triangle's neighbors
                    var faceCounts = new Dictionary<FaceKey, int>(validCells * 2);

                    void CountFace(int a, int b, int c)
                    {
                        var k = new FaceKey(a, b, c);
                        faceCounts.TryGetValue(k, out int count);
                        faceCounts[k] = count + 1;
                    }

                    for (int i = 0; i < validCells; i++)
                    {
                        int p1 = BitConverter.ToInt32(cellData, i * 16);
                        int p2 = BitConverter.ToInt32(cellData, i * 16 + 4);
                        int p3 = BitConverter.ToInt32(cellData, i * 16 + 8);
                        int p4 = BitConverter.ToInt32(cellData, i * 16 + 12);

                        if (p1 < 0 || p1 >= totalOriginalPoints || p4 >= totalOriginalPoints) continue;

                        CountFace(p1, p3, p2);
                        CountFace(p1, p2, p4);
                        CountFace(p1, p4, p3);
                        CountFace(p2, p3, p4);
                    }

                    // ✨ PASS 2: We ONLY extract the outer skin of the Aorta! (No more internal triangle lag)
                    var positions = new List<float>();
                    var indices = new List<uint>();
                    var scalars = new List<float>();

                    int[] indexMap = new int[totalOriginalPoints];
                    Array.Fill(indexMap, -1);
                    uint currentNewIndex = 0;

                    void ProcessFace(int a, int b, int c)
                    {
                        if (faceCounts[new FaceKey(a, b, c)] == 1)
                        { // Count == 1 means it's on the outside!
                            int[] pts = { a, b, c };
                            foreach (int p in pts)
                            {
                                if (indexMap[p] == -1)
                                {
                                    indexMap[p] = (int)currentNewIndex++;

                                    positions.Add(BitConverter.ToSingle(pointData, p * 12));
                                    positions.Add(BitConverter.ToSingle(pointData, p * 12 + 4));
                                    positions.Add(BitConverter.ToSingle(pointData, p * 12 + 8));

                                    float vel = 0;
                                    if (velocityData != null && (p * 4) + 3 < velocityData.Length)
                                        vel = Math.Abs(BitConverter.ToSingle(velocityData, p * 4));
                                    scalars.Add(vel);
                                }
                            }
                            indices.Add((uint)indexMap[a]);
                            indices.Add((uint)indexMap[b]);
                            indices.Add((uint)indexMap[c]);
                        }
                    }

                    for (int i = 0; i < validCells; i++)
                    {
                        int p1 = BitConverter.ToInt32(cellData, i * 16);
                        int p2 = BitConverter.ToInt32(cellData, i * 16 + 4);
                        int p3 = BitConverter.ToInt32(cellData, i * 16 + 8);
                        int p4 = BitConverter.ToInt32(cellData, i * 16 + 12);

                        if (p1 < 0 || p1 >= totalOriginalPoints || p4 >= totalOriginalPoints) continue;

                        ProcessFace(p1, p3, p2);
                        ProcessFace(p1, p2, p4);
                        ProcessFace(p1, p4, p3);
                        ProcessFace(p2, p3, p4);
                    }

                    mesh.Positions = positions.ToArray();
                    mesh.Indices = indices.ToArray();
                    mesh.Normals = ComputeNormals(mesh.Positions, mesh.Indices); // Generates perfect smooth shading

                    mesh.ScalarFields["Velocity"] = scalars.ToArray();
                    mesh.ActiveScalar = "Velocity";

                    mesh.FormatDetected = "iTFlow 3D Native (.point + .cells4)";
                    mesh.MetaInfo = $"Surface Extracted Perfectly.\nRemoved {validCells * 4 - mesh.TriangleCount:N0} hidden interior triangles!";

                    return mesh;
                }

                // Standard STL/PLY fallback inside ZIP
                var geoEntry = zip.Entries.OrderByDescending(e => e.Length).FirstOrDefault(e => e.Name.EndsWith(".stl", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".ply", StringComparison.OrdinalIgnoreCase));
                geoEntry ??= zip.Entries.OrderByDescending(e => e.Length).FirstOrDefault();

                if (geoEntry == null) { mesh.MetaInfo = "ZIP is empty."; return FallbackSphere(mesh); }

                byte[] inner;
                using (var ms = new MemoryStream()) { geoEntry.Open().CopyTo(ms); inner = ms.ToArray(); }

                mesh.MetaInfo = $"ZIP entry: {geoEntry.Name} ({inner.Length / 1024} KB)\n";

                var innerMesh = new ItsfMesh { SourcePath = path };
                if (TryParseBinaryStl(inner, innerMesh)) { CopyGeometry(innerMesh, mesh); return mesh; }
                if (TryParsePly(inner, innerMesh)) { CopyGeometry(innerMesh, mesh); return mesh; }
                if (TryParseRawFloats(inner, innerMesh)) { CopyGeometry(innerMesh, mesh); return mesh; }

                if (mesh.VertexCount == 0) return FallbackSphere(mesh);
            }
            catch (Exception ex)
            {
                mesh.MetaInfo = "ZIP error: " + ex.Message;
                return FallbackSphere(mesh);
            }
            return mesh;
        }

        private static void CopyGeometry(ItsfMesh src, ItsfMesh dst)
        {
            dst.Positions = src.Positions; dst.Normals = src.Normals; dst.Indices = src.Indices;
            dst.FormatDetected = src.FormatDetected;
            dst.MetaInfo += src.MetaInfo;
        }

        private static bool TryParseBinaryStl(byte[] raw, ItsfMesh mesh)
        {
            if (raw.Length < 84) return false;
            uint triCount = BitConverter.ToUInt32(raw, 80);
            long expected = 84L + triCount * 50L;
            if (Math.Abs(raw.Length - expected) > 512 || triCount == 0 || triCount > 20_000_000) return false;

            var pos = new List<float>((int)(triCount * 9));
            var nrm = new List<float>((int)(triCount * 9));
            var idx = new List<uint>((int)(triCount * 3));
            uint vi = 0;

            int off = 84;
            for (uint t = 0; t < triCount; t++, off += 50)
            {
                float nx = BitConverter.ToSingle(raw, off);
                float ny = BitConverter.ToSingle(raw, off + 4);
                float nz = BitConverter.ToSingle(raw, off + 8);
                for (int v = 0; v < 3; v++)
                {
                    int vOff = off + 12 + v * 12;
                    pos.Add(BitConverter.ToSingle(raw, vOff));
                    pos.Add(BitConverter.ToSingle(raw, vOff + 4));
                    pos.Add(BitConverter.ToSingle(raw, vOff + 8));
                    nrm.Add(nx); nrm.Add(ny); nrm.Add(nz);
                    idx.Add(vi++);
                }
            }

            mesh.Positions = pos.ToArray();
            mesh.Normals = nrm.ToArray();
            mesh.Indices = idx.ToArray();
            mesh.FormatDetected = "Binary STL";
            mesh.MetaInfo = $"Binary STL — {triCount} triangles\n";
            return true;
        }

        private static bool TryParseAsciiStl(byte[] raw, ItsfMesh mesh)
        {
            try
            {
                string text = Encoding.UTF8.GetString(raw);
                var pos = new List<float>(); var nrm = new List<float>(); var idx = new List<uint>();
                float nx = 0, ny = 0, nz = 0; uint vi = 0;
                foreach (var rawLine in text.Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line.StartsWith("facet normal"))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 5)
                        { nx = P(parts[2]); ny = P(parts[3]); nz = P(parts[4]); }
                    }
                    else if (line.StartsWith("vertex"))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 4)
                        { pos.Add(P(parts[1])); pos.Add(P(parts[2])); pos.Add(P(parts[3])); nrm.Add(nx); nrm.Add(ny); nrm.Add(nz); idx.Add(vi++); }
                    }
                }
                if (vi < 3) return false;
                mesh.Positions = pos.ToArray(); mesh.Normals = nrm.ToArray(); mesh.Indices = idx.ToArray();
                mesh.FormatDetected = "ASCII STL";
                mesh.MetaInfo = $"ASCII STL — {vi / 3} triangles\n";
                return true;
            }
            catch { return false; }
        }

        private static bool TryParsePly(byte[] raw, ItsfMesh mesh)
        {
            try
            {
                int headerEnd = FindHeaderEnd(raw);
                if (headerEnd < 0) return false;
                string header = Encoding.ASCII.GetString(raw, 0, headerEnd);
                var lines = header.Split('\n').Select(l => l.Trim()).ToArray();

                bool isBinary = lines.Any(l => l.StartsWith("format binary_little_endian"));
                bool isAscii = lines.Any(l => l.StartsWith("format ascii"));
                if (!isBinary && !isAscii) return false;

                int vertexCount = 0, faceCount = 0;
                foreach (var l in lines)
                {
                    if (l.StartsWith("element vertex")) int.TryParse(l.Split(' ').Last(), out vertexCount);
                    if (l.StartsWith("element face")) int.TryParse(l.Split(' ').Last(), out faceCount);
                }
                if (vertexCount == 0) return false;

                var props = lines.Where(l => l.StartsWith("property float") || l.StartsWith("property double"))
                    .Select(l => l.Split(' ').Last()).ToList();
                int xi = props.IndexOf("x"), yi = props.IndexOf("y"), zi = props.IndexOf("z");
                int nxi = props.IndexOf("nx"), nyi = props.IndexOf("ny"), nzi = props.IndexOf("nz");
                bool hasNormals = nxi >= 0 && nyi >= 0 && nzi >= 0;
                int propCount = props.Count;

                var positions = new float[vertexCount * 3];
                var normals = hasNormals ? new float[vertexCount * 3] : null;

                if (isBinary)
                {
                    int off = headerEnd + 1;
                    int stride = propCount * 4;
                    for (int v = 0; v < vertexCount; v++, off += stride)
                    {
                        positions[v * 3] = BitConverter.ToSingle(raw, off + xi * 4);
                        positions[v * 3 + 1] = BitConverter.ToSingle(raw, off + yi * 4);
                        positions[v * 3 + 2] = BitConverter.ToSingle(raw, off + zi * 4);
                        if (hasNormals && normals != null)
                        {
                            normals[v * 3] = BitConverter.ToSingle(raw, off + nxi * 4);
                            normals[v * 3 + 1] = BitConverter.ToSingle(raw, off + nyi * 4);
                            normals[v * 3 + 2] = BitConverter.ToSingle(raw, off + nzi * 4);
                        }
                    }
                    var indices = new List<uint>();
                    for (int f = 0; f < faceCount; f++)
                    {
                        byte count = raw[off++];
                        uint i0 = BitConverter.ToUInt32(raw, off); off += 4;
                        uint i1 = BitConverter.ToUInt32(raw, off); off += 4;
                        uint i2 = BitConverter.ToUInt32(raw, off); off += 4;
                        indices.Add(i0); indices.Add(i1); indices.Add(i2);
                        for (int extra = 3; extra < count; extra++) off += 4;
                    }
                    mesh.Positions = positions;
                    mesh.Normals = normals ?? ComputeNormals(positions, indices.ToArray());
                    mesh.Indices = indices.ToArray();
                }
                else
                {
                    var dataLines = Encoding.ASCII.GetString(raw).Split('\n')
                        .SkipWhile(l => !l.Trim().Equals("end_header", StringComparison.OrdinalIgnoreCase))
                        .Skip(1).ToArray();
                    for (int v = 0; v < vertexCount && v < dataLines.Length; v++)
                    {
                        var ps = dataLines[v].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (ps.Length > Math.Max(xi, Math.Max(yi, zi)))
                        {
                            positions[v * 3] = P(ps[xi]);
                            positions[v * 3 + 1] = P(ps[yi]);
                            positions[v * 3 + 2] = P(ps[zi]);
                        }
                    }
                    var indices = new List<uint>();
                    for (int f = 0; f < faceCount; f++)
                    {
                        var ps = dataLines[vertexCount + f].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (ps.Length >= 4)
                        { indices.Add(uint.Parse(ps[1])); indices.Add(uint.Parse(ps[2])); indices.Add(uint.Parse(ps[3])); }
                    }
                    mesh.Positions = positions;
                    mesh.Normals = ComputeNormals(positions, indices.ToArray());
                    mesh.Indices = indices.ToArray();
                }

                mesh.FormatDetected = isBinary ? "PLY Binary" : "PLY ASCII";
                mesh.MetaInfo = $"{mesh.FormatDetected} — {vertexCount} verts, {faceCount} faces\n";
                return mesh.VertexCount > 0;
            }
            catch { return false; }
        }

        private static int FindHeaderEnd(byte[] raw)
        {
            byte[] marker = Encoding.ASCII.GetBytes("end_header");
            for (int i = 0; i < raw.Length - marker.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < marker.Length; j++) { if (raw[i + j] != marker[j]) { match = false; break; } }
                if (match) return i + marker.Length;
            }
            return -1;
        }

        private static bool TryParseRawFloats(byte[] raw, ItsfMesh mesh)
        {
            if (raw.Length < 36) return false;
            int floatCount = raw.Length / 4;
            int triFloats = (floatCount / 9) * 9;
            if (triFloats < 9) return false;

            var pos = new float[triFloats];
            for (int i = 0; i < triFloats; i++)
                pos[i] = BitConverter.ToSingle(raw, i * 4);

            int nanCount = pos.Count(float.IsNaN);
            if (nanCount > triFloats / 10) return false;

            var idx = new uint[triFloats / 3];
            for (uint i = 0; i < idx.Length; i++) idx[i] = i;

            mesh.Positions = pos;
            mesh.Normals = ComputeNormals(pos, idx);
            mesh.Indices = idx;
            mesh.FormatDetected = "Raw float triplets";
            mesh.MetaInfo = $"Raw floats — {triFloats / 9} triangles (heuristic)\n";
            return true;
        }

        public static float[] ComputeNormals(float[] pos, uint[] idx)
        {
            int vc = pos.Length / 3;
            var acc = new float[vc * 3];
            var cnt = new int[vc];

            for (int t = 0; t < idx.Length / 3; t++)
            {
                int a = (int)idx[t * 3] * 3, b = (int)idx[t * 3 + 1] * 3, c = (int)idx[t * 3 + 2] * 3;
                float ux = pos[b] - pos[a], uy = pos[b + 1] - pos[a + 1], uz = pos[b + 2] - pos[a + 2];
                float vx = pos[c] - pos[a], vy = pos[c + 1] - pos[a + 1], vz = pos[c + 2] - pos[a + 2];
                float nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                foreach (int vi in new[] { (int)idx[t * 3], (int)idx[t * 3 + 1], (int)idx[t * 3 + 2] })
                { acc[vi * 3] += nx; acc[vi * 3 + 1] += ny; acc[vi * 3 + 2] += nz; cnt[vi]++; }
            }

            var nrm = new float[vc * 3];
            for (int v = 0; v < vc; v++)
            {
                float len = MathF.Sqrt(acc[v * 3] * acc[v * 3] + acc[v * 3 + 1] * acc[v * 3 + 1] + acc[v * 3 + 2] * acc[v * 3 + 2]);
                if (len < 1e-8f) len = 1f;
                nrm[v * 3] = acc[v * 3] / len;
                nrm[v * 3 + 1] = acc[v * 3 + 1] / len;
                nrm[v * 3 + 2] = acc[v * 3 + 2] / len;
            }
            return nrm;
        }

        private static ItsfMesh FallbackSphere(ItsfMesh mesh)
        {
            const int rings = 32, segs = 48;
            var pos = new List<float>(); var nrm = new List<float>(); var idx = new List<uint>();

            for (int r = 0; r <= rings; r++)
            {
                float phi = MathF.PI * r / rings;
                for (int s = 0; s <= segs; s++)
                {
                    float theta = 2f * MathF.PI * s / segs;
                    float x = MathF.Sin(phi) * MathF.Cos(theta);
                    float y = MathF.Cos(phi);
                    float z = MathF.Sin(phi) * MathF.Sin(theta);
                    pos.Add(x); pos.Add(y); pos.Add(z);
                    nrm.Add(x); nrm.Add(y); nrm.Add(z);
                }
            }
            for (int r = 0; r < rings; r++)
                for (int s = 0; s < segs; s++)
                {
                    uint a = (uint)(r * (segs + 1) + s), b = a + 1;
                    uint c = (uint)((r + 1) * (segs + 1) + s), d = c + 1;
                    idx.Add(a); idx.Add(c); idx.Add(b);
                    idx.Add(b); idx.Add(c); idx.Add(d);
                }

            int vc = pos.Count / 3;
            var scalar = new float[vc];
            for (int v = 0; v < vc; v++) scalar[v] = (pos[v * 3 + 1] + 1f) / 2f;
            mesh.ScalarFields["height"] = scalar; mesh.ActiveScalar = "height";

            mesh.Positions = pos.ToArray(); mesh.Normals = nrm.ToArray(); mesh.Indices = idx.ToArray();
            mesh.FormatDetected = "Placeholder sphere";
            return mesh;
        }

        private static float P(string s)
            => float.Parse(s, System.Globalization.NumberStyles.Float,
               System.Globalization.CultureInfo.InvariantCulture);
    }
}