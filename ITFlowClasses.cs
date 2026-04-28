#nullable disable
using System;
using ProtoBuf;
using System.Collections.Generic;

namespace DicomViewerApp
{
    [ProtoContract]
    public class SaveModel
    {
        [ProtoMember(1)] public string Cine { get; set; }
        [ProtoMember(2)] public string CineDataset { get; set; }
        [ProtoMember(3)] public byte[] CineMatrix { get; set; }

        [ProtoMember(4)] public string Mag { get; set; }
        [ProtoMember(5)] public string MagDataset { get; set; }
        [ProtoMember(6)] public byte[] MagMatrix { get; set; }

        [ProtoMember(7)] public string Vel { get; set; }
        [ProtoMember(8)] public string VelocityDataset { get; set; }
        [ProtoMember(9)] public byte[] UMatrix { get; set; }
        [ProtoMember(10)] public byte[] VMatrix { get; set; }
        [ProtoMember(11)] public byte[] WMatrix { get; set; }

        [ProtoMember(12)] public List<string> Masks { get; set; } = new List<string>();
        [ProtoMember(13)] public List<string> Objects3D { get; set; } = new List<string>();
        [ProtoMember(14)] public string VeloMaxMin { get; set; }
    }

    public class SaveMaskLegacy
    {
        public string Name { get; set; }
        public string mask { get; set; }
        public string Color { get; set; }
        public int Number { get; set; }
        public bool IsVisible { get; set; }
        public byte[] matrix { get; set; }
    }
}
public class Geometry { public void MakeTable() { } public byte[] matrix; }
    public class Velocity { public void MakeTable() { } public byte[] LocalU, LocalV, LocalW; }
    public class SaveMaskLegacy { public string Name { get; set; } public string mask { get; set; } public string Color { get; set; } public int Number { get; set; } public bool IsVisible { get; set; } public byte[] matrix { get; set; } }
    public class Mask4D { public byte[] matrix; public void MakeTable() { } }
    public class MaskLayer
    {
        public MaskLayer(Mask4D m, string n, string c, int num) { mask = m; }
        public Mask4D mask; public bool IsVisible;
    }
    public class SaveObject3D { public System.Type Type; public string Title; public string FlowRateData; public byte[] Data; public List<float[]> wssPoints, wssVecUps; }
    public class FlowRate { public MatrixDataTemporalDeform Coord; public byte[] Mask2DCine; }
    public class MatrixDataTemporalDeform { public MatrixDataTemporalDeform(MatrixDataTemporalDeform other) { } }
    public class MeasurementWSS { public MeasurementWSS(string t, dynamic p, dynamic v) { } }
    public class PathlineNode { public dynamic PathlineStartPoints; }
    public class SaveVelData { public float Max, Min; }
    public class MatrixData
    {
        public int Columns, Rows, Slices, Phases;
        public float[] ImageOrientationX, ImageOrientationY, ImageOrientationZ, voxelsizeM;
        public void SetTriggerTimeTable(float[] t) { }
    }
    public static class ManagerLocator { public static dynamic Object3DManager = null; }
