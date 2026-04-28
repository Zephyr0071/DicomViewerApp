#nullable disable

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PureHDF;

namespace DicomViewerApp
{
    public class HdfViewer : Form
    {
        private PictureBox pbMain;
        private TextBox txtInfo;
        private Button btnLoadH5, btnPlay, btnRotateData, btnToggleFlow;
        private TrackBar tbFrame, tbSpeed, tbContrast, tbGamma, tbPowerGate;
        private Label lblStatus, lblFrame;
        private System.Windows.Forms.Timer cineTimer;

        private byte[] _tissueRaw;
        private float[] _velRaw;
        private float[] _powerRaw;
        private float[] _ecgData;

        private int _tFrames, _tDepth, _tWidth;
        private int _vFrames, _vDepth, _vWidth;
        private double _tWidthRad, _tOrigoY, _tDepthStart, _tDepthEnd;
        private double _vWidthRad, _vDepthStart, _vDepthEnd;
        private double _vNyquist = 1.0;

        private int _canvasW, _canvasH, _originX, _originY;
        private int[] _tLutRow, _tLutCol, _vLutRow, _vLutCol;
        private bool _lutBuilt = false;
        private Dictionary<int, Bitmap> _tissueCache = new Dictionary<int, Bitmap>();
        private Dictionary<int, Bitmap> _dopplerCache = new Dictionary<int, Bitmap>();

        private int _currentFrame = 0;
        private bool _isPlaying = false, _showFlow = true;
        private int _matrixRotation = 0;
        private float _zoom = 1f;
        private PointF _offset = new PointF(0, 0);
        private Point _mouseStart;
        private bool _isDragging;

        public HdfViewer() { BuildUI(); }

        private void BuildUI()
        {
            Text = "HDF5 Clinical Viewer (Mouse Scroll Enabled)";
            Size = new Size(1300, 950);
            BackColor = Color.FromArgb(10, 12, 16);
            StartPosition = FormStartPosition.CenterScreen;

            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 80f));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20f));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 55f));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 55f));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 38f));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));

            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 28, 42), Padding = new Padding(6, 8, 0, 0) };
            btnLoadH5 = MakeBtn("📂  Load HDF5", Color.FromArgb(0, 120, 212));
            btnRotateData = MakeBtn("🔄  Rotate Data", Color.FromArgb(160, 80, 180));
            btnToggleFlow = MakeBtn("🔴  Doppler On", Color.FromArgb(60, 120, 60));
            btnPlay = MakeBtn("▶  Play", Color.FromArgb(40, 44, 58));

            btnLoadH5.Click += (s, e) => PickAndLoad();
            btnRotateData.Click += (s, e) => {
                _matrixRotation = (_matrixRotation + 1) % 4;
                if (_tissueRaw != null) { BuildLuts(); FitToWindow(); _tissueCache.Clear(); _dopplerCache.Clear(); pbMain?.Invalidate(); }
            };
            btnToggleFlow.Click += (s, e) => { _showFlow = !_showFlow; btnToggleFlow.Text = _showFlow ? "🔴  Doppler On" : "⬜  Doppler Off"; pbMain?.Invalidate(); };
            btnPlay.Click += TogglePlay;

            toolbar.Controls.AddRange(new Control[] { btnLoadH5, btnRotateData, btnToggleFlow, btnPlay });
            tbSpeed = MakeSlider(1, 60, 15);
            tbSpeed.ValueChanged += (s, e) => { if (cineTimer != null) cineTimer.Interval = Math.Max(1, 1000 / tbSpeed.Value); };
            toolbar.Controls.Add(MakeSliderPanel("Speed", tbSpeed));

            var tissueBar = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(22, 28, 40), Padding = new Padding(6, 6, 0, 0) };
            tbContrast = MakeSlider(1, 30, 10);
            tbGamma = MakeSlider(1, 30, 12);
            tbPowerGate = MakeSlider(0, 40, 10);
            EventHandler clearTissue = (s, e) => { _tissueCache.Clear(); pbMain?.Invalidate(); };
            EventHandler clearDoppler = (s, e) => { _dopplerCache.Clear(); pbMain?.Invalidate(); };
            tbContrast.ValueChanged += clearTissue; tbGamma.ValueChanged += clearTissue; tbPowerGate.ValueChanged += clearDoppler;

            tissueBar.Controls.Add(new Label { Text = "✨ TISSUE:", ForeColor = Color.Gold, Font = new Font("Segoe UI", 9f, FontStyle.Bold), AutoSize = true, Margin = new Padding(4, 10, 8, 0) });
            tissueBar.Controls.Add(MakeSliderPanel("Contrast", tbContrast));
            tissueBar.Controls.Add(MakeSliderPanel("Gamma", tbGamma));
            tissueBar.Controls.Add(MakeSliderPanel("Doppler Gate", tbPowerGate));

            tbFrame = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 0, TickStyle = TickStyle.None, BackColor = Color.FromArgb(18, 22, 30) };
            tbFrame.ValueChanged += (s, e) => { _currentFrame = tbFrame.Value; if (lblFrame != null) lblFrame.Text = $"{_currentFrame + 1}/{_tFrames}"; pbMain?.Invalidate(); };
            lblFrame = new Label { Dock = DockStyle.Fill, ForeColor = Color.Cyan, Font = new Font("Segoe UI", 9f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Text = "—" };

            var sliderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            sliderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            sliderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100f));
            sliderRow.Controls.Add(tbFrame, 0, 0); sliderRow.Controls.Add(lblFrame, 1, 0);

            pbMain = new PictureBox { Dock = DockStyle.Fill, BackColor = Color.Black };
            pbMain.Paint += PbMain_Paint;

            // NEW: Ensure PictureBox gets focus so scrolling always works when hovering
            pbMain.MouseEnter += (s, e) => pbMain.Focus();

            // NEW: Mouse Wheel Logic (Scroll Frames vs Ctrl+Zoom)
            pbMain.MouseWheel += (s, e) => {
                if (ModifierKeys.HasFlag(Keys.Control))
                {
                    // Ctrl + Wheel = Zoom
                    _zoom *= (e.Delta > 0) ? 1.1f : 0.9f;
                    _zoom = Math.Max(0.05f, Math.Min(20f, _zoom));
                    pbMain.Invalidate();
                }
                else if (_tFrames > 1)
                {
                    // Just Wheel = Scrub Timeline
                    if (_isPlaying) TogglePlay(null, null); // Auto-pause if scrolling manually
                    int next = _currentFrame - Math.Sign(e.Delta); // Up = Previous Frame, Down = Next Frame
                    tbFrame.Value = Math.Max(0, Math.Min(_tFrames - 1, next));
                }
            };

            pbMain.MouseDown += (s, e) => { _isDragging = true; _mouseStart = e.Location; };
            pbMain.MouseMove += (s, e) => { if (!_isDragging) return; _offset.X += e.X - _mouseStart.X; _offset.Y += e.Y - _mouseStart.Y; _mouseStart = e.Location; pbMain.Invalidate(); };
            pbMain.MouseUp += (s, e) => _isDragging = false;
            pbMain.MouseDoubleClick += (s, e) => FitToWindow();

            txtInfo = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BackColor = Color.FromArgb(12, 14, 20), ForeColor = Color.FromArgb(120, 200, 255), Font = new Font("Consolas", 8.5f), BorderStyle = BorderStyle.None };
            lblStatus = new Label { Dock = DockStyle.Fill, ForeColor = Color.Gray, Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleLeft, Text = " Ready." };

            cineTimer = new System.Windows.Forms.Timer { Interval = 1000 / 15 };
            cineTimer.Tick += (s, e) => { if (_tFrames > 0) { _currentFrame = (_currentFrame + 1) % _tFrames; tbFrame.Value = _currentFrame; } };

            main.Controls.Add(toolbar, 0, 0); main.SetColumnSpan(toolbar, 2);
            main.Controls.Add(tissueBar, 0, 1); main.SetColumnSpan(tissueBar, 2);
            main.Controls.Add(sliderRow, 0, 2); main.SetColumnSpan(sliderRow, 2);
            main.Controls.Add(pbMain, 0, 3); main.Controls.Add(txtInfo, 1, 3);
            main.Controls.Add(lblStatus, 0, 4); main.SetColumnSpan(lblStatus, 2);
            Controls.Add(main);
        }

        private void PickAndLoad()
        {
            using var ofd = new OpenFileDialog { Filter = "HDF5|*.h5" };
            if (ofd.ShowDialog() == DialogResult.OK) LoadHdf5(ofd.FileName);
        }

        private void LoadHdf5(string path)
        {
            try
            {
                using var h5 = H5File.OpenRead(path);
                var allNames = h5.Children().Select(c => c.Name).ToList();

                // 1. Load Tissue
                var tDs = h5.Dataset("Tissue");
                var tDims = tDs.Space.Dimensions;
                _tFrames = (int)tDims[0]; _tDepth = (int)tDims[1]; _tWidth = (int)tDims[2];
                _tissueRaw = tDs.Read<byte[]>();
                _tWidthRad = ReadAttr(tDs, "Width", 1.57); _tDepthEnd = ReadAttr(tDs, "DepthEnd", 0.15);
                _tOrigoY = ReadAttr1(tDs, "Origo", 0.0); _tDepthStart = ReadAttr(tDs, "DepthStart", 0.0);

                // 2. Load Flow
                var vDs = h5.Dataset("FlowVelocity");
                var vDims = vDs.Space.Dimensions;
                _vFrames = (int)vDims[0]; _vDepth = (int)vDims[1]; _vWidth = (int)vDims[2];
                _velRaw = vDs.Read<float[]>();
                _vNyquist = ReadAttr(vDs, "VNyquist", 0.41);
                _vWidthRad = ReadAttr(vDs, "Width", 1.57); _vDepthEnd = ReadAttr(vDs, "DepthEnd", 0.15);
                _vDepthStart = ReadAttr(vDs, "DepthStart", 0.0);

                try { _powerRaw = h5.Dataset("FlowPower").Read<float[]>(); } catch { _powerRaw = null; }

                // 3. SMART ECG LOADER
                _ecgData = null;
                string ecgName = allNames.FirstOrDefault(n => n.Equals("Ecg", StringComparison.OrdinalIgnoreCase));
                if (ecgName != null)
                {
                    var ecgDs = h5.Dataset(ecgName);
                    try { _ecgData = ecgDs.Read<float[]>(); }
                    catch
                    {
                        try { _ecgData = ecgDs.Read<double[]>().Select(v => (float)v).ToArray(); }
                        catch
                        {
                            try { _ecgData = ecgDs.Read<short[]>().Select(v => (float)v).ToArray(); }
                            catch { }
                        }
                    }
                }

                BuildLuts(); FitToWindow();
                tbFrame.Maximum = Math.Max(0, _tFrames - 1);
                lblStatus.Text = $"Loaded: {Path.GetFileName(path)}";

                txtInfo.Text = $"--- FILE INFO ---\r\n" +
                               $"Tissue: {_tDepth}x{_tWidth}\r\n" +
                               $"Doppler: {_vDepth}x{_vWidth}\r\n" +
                               $"VNyq: {_vNyquist:F3}\r\n" +
                               $"ECG Signal: {(_ecgData != null ? "FOUND ✅" : "NOT FOUND ❌")}\r\n\r\n" +
                               $"[Scroll] = Frames\r\n" +
                               $"[Ctrl+Scroll] = Zoom";

            }
            catch (Exception ex) { MessageBox.Show("Loader Error: " + ex.Message); }
        }

        private void BuildLuts()
        {
            int tEffW = (_matrixRotation % 2 != 0) ? _tDepth : _tWidth;
            int tEffH = (_matrixRotation % 2 != 0) ? _tWidth : _tDepth;
            double depthRange = Math.Max(0.01, _tDepthEnd - _tDepthStart);
            double pxPerM = tEffH / depthRange;
            double apexPx = Math.Abs(_tOrigoY) * pxPerM;
            double totalR = tEffH + apexPx;
            double halfA_t = _tWidthRad / 2.0;
            const double SC = 2.0;

            _canvasW = (int)(2.0 * totalR * Math.Sin(halfA_t) * SC) + 40;
            _canvasH = (int)(totalR * SC) + 20;
            _originX = _canvasW / 2; _originY = (int)(apexPx * SC);

            int npx = _canvasW * _canvasH;
            _tLutRow = new int[npx]; _tLutCol = new int[npx];
            _vLutRow = new int[npx]; _vLutCol = new int[npx];
            for (int i = 0; i < npx; i++) { _tLutRow[i] = -1; _vLutRow[i] = -1; }

            int vEffW = (_matrixRotation % 2 != 0) ? _vDepth : _vWidth;
            int vEffH = (_matrixRotation % 2 != 0) ? _vWidth : _vDepth;
            double v_rowStepM = Math.Max(0.01, _vDepthEnd - _vDepthStart) / vEffH;

            for (int py = 0; py < _canvasH; py++)
            {
                double dy = py - _originY; if (dy < 0) continue;
                for (int px = 0; px < _canvasW; px++)
                {
                    double dx = px - _originX; double r = Math.Sqrt(dx * dx + dy * dy), theta = Math.Atan2(dx, dy);
                    int idx = py * _canvasW + px;
                    if (r >= apexPx * SC && r <= totalR * SC && Math.Abs(theta) <= halfA_t)
                    {
                        int rY = (int)(r / SC - apexPx), rX = (int)((theta + halfA_t) / _tWidthRad * tEffW);
                        int oX = rX, oY = rY;
                        if (_matrixRotation == 1) { oX = rY; oY = tEffW - 1 - rX; }
                        else if (_matrixRotation == 2) { oX = tEffW - 1 - rX; oY = tEffH - 1 - rY; }
                        else if (_matrixRotation == 3) { oX = tEffH - 1 - rY; oY = rX; }
                        _tLutRow[idx] = Math.Max(0, Math.Min(_tDepth - 1, oY));
                        _tLutCol[idx] = Math.Max(0, Math.Min(_tWidth - 1, oX));
                    }
                    if (Math.Abs(theta) <= (_vWidthRad / 2.0))
                    {
                        int rY = (int)(((r / SC) / pxPerM + _tDepthStart - _vDepthStart) / v_rowStepM);
                        int rX = (int)((theta + (_vWidthRad / 2.0)) / _vWidthRad * vEffW);
                        if (rY >= 0 && rY < vEffH && rX >= 0 && rX < vEffW)
                        {
                            int oX = rX, oY = rY;
                            if (_matrixRotation == 1) { oX = rY; oY = vEffW - 1 - rX; }
                            else if (_matrixRotation == 2) { oX = vEffW - 1 - rX; oY = vEffH - 1 - rY; }
                            else if (_matrixRotation == 3) { oX = vEffH - 1 - rY; oY = rX; }
                            _vLutRow[idx] = oY; _vLutCol[idx] = oX;
                        }
                    }
                }
            }
            _lutBuilt = true;
        }

        private void PbMain_Paint(object sender, PaintEventArgs e)
        {
            if (!_lutBuilt) return;
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            var state = g.Save();
            g.TranslateTransform(_offset.X, _offset.Y); g.ScaleTransform(_zoom, _zoom);

            var tbmp = GetTissueBitmap(_currentFrame);
            if (tbmp != null) g.DrawImage(tbmp, 0, 0);

            if (_showFlow && _velRaw != null)
            {
                var dbmp = GetDopplerBitmap(_currentFrame);
                if (dbmp != null) g.DrawImage(dbmp, 0, 0);
            }
            g.Restore(state);
            if (_ecgData != null) DrawEcg(g);
        }

        private Bitmap GetTissueBitmap(int frame)
        {
            if (_tissueCache.TryGetValue(frame, out var c)) return c;
            var bmp = new Bitmap(_canvasW, _canvasH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, _canvasW, _canvasH), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            byte[] pix = new byte[_canvasH * data.Stride];
            double contrast = tbContrast.Value / 10.0, gamma = tbGamma.Value / 10.0;
            int fOff = frame * _tDepth * _tWidth;
            for (int i = 0; i < _canvasW * _canvasH; i++)
            {
                int row = _tLutRow[i]; if (row < 0) { pix[i * 4 + 3] = 255; continue; }
                byte val = _tissueRaw[fOff + row * _tWidth + _tLutCol[i]];
                byte v = (byte)Math.Min(255, Math.Pow(val / 255.0, gamma) * contrast * 255);
                pix[i * 4] = v; pix[i * 4 + 1] = v; pix[i * 4 + 2] = v; pix[i * 4 + 3] = 255;
            }
            Marshal.Copy(pix, 0, data.Scan0, pix.Length); bmp.UnlockBits(data);
            return _tissueCache[frame] = bmp;
        }

        private Bitmap GetDopplerBitmap(int frame)
        {
            if (_dopplerCache.TryGetValue(frame, out var c)) return c;
            var bmp = new Bitmap(_canvasW, _canvasH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, _canvasW, _canvasH), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            byte[] pix = new byte[_canvasH * data.Stride];
            int vOff = frame * _vDepth * _vWidth;
            double pGate = 1.5 + tbPowerGate.Value * 0.05;
            for (int i = 0; i < _canvasW * _canvasH; i++)
            {
                int row = _vLutRow[i]; if (row < 0) continue;
                int vi = vOff + row * _vWidth + _vLutCol[i];
                if (vi >= _velRaw.Length) continue;
                if (_powerRaw != null && Math.Log10(Math.Max(1.0, _powerRaw[vi])) < pGate) continue;
                float vel = _velRaw[vi]; if (Math.Abs(vel) < 0.04f) continue;
                float norm = Math.Max(-1f, Math.Min(1f, (float)(vel / _vNyquist)));
                if (norm > 0) { pix[i * 4 + 2] = 255; pix[i * 4 + 1] = (byte)(255 - norm * 200); pix[i * 4] = 0; }
                else { pix[i * 4 + 2] = 0; pix[i * 4 + 1] = (byte)(255 - Math.Abs(norm) * 200); pix[i * 4] = 255; }
                pix[i * 4 + 3] = (byte)(160 + Math.Abs(norm) * 90);
            }
            Marshal.Copy(pix, 0, data.Scan0, pix.Length); bmp.UnlockBits(data);
            return _dopplerCache[frame] = bmp;
        }

        private void DrawEcg(Graphics g)
        {
            if (_ecgData == null || _ecgData.Length < 2) return;
            int h = 100, pad = 30, y = pbMain.Height - h - 40, w = pbMain.Width - pad * 2;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var p = new Pen(Color.FromArgb(40, 0, 255, 0), 1))
            {
                for (int i = 0; i <= 4; i++) g.DrawLine(p, pad, y + i * (h / 4), pad + w, y + i * (h / 4));
            }

            float min = _ecgData.Min(), max = _ecgData.Max();
            float range = Math.Max(0.01f, max - min);
            var pts = _ecgData.Select((v, i) => new PointF(pad + (float)i / (_ecgData.Length - 1) * w, y + h - (v - min) / range * h)).ToArray();

            using (var glow = new Pen(Color.FromArgb(70, 0, 255, 0), 5)) g.DrawLines(glow, pts);
            using (var core = new Pen(Color.LimeGreen, 2f)) g.DrawLines(core, pts);

            float px = pad + (float)_currentFrame / Math.Max(1, _tFrames - 1) * w;
            using (var needleGlow = new Pen(Color.FromArgb(100, 255, 0, 0), 6)) g.DrawLine(needleGlow, px, y - 5, px, y + h + 5);
            using (var needle = new Pen(Color.Red, 1.5f)) g.DrawLine(needle, px, y - 5, px, y + h + 5);
        }

        private void FitToWindow()
        {
            if (_canvasW == 0) return;
            _zoom = Math.Min((float)pbMain.Width / _canvasW, (float)(pbMain.Height - 150) / _canvasH) * 0.9f;
            _offset = new PointF((pbMain.Width - _canvasW * _zoom) / 2, 20);
            pbMain.Invalidate();
        }

        private void TogglePlay(object sender, EventArgs e) { _isPlaying = !_isPlaying; if (_isPlaying) cineTimer.Start(); else cineTimer.Stop(); btnPlay.Text = _isPlaying ? "⏸ Pause" : "▶ Play"; }
        private static double ReadAttr(IH5Dataset ds, string name, double def) { try { var a = ds.Attribute(name); return a.Read<double>(); } catch { try { return ds.Attribute(name).Read<float>(); } catch { return def; } } }
        private static double ReadAttr1(IH5Dataset ds, string name, double def) { try { return ds.Attribute(name).Read<double[]>().ElementAtOrDefault(1); } catch { try { return ds.Attribute(name).Read<float[]>().ElementAtOrDefault(1); } catch { return def; } } }
        private Button MakeBtn(string t, Color c) => new Button { Text = t, BackColor = c, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Height = 36, AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        private TrackBar MakeSlider(int min, int max, int v) => new TrackBar { Minimum = min, Maximum = max, Value = v, Width = 110, TickStyle = TickStyle.None, BackColor = Color.FromArgb(22, 28, 40) };
        private Panel MakeSliderPanel(string l, TrackBar tb) { var p = new FlowLayoutPanel { Width = 125, Height = 50 }; p.Controls.Add(new Label { Text = l, ForeColor = Color.White, AutoSize = true, Font = new Font("Segoe UI", 7.5f) }); p.Controls.Add(tb); return p; }
    }
}