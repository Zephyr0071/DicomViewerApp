#nullable enable

using System;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using HelixToolkit.Wpf;
using System.Windows.Media.Media3D;

namespace DicomViewerApp
{
    public class Viewer3DForm : Form
    {
        public Viewer3DForm(ItsfMesh itsfMesh, string title)
        {
            Text = "3D CFD Viewer - " + title;
            Size = new System.Drawing.Size(1000, 800);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = System.Drawing.Color.FromArgb(15, 15, 20);

            var viewport = new HelixViewport3D
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 15, 20)),
                ShowCoordinateSystem = true,
                ShowViewCube = true,
                ZoomExtentsWhenLoaded = true,
                ShowFrameRate = true,

                CameraMode = CameraMode.Inspect,
                CameraRotationMode = CameraRotationMode.Trackball,

                IsMoveEnabled = true,
                IsPanEnabled = true,
                IsRotationEnabled = true,

                Camera = new PerspectiveCamera
                {
                    NearPlaneDistance = 0.001,
                    FarPlaneDistance = 10000000.0
                }
            };

            viewport.RotateGesture = new System.Windows.Input.MouseGesture(System.Windows.Input.MouseAction.LeftClick);
            viewport.PanGesture = new System.Windows.Input.MouseGesture(System.Windows.Input.MouseAction.RightClick);

            var ambientLightVisual = new ModelVisual3D();
            ambientLightVisual.Content = new AmbientLight(System.Windows.Media.Color.FromRgb(80, 80, 80));
            viewport.Children.Add(new DefaultLights());
            viewport.Children.Add(ambientLightVisual);

            var mesh = new MeshGeometry3D();

            var positions = new Point3DCollection(itsfMesh.VertexCount);
            for (int i = 0; i < itsfMesh.Positions.Length; i += 3)
            {
                positions.Add(new Point3D(itsfMesh.Positions[i], itsfMesh.Positions[i + 1], itsfMesh.Positions[i + 2]));
            }

            // ✨ THE MESH FIX: We removed the brutal "Decimator" loop! 
            // The parser already did the hard work, so we feed the GPU the 100% unbroken, flawless mesh.
            var indices = new System.Windows.Media.Int32Collection(itsfMesh.Indices.Length);
            foreach (var idx in itsfMesh.Indices)
            {
                indices.Add((int)idx);
            }

            if (itsfMesh.Normals != null && itsfMesh.Normals.Length == itsfMesh.Positions.Length)
            {
                var normals = new Vector3DCollection(itsfMesh.VertexCount);
                for (int i = 0; i < itsfMesh.Normals.Length; i += 3)
                {
                    normals.Add(new Vector3D(itsfMesh.Normals[i], itsfMesh.Normals[i + 1], itsfMesh.Normals[i + 2]));
                }
                mesh.Normals = normals;
            }

            // We apply default texture coordinates just to keep WPF happy, but they won't affect our solid color
            var texCoords = new System.Windows.Media.PointCollection(itsfMesh.VertexCount);
            for (int i = 0; i < itsfMesh.VertexCount; i++) texCoords.Add(new System.Windows.Point(0.5, 0.5));

            mesh.Positions = positions;
            mesh.TriangleIndices = indices;
            mesh.TextureCoordinates = texCoords;
            mesh.Freeze();

            // ✨ THE COLOR FIX: A beautiful, smooth Light Grey color
            var lightGreyBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(129, 133, 137));
            lightGreyBrush.Freeze();

            // DiffuseMaterial interacts with the DefaultLights to give the model real 3D volume, shadows, and smooth curves
            var material = new DiffuseMaterial(lightGreyBrush);
            material.Freeze();

            var modelGroup = new Model3DGroup();
            modelGroup.Children.Add(new GeometryModel3D { Geometry = mesh, Material = material, BackMaterial = material });

            var current3DModel = new ModelVisual3D { Content = modelGroup };
            viewport.Children.Add(current3DModel);

            var host = new ElementHost
            {
                Dock = DockStyle.Fill,
                Child = viewport
            };

            Controls.Add(host);
        }
    }
}