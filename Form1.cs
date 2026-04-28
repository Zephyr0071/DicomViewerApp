#nullable disable

using FellowOakDicom;
using FellowOakDicom.Imaging;
using PureHDF;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using SDColor = System.Drawing.Color;
using SDFont = System.Drawing.Font;
using SDImage = System.Drawing.Image;
using SDSize = System.Drawing.Size;

namespace DicomViewerApp
{
    public class PinnedSlice
    {
        public string SeriesName { get; set; }
        public string FilePath { get; set; }
        public int SliceIndex { get; set; }
        public int FrameIndex { get; set; }
        public bool IsMultiFrame { get; set; }
        public override string ToString()
        {
            string baseName = SeriesName.Split('[')[0].Trim();
            if (IsMultiFrame) return $"📌 {baseName} - Frame {FrameIndex + 1}";
            return $"📌 {baseName} - Slice {SliceIndex + 1}";
        }
    }

    public enum ViewerMode { None, StandardImage, Dicom, Hdf5Polar }
    public enum SeriesChannel { C, M, AP, SI, RL, P }

    public class Form1 : Form
    {
        private TableLayoutPanel imageSplitLayout;
        private PictureBox pictureBox1;
        private PictureBox pictureBox2;
        private bool _isSplitView = false;

        private bool _showMaskLayer = false;
        private bool _showVelocityLayer = false;
        private bool _showVorticityLayer = false;
        private byte[] _rawMaskData = null;

        private TextBox txtData;
        private ListBox lstPinned;
        private Button btnPin, btnSnapshot, btnRemovePin, btnClearPins;
        private Button btnOpenFile, btnOpenFolder, btnClear, btnPrev, btnNext, btnPlay;
        private Button btnRotateData, btnToggleFlow, btnSplitView, btnPdfReport, btnToggleHeatmap;
        private ComboBox cbSeries;

        private Panel _channelBar;
        private Button[] _channelButtons;
        private SeriesChannel _activeChannel = SeriesChannel.M;

        private Label lblStatus, lblSlice, lblFrame;
        private TrackBar sliceSlider, speedSlider, tbFrame;
        private NumericUpDown numSpeed;
        private bool _isUpdatingSpeed = false;

        private Label lblContrast, lblBrightness, lblH5Contrast, lblH5Brightness, lblGamma, lblPowerGate, lblColormap;
        private TrackBar tbContrast, tbBrightness, tbH5Contrast, tbH5Brightness, tbGamma, tbPowerGate;
        private ComboBox cbColormap;

        private TableLayoutPanel mainLayout;
        private Panel navPanel;
        private System.Windows.Forms.Timer cineTimer;

        private ViewerMode _currentMode = ViewerMode.None;
        private Dictionary<string, List<string>> _seriesGroups = new Dictionary<string, List<string>>();
        private List<string> _seriesFiles = new List<string>();
        private int _currentIndex = 0, _currentFrame = 0, _totalFrames = 1;
        private bool _isPlaying = false, _isNewLoad = true;
        private int _jumpToSlice = -1, _jumpToFrame = -1;

        private float _zoom = 1.0f;
        private PointF _offset = new PointF(0, 0);
        private Point _mouseStart;
        private bool _isDragging = false;

        private SDImage _loadedImage;
        private bool _isMultiFrame = false;
        private string _activeMultiFramePath = "";
        private string _baseMetadataText = "";
        private bool _isRenderingFrame = false, _needsAnotherFrameRender = false;
        private bool _isAdjustingWL = false;
        private Point _wlMouseStart;
        private double _currentWC = 0, _currentWW = 0;
        private bool _isRenderingContrast = false, _needsAnotherRender = false, _isUpdatingSliders = false;

        // --- DATA ARRAYS ---
        private byte[] _h5TissueRaw;
        private byte[] _h5CineRaw;
        private float[] _h5VelRaw, _h5PowerRaw, _h5EcgData, _h5VxRaw, _h5VyRaw, _h5VzRaw;

        private bool _hasVfm = false, _lutBuilt = false, _isMMode = false, _showFlow = true, _showHeatmap = false;
        private double[] _temporalVelocities;
        private bool _isTemporalScanDone = false;

        private int _tFrames = 1, _tDepth = 1, _tWidth = 1, _cDepth = 1, _cWidth = 1, _cFrames = 1;
        private int _vFrames = 1, _vDepth = 1, _vWidth = 1;
        private double _tWidthRad, _tOrigoY, _tDepthStart, _tDepthEnd, _vWidthRad, _vDepthStart, _vDepthEnd, _vNyquist = 1.0;
        private int _canvasW = 10, _canvasH = 10, _originX, _originY, _matrixRotation = 0;
        private int[] _tLutRow, _tLutCol, _vLutRow, _vLutCol;

        private string _currentColormap = "Grayscale";
        private byte[] _lutR = new byte[256]; private byte[] _lutG = new byte[256]; private byte[] _lutB = new byte[256];

        private const int MAX_CACHE_FRAMES = 200;
        private readonly object _cacheLock = new object();
        private Dictionary<int, Bitmap> _tissueCache = new Dictionary<int, Bitmap>(); private Queue<int> _tKeys = new Queue<int>();
        private Dictionary<int, Bitmap> _dopplerCache = new Dictionary<int, Bitmap>(); private Queue<int> _vKeys = new Queue<int>();
        private Dictionary<int, Bitmap> _heatmapCache = new Dictionary<int, Bitmap>(); private Queue<int> _hKeys = new Queue<int>();
        private Dictionary<int, Bitmap> _pcCache = new Dictionary<int, Bitmap>(); private Queue<int> _pKeys = new Queue<int>();

        public Form1()
        {
            this.DoubleBuffered = true; BuildUI();
            typeof(Panel).InvokeMember("DoubleBuffered", System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, mainLayout, new object[] { true });
            typeof(Control).InvokeMember("DoubleBuffered", System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, pictureBox1, new object[] { true });
            typeof(Control).InvokeMember("DoubleBuffered", System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, pictureBox2, new object[] { true });
            new DicomSetupBuilder().RegisterServices(s => s.AddFellowOakDicom().AddImageManager<ImageSharpImageManager>()).Build();
            SetSliderVisibility(ViewerMode.None);
        }

        private void BuildChannelBar(TableLayoutPanel imageWrapper)
        {
            var labels = new[] { "C", "M", "AP", "SI", "RL", "P" };
            _channelButtons = new Button[labels.Length];
            _channelBar = new Panel { Dock = DockStyle.Fill, BackColor = SDColor.FromArgb(10, 10, 14), Visible = false, Padding = new Padding(6, 3, 6, 3) };
            var btnLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = labels.Length, RowCount = 1, Margin = new Padding(0) };
            for (int i = 0; i < labels.Length; i++) btnLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / labels.Length));
            btnLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            for (int i = 0; i < labels.Length; i++)
            {
                var ch = (SeriesChannel)i;
                var btn = new Button { Text = labels[i], Font = new SDFont("Segoe UI", 9.5f, FontStyle.Bold), Dock = DockStyle.Fill, BackColor = SDColor.FromArgb(28, 32, 46), ForeColor = SDColor.FromArgb(160, 185, 230), FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Margin = new Padding(3, 2, 3, 2), Tag = ch };
                btn.FlatAppearance.BorderSize = 1; btn.FlatAppearance.BorderColor = SDColor.FromArgb(55, 65, 95);
                btn.Click += (s, e) => SetActiveChannel(ch);
                _channelButtons[i] = btn; btnLayout.Controls.Add(btn, i, 0);
            }
            _channelBar.Controls.Add(btnLayout); imageWrapper.Controls.Add(_channelBar, 0, 0); HighlightChannelButton(SeriesChannel.M);
        }

        private void SetActiveChannel(SeriesChannel ch) { _activeChannel = ch; HighlightChannelButton(ch); ClearCaches(); InvalidateViewers(); SetStatus($"Series: {ch}"); }
        private void HighlightChannelButton(SeriesChannel ch) { if (_channelButtons == null) return; foreach (var b in _channelButtons) { bool active = (SeriesChannel)b.Tag == ch; b.BackColor = active ? SDColor.FromArgb(0, 95, 195) : SDColor.FromArgb(28, 32, 46); b.ForeColor = active ? SDColor.White : SDColor.FromArgb(160, 185, 230); b.FlatAppearance.BorderColor = active ? SDColor.FromArgb(0, 140, 255) : SDColor.FromArgb(55, 65, 95); } }
        private void ShowChannelBar(bool show) { if (_channelBar != null) _channelBar.Visible = show; }
        private void InvalidateViewers() { pictureBox1?.Invalidate(); if (_isSplitView) pictureBox2?.Invalidate(); }

        private void BuildColormapLUT()
        {
            for (int i = 0; i < 256; i++)
            {
                double t = i / 255.0;
                if (_currentColormap == "Sepia") { _lutR[i] = (byte)Math.Min(255, i * 1.15); _lutG[i] = (byte)Math.Min(255, i * 0.85); _lutB[i] = (byte)Math.Min(255, i * 0.45); }
                else if (_currentColormap == "Jet") { _lutR[i] = (byte)(255 * Math.Max(0, Math.Min(1, 1.5 - Math.Abs(4 * t - 3)))); _lutG[i] = (byte)(255 * Math.Max(0, Math.Min(1, 1.5 - Math.Abs(4 * t - 2)))); _lutB[i] = (byte)(255 * Math.Max(0, Math.Min(1, 1.5 - Math.Abs(4 * t - 1)))); }
                else if (_currentColormap == "Ocean") { _lutR[i] = (byte)Math.Min(255, i * 0.4); _lutG[i] = (byte)Math.Min(255, i * 0.8); _lutB[i] = (byte)Math.Min(255, i * 1.2); }
                else { _lutR[i] = (byte)i; _lutG[i] = (byte)i; _lutB[i] = (byte)i; }
            }
        }

        private void BuildUI()
        {
            txtData = new TextBox(); lstPinned = new ListBox(); btnPin = new Button(); btnSnapshot = new Button(); btnRemovePin = new Button(); btnClearPins = new Button(); btnOpenFile = new Button(); btnOpenFolder = new Button(); cbSeries = new ComboBox(); cbSeries.DropDownWidth = 600; btnPrev = new Button(); btnNext = new Button(); btnClear = new Button(); btnRotateData = new Button(); btnToggleFlow = new Button(); btnToggleHeatmap = new Button(); btnSplitView = new Button(); btnPdfReport = new Button();
            lblStatus = new Label(); lblSlice = new Label(); sliceSlider = new TrackBar(); mainLayout = new TableLayoutPanel(); navPanel = new Panel(); btnPlay = new Button(); speedSlider = new TrackBar(); numSpeed = new NumericUpDown(); cineTimer = new System.Windows.Forms.Timer();
            tbContrast = new TrackBar(); tbBrightness = new TrackBar(); tbH5Contrast = new TrackBar(); tbH5Brightness = new TrackBar(); tbGamma = new TrackBar(); tbPowerGate = new TrackBar();

            SuspendLayout(); ((System.ComponentModel.ISupportInitialize)(sliceSlider)).BeginInit(); ((System.ComponentModel.ISupportInitialize)(speedSlider)).BeginInit(); ((System.ComponentModel.ISupportInitialize)(numSpeed)).BeginInit(); ((System.ComponentModel.ISupportInitialize)(tbContrast)).BeginInit(); ((System.ComponentModel.ISupportInitialize)(tbBrightness)).BeginInit(); ((System.ComponentModel.ISupportInitialize)(tbH5Contrast)).BeginInit(); ((System.ComponentModel.ISupportInitialize)(tbH5Brightness)).BeginInit(); ((System.ComponentModel.ISupportInitialize)(tbGamma)).BeginInit(); ((System.ComponentModel.ISupportInitialize)(tbPowerGate)).BeginInit();

            StyleButton(btnOpenFile, "📂 Open File", SDColor.FromArgb(0, 120, 212)); StyleButton(btnOpenFolder, "📁 Open Folder", SDColor.FromArgb(0, 150, 136)); StyleButton(btnPin, "📌 Pin", SDColor.FromArgb(200, 120, 0)); StyleButton(btnSnapshot, "📸 Save", SDColor.FromArgb(0, 150, 100)); StyleButton(btnClear, "❌ Clear", SDColor.FromArgb(140, 50, 50)); StyleButton(btnRotateData, "🔄 Rotate: 0°", SDColor.FromArgb(160, 80, 180)); StyleButton(btnToggleFlow, "🔴 Doppler/VFM", SDColor.FromArgb(60, 120, 60)); StyleButton(btnToggleHeatmap, "🔥 Heatmap Off", SDColor.FromArgb(80, 80, 80)); StyleButton(btnSplitView, "◫ Split View", SDColor.FromArgb(80, 100, 150)); StyleButton(btnPdfReport, "📄 Export PDF", SDColor.FromArgb(200, 50, 50)); StyleButton(btnRemovePin, "➖ Remove Pin", SDColor.FromArgb(100, 70, 70)); StyleButton(btnClearPins, "❌ Clear Pins", SDColor.FromArgb(140, 50, 50));

            btnOpenFile.Click += (s, e) => OpenFile(); btnOpenFolder.Click += (s, e) => OpenFolder(); btnClear.Click += (s, e) => ClearAll(); btnPin.Click += BtnPin_Click; btnSnapshot.Click += BtnSnapshot_Click; btnPdfReport.Click += BtnPdfReport_Click; btnRemovePin.Click += (s, e) => { if (lstPinned.SelectedIndex != -1) { lstPinned.Items.RemoveAt(lstPinned.SelectedIndex); SetStatus("Selected pin removed."); } }; btnClearPins.Click += (s, e) => { lstPinned.Items.Clear(); SetStatus("All pins cleared."); };
            btnToggleHeatmap.Click += (s, e) => { _showHeatmap = !_showHeatmap; btnToggleHeatmap.Text = _showHeatmap ? "🔥 Heatmap On" : "🔥 Heatmap Off"; btnToggleHeatmap.BackColor = _showHeatmap ? SDColor.FromArgb(220, 100, 30) : SDColor.FromArgb(80, 80, 80); InvalidateViewers(); };
            btnSplitView.Click += (s, e) => { _isSplitView = !_isSplitView; if (_isSplitView) { imageSplitLayout.ColumnStyles[0].Width = 50f; imageSplitLayout.ColumnStyles[1].Width = 50f; btnSplitView.BackColor = SDColor.FromArgb(120, 140, 190); } else { imageSplitLayout.ColumnStyles[0].Width = 100f; imageSplitLayout.ColumnStyles[1].Width = 0f; btnSplitView.BackColor = SDColor.FromArgb(80, 100, 150); } FitToWindow(); InvalidateViewers(); };
            btnRotateData.Click += (s, e) => { if (_currentMode == ViewerMode.None) return; _matrixRotation = (_matrixRotation + 1) % 4; btnRotateData.Text = $"🔄 Rotate: {_matrixRotation * 90}°"; if (_currentMode == ViewerMode.Hdf5Polar) { if (_h5TissueRaw != null) { if (_isMMode) { _canvasW = Math.Max(1, (_matrixRotation % 2 != 0) ? _tDepth : _tWidth); _canvasH = Math.Max(1, (_matrixRotation % 2 != 0) ? _tWidth : _tDepth); } else { BuildPolarLuts(); } FitToWindow(); ClearCaches(); InvalidateViewers(); } } else if (_currentMode == ViewerMode.Dicom) { var img = DicomEngine.RenderFrame(_currentFrame); if (img != null) { var old = _loadedImage; _loadedImage = ApplyRotation(img); FitToWindow(); InvalidateViewers(); old?.Dispose(); } } else if (_currentMode == ViewerMode.StandardImage) ProcessStandardImage(_seriesFiles[_currentIndex]); };
            btnToggleFlow.Click += (s, e) => { _showFlow = !_showFlow; btnToggleFlow.Text = _showFlow ? "🔴 Doppler/VFM" : "⬜ Doppler Off"; btnToggleFlow.BackColor = _showFlow ? SDColor.FromArgb(60, 120, 60) : SDColor.FromArgb(80, 80, 80); ClearCaches(); InvalidateViewers(); };

            cbSeries.Dock = DockStyle.Fill; cbSeries.DropDownStyle = ComboBoxStyle.DropDownList; cbSeries.BackColor = SDColor.FromArgb(40, 44, 58); cbSeries.ForeColor = SDColor.White; cbSeries.Font = new SDFont("Segoe UI", 9.5f); cbSeries.Margin = new Padding(4); cbSeries.SelectedIndexChanged += CbSeries_SelectedIndexChanged;
            cineTimer.Interval = 1000 / 15; cineTimer.Tick += (s, e) => { if (_currentMode == ViewerMode.Hdf5Polar) { if (_totalFrames > 0) { _currentFrame = (_currentFrame + 1) % _totalFrames; sliceSlider.Value = _currentFrame; } } else if (_isMultiFrame) { int next = _currentFrame + 1; if (next >= _totalFrames) next = 0; LoadDicomFrame(next); } else { if (_seriesFiles.Count <= 1) { StopPlayback(); return; } int next = _currentIndex + 1; if (next >= _seriesFiles.Count) next = 0; LoadSlice(next); } };

            navPanel.Dock = DockStyle.Fill; navPanel.BackColor = SDColor.FromArgb(22, 26, 36); navPanel.Margin = new Padding(0);
            StyleNavButton(btnPlay, "▶ Play"); StyleNavButton(btnPrev, "◀ Prev"); StyleNavButton(btnNext, "Next ▶"); btnPlay.Enabled = false; btnPrev.Enabled = false; btnNext.Enabled = false; btnPlay.Click += (s, e) => TogglePlayback(); btnPrev.Click += (s, e) => Navigate(-1); btnNext.Click += (s, e) => Navigate(+1);

            sliceSlider.Dock = DockStyle.Fill; sliceSlider.Minimum = 0; sliceSlider.Maximum = 0; sliceSlider.TickFrequency = 1; sliceSlider.BackColor = SDColor.FromArgb(22, 26, 36); sliceSlider.Enabled = false; sliceSlider.ValueChanged += SliceSlider_MasterChanged;
            speedSlider.Dock = DockStyle.Fill; speedSlider.Minimum = 1; speedSlider.Maximum = 120; speedSlider.Value = 15; speedSlider.TickStyle = TickStyle.None; speedSlider.BackColor = SDColor.FromArgb(22, 26, 36);
            numSpeed.Dock = DockStyle.Fill; numSpeed.Minimum = 1; numSpeed.Maximum = 120; numSpeed.Value = 15; numSpeed.BackColor = SDColor.FromArgb(30, 34, 46); numSpeed.ForeColor = SDColor.FromArgb(200, 120, 0); numSpeed.Font = new SDFont("Segoe UI", 9.5f, FontStyle.Bold); numSpeed.BorderStyle = BorderStyle.FixedSingle; numSpeed.TextAlign = HorizontalAlignment.Center; numSpeed.Margin = new Padding(2, 6, 2, 6);
            speedSlider.ValueChanged += (s, e) => { if (_isUpdatingSpeed) return; _isUpdatingSpeed = true; if (speedSlider.Value <= numSpeed.Maximum && speedSlider.Value >= numSpeed.Minimum) numSpeed.Value = speedSlider.Value; cineTimer.Interval = 1000 / speedSlider.Value; _isUpdatingSpeed = false; };
            numSpeed.ValueChanged += (s, e) => { if (_isUpdatingSpeed) return; _isUpdatingSpeed = true; if (numSpeed.Value <= speedSlider.Maximum && numSpeed.Value >= speedSlider.Minimum) speedSlider.Value = (int)numSpeed.Value; cineTimer.Interval = 1000 / (int)numSpeed.Value; _isUpdatingSpeed = false; };

            lblSlice.Text = "—"; lblSlice.Font = new SDFont("Segoe UI", 9f); lblSlice.ForeColor = SDColor.FromArgb(160, 180, 220); lblSlice.BackColor = SDColor.FromArgb(22, 26, 36); lblSlice.TextAlign = System.Drawing.ContentAlignment.MiddleCenter; lblSlice.Dock = DockStyle.Fill;

            var navTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, Margin = new Padding(0), Padding = new Padding(4, 6, 4, 6), BackColor = SDColor.FromArgb(22, 26, 36) };
            navTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f)); navTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f)); navTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); navTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f)); navTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55f)); navTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f)); navTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f)); navTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            navTable.Controls.Add(btnPlay, 0, 0); navTable.Controls.Add(btnPrev, 1, 0); navTable.Controls.Add(sliceSlider, 2, 0); navTable.Controls.Add(btnNext, 3, 0); navTable.Controls.Add(numSpeed, 4, 0); navTable.Controls.Add(speedSlider, 5, 0); navTable.Controls.Add(lblSlice, 6, 0); navPanel.Controls.Add(navTable);

            imageSplitLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            imageSplitLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f)); imageSplitLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 0f));
            pictureBox1 = new PictureBox { Dock = DockStyle.Fill, BackColor = SDColor.FromArgb(8, 8, 8), Margin = new Padding(0), AllowDrop = true }; pictureBox2 = new PictureBox { Dock = DockStyle.Fill, BackColor = SDColor.FromArgb(8, 8, 8), Margin = new Padding(0) };
            pictureBox1.Paint += PictureBox1_Paint; pictureBox2.Paint += PictureBox2_Paint;
            var unifyMouseWheel = new MouseEventHandler(PictureBox_MouseWheel); var unifyMouseDown = new MouseEventHandler(PictureBox_MouseDown); var unifyMouseMove = new MouseEventHandler(PictureBox_MouseMove); var unifyMouseUp = new MouseEventHandler(PictureBox_MouseUp);
            pictureBox1.MouseWheel += unifyMouseWheel; pictureBox2.MouseWheel += unifyMouseWheel; pictureBox1.MouseDown += unifyMouseDown; pictureBox2.MouseDown += unifyMouseDown; pictureBox1.MouseMove += unifyMouseMove; pictureBox2.MouseMove += unifyMouseMove; pictureBox1.MouseUp += unifyMouseUp; pictureBox2.MouseUp += unifyMouseUp;
            pictureBox1.MouseEnter += (s, e) => pictureBox1.Focus(); pictureBox2.MouseEnter += (s, e) => pictureBox2.Focus(); pictureBox1.MouseDoubleClick += (s, e) => FitToWindow(); pictureBox2.MouseDoubleClick += (s, e) => FitToWindow();
            pictureBox1.DragEnter += (s, e) => { if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; }; pictureBox1.DragDrop += (s, e) => { var files = e.Data?.GetData(DataFormats.FileDrop) as string[]; if (files == null || files.Length == 0) return; if (Directory.Exists(files[0])) LoadFolder(files[0]); else LoadSingleFile(files[0]); };
            imageSplitLayout.Controls.Add(pictureBox1, 0, 0); imageSplitLayout.Controls.Add(pictureBox2, 1, 0);

            var imageAreaWrapper = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
            imageAreaWrapper.RowStyles.Add(new RowStyle(SizeType.Absolute, 45f)); imageAreaWrapper.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); imageAreaWrapper.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            BuildChannelBar(imageAreaWrapper); imageAreaWrapper.Controls.Add(imageSplitLayout, 0, 1);

            tbContrast.Minimum = 1; tbContrast.Maximum = 8000; tbContrast.TickStyle = TickStyle.None; tbContrast.Dock = DockStyle.Fill; tbBrightness.Minimum = -2000; tbBrightness.Maximum = 6000; tbBrightness.TickStyle = TickStyle.None; tbBrightness.Dock = DockStyle.Fill;
            tbContrast.ValueChanged += TbContrast_ValueChanged; tbBrightness.ValueChanged += TbBrightness_ValueChanged; lblContrast = MakeLabel("DICOM WW"); lblBrightness = MakeLabel("DICOM WC");
            tbH5Contrast.Minimum = 1; tbH5Contrast.Maximum = 50; tbH5Contrast.Value = 10; tbH5Contrast.TickStyle = TickStyle.None; tbH5Contrast.Dock = DockStyle.Fill; tbH5Brightness.Minimum = -100; tbH5Brightness.Maximum = 100; tbH5Brightness.Value = 0; tbH5Brightness.TickStyle = TickStyle.None; tbH5Brightness.Dock = DockStyle.Fill; tbGamma.Minimum = 1; tbGamma.Maximum = 30; tbGamma.Value = 12; tbGamma.TickStyle = TickStyle.None; tbGamma.Dock = DockStyle.Fill; tbPowerGate.Minimum = 0; tbPowerGate.Maximum = 40; tbPowerGate.Value = 10; tbPowerGate.TickStyle = TickStyle.None; tbPowerGate.Dock = DockStyle.Fill;
            lblH5Contrast = MakeLabel($"Contrast: {tbH5Contrast.Value / 10.0:F1}x"); lblH5Brightness = MakeLabel($"Brightness: {tbH5Brightness.Value}"); lblGamma = MakeLabel($"Gamma: {tbGamma.Value / 10.0:F1}"); lblPowerGate = MakeLabel($"VFM/Dop Gate: {tbPowerGate.Value}");
            tbH5Contrast.ValueChanged += (s, e) => { lblH5Contrast.Text = $"Contrast: {tbH5Contrast.Value / 10.0:F1}x"; if (_currentMode == ViewerMode.Hdf5Polar) { ClearCaches(); InvalidateViewers(); } }; tbH5Brightness.ValueChanged += (s, e) => { lblH5Brightness.Text = $"Brightness: {tbH5Brightness.Value}"; if (_currentMode == ViewerMode.Hdf5Polar) { ClearCaches(); InvalidateViewers(); } }; tbGamma.ValueChanged += (s, e) => { lblGamma.Text = $"Gamma: {tbGamma.Value / 10.0:F1}"; if (_currentMode == ViewerMode.Hdf5Polar) { ClearCaches(); InvalidateViewers(); } }; tbPowerGate.ValueChanged += (s, e) => { lblPowerGate.Text = $"VFM/Dop Gate: {tbPowerGate.Value}"; if (_currentMode == ViewerMode.Hdf5Polar) { ClearCaches(); InvalidateViewers(); } };
            lblColormap = MakeLabel("Tissue LUT:"); cbColormap = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = SDColor.FromArgb(40, 44, 58), ForeColor = SDColor.White, Font = new SDFont("Segoe UI", 9f) }; cbColormap.Items.AddRange(new string[] { "Grayscale", "Sepia (Gold)", "Jet (Thermal)", "Ocean (Blue)" }); cbColormap.SelectedIndex = 0; cbColormap.SelectedIndexChanged += (s, e) => { _currentColormap = cbColormap.SelectedItem.ToString().Split(' ')[0]; BuildColormapLUT(); if (_currentMode == ViewerMode.Hdf5Polar) { ClearCaches(); InvalidateViewers(); } };
            BuildColormapLUT();

            var contrastTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Margin = new Padding(0), Padding = new Padding(5), BackColor = SDColor.FromArgb(24, 28, 38) };
            contrastTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120f)); contrastTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            for (int i = 0; i < 5; i++) contrastTable.RowStyles.Add(new RowStyle(SizeType.Percent, 20f));
            contrastTable.Controls.Add(lblContrast, 0, 0); contrastTable.Controls.Add(tbContrast, 1, 0); contrastTable.Controls.Add(lblH5Contrast, 0, 0); contrastTable.Controls.Add(tbH5Contrast, 1, 0); contrastTable.Controls.Add(lblBrightness, 0, 1); contrastTable.Controls.Add(tbBrightness, 1, 1); contrastTable.Controls.Add(lblH5Brightness, 0, 1); contrastTable.Controls.Add(tbH5Brightness, 1, 1); contrastTable.Controls.Add(lblGamma, 0, 2); contrastTable.Controls.Add(tbGamma, 1, 2); contrastTable.Controls.Add(lblPowerGate, 0, 3); contrastTable.Controls.Add(tbPowerGate, 1, 3); contrastTable.Controls.Add(lblColormap, 0, 4); contrastTable.Controls.Add(cbColormap, 1, 4);

            txtData.Dock = DockStyle.Fill; txtData.Multiline = true; txtData.ReadOnly = true; txtData.ScrollBars = ScrollBars.Vertical; txtData.Font = new SDFont("Consolas", 9.5f); txtData.BackColor = SDColor.FromArgb(20, 24, 35); txtData.ForeColor = SDColor.FromArgb(180, 210, 255); txtData.BorderStyle = BorderStyle.None; txtData.Margin = new Padding(0);
            lstPinned.Dock = DockStyle.Fill; lstPinned.BackColor = SDColor.FromArgb(26, 30, 42); lstPinned.ForeColor = SDColor.FromArgb(255, 200, 100); lstPinned.Font = new SDFont("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold); lstPinned.BorderStyle = BorderStyle.None; lstPinned.DoubleClick += LstPinned_DoubleClick; lstPinned.KeyDown += (s, e) => { if ((e.KeyCode == Keys.Delete || e.KeyCode == Keys.Back) && lstPinned.SelectedIndex != -1) { lstPinned.Items.RemoveAt(lstPinned.SelectedIndex); SetStatus("Selected pin removed."); } };
            var pinButtonsTable = new TableLayoutPanel { Dock = DockStyle.Top, Height = 36, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) }; pinButtonsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f)); pinButtonsTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f)); pinButtonsTable.Controls.Add(btnRemovePin, 0, 0); pinButtonsTable.Controls.Add(btnClearPins, 1, 0);
            var pinContainer = new Panel { Dock = DockStyle.Fill }; pinContainer.Controls.Add(lstPinned); pinContainer.Controls.Add(pinButtonsTable);

            var rightPanelSplit = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(10, 10, 10, 0) };
            rightPanelSplit.RowStyles.Add(new RowStyle(SizeType.Percent, 50f)); rightPanelSplit.RowStyles.Add(new RowStyle(SizeType.Percent, 50f)); rightPanelSplit.RowStyles.Add(new RowStyle(SizeType.Absolute, 220f));
            rightPanelSplit.Controls.Add(CreateGroupPanel("FILE METADATA", txtData), 0, 0); rightPanelSplit.Controls.Add(CreateGroupPanel("PINNED FRAMES", pinContainer), 0, 1); rightPanelSplit.Controls.Add(CreateGroupPanel("IMAGE CONTROLS", contrastTable), 0, 2);

            lblStatus.Dock = DockStyle.Fill; lblStatus.Font = new SDFont("Segoe UI", 8.5f); lblStatus.ForeColor = SDColor.FromArgb(130, 150, 180); lblStatus.BackColor = SDColor.FromArgb(15, 18, 26); lblStatus.Text = "Ready"; lblStatus.TextAlign = System.Drawing.ContentAlignment.MiddleLeft; lblStatus.Padding = new Padding(8, 0, 0, 0); lblStatus.Margin = new Padding(0);

            mainLayout.Dock = DockStyle.Fill; mainLayout.Margin = new Padding(0); mainLayout.Padding = new Padding(0); mainLayout.BackColor = SDColor.FromArgb(18, 22, 30); mainLayout.ColumnCount = 2; mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72f)); mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28f)); mainLayout.RowCount = 4; mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f)); mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f)); mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));

            var toolbarTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 11, RowCount = 1, Margin = new Padding(0), Padding = new Padding(4), BackColor = SDColor.FromArgb(22, 26, 36) };
            toolbarTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 7f)); toolbarTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 7f)); toolbarTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 26f)); toolbarTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 5f)); toolbarTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 6f)); toolbarTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 9f)); toolbarTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 10f)); toolbarTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 8f)); toolbarTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 8f)); toolbarTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 8f)); toolbarTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 6f)); toolbarTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            toolbarTable.Controls.Add(btnOpenFile, 0, 0); toolbarTable.Controls.Add(btnOpenFolder, 1, 0); toolbarTable.Controls.Add(cbSeries, 2, 0); toolbarTable.Controls.Add(btnPin, 3, 0); toolbarTable.Controls.Add(btnSnapshot, 4, 0); toolbarTable.Controls.Add(btnRotateData, 5, 0); toolbarTable.Controls.Add(btnToggleFlow, 6, 0); toolbarTable.Controls.Add(btnToggleHeatmap, 7, 0); toolbarTable.Controls.Add(btnSplitView, 8, 0); toolbarTable.Controls.Add(btnPdfReport, 9, 0); toolbarTable.Controls.Add(btnClear, 10, 0);

            mainLayout.Controls.Add(toolbarTable, 0, 0); mainLayout.SetColumnSpan(toolbarTable, 2); mainLayout.Controls.Add(imageAreaWrapper, 0, 1); mainLayout.Controls.Add(rightPanelSplit, 1, 1); mainLayout.Controls.Add(navPanel, 0, 2); mainLayout.SetColumnSpan(navPanel, 2); mainLayout.Controls.Add(lblStatus, 0, 3); mainLayout.SetColumnSpan(lblStatus, 2);

            Text = "(DICOM + HDF5 + iTFlow2)"; ClientSize = new SDSize(1400, 900); MinimumSize = new SDSize(800, 600); BackColor = SDColor.FromArgb(18, 22, 30); StartPosition = FormStartPosition.CenterScreen; Controls.Add(mainLayout); tbFrame = new TrackBar { Minimum = 0, Maximum = 0, Value = 0 }; lblFrame = new Label { Text = "—" };

            ((System.ComponentModel.ISupportInitialize)(sliceSlider)).EndInit(); ((System.ComponentModel.ISupportInitialize)(speedSlider)).EndInit(); ((System.ComponentModel.ISupportInitialize)(numSpeed)).EndInit(); ((System.ComponentModel.ISupportInitialize)(tbContrast)).EndInit(); ((System.ComponentModel.ISupportInitialize)(tbBrightness)).EndInit(); ((System.ComponentModel.ISupportInitialize)(tbH5Contrast)).EndInit(); ((System.ComponentModel.ISupportInitialize)(tbH5Brightness)).EndInit(); ((System.ComponentModel.ISupportInitialize)(tbGamma)).EndInit(); ((System.ComponentModel.ISupportInitialize)(tbPowerGate)).EndInit(); ((System.ComponentModel.ISupportInitialize)(tbFrame)).EndInit();
            ResumeLayout(false);
        }

        private void BtnPdfReport_Click(object sender, EventArgs e) { if (_seriesFiles.Count == 0 || string.IsNullOrEmpty(txtData.Text)) return; using (SaveFileDialog sfd = new SaveFileDialog()) { sfd.Filter = "PDF Files|*.pdf"; sfd.FileName = $"Clinical_Report_{DateTime.Now:yyyyMMdd}.pdf"; sfd.Title = "Select where to save the Hemodynamic Report"; if (sfd.ShowDialog() == DialogResult.OK) { SetStatus("Generating PDF Report..."); Cursor = Cursors.WaitCursor; try { Bitmap screenCapture = new Bitmap(imageSplitLayout.Width, imageSplitLayout.Height); imageSplitLayout.DrawToBitmap(screenCapture, new Rectangle(0, 0, imageSplitLayout.Width, imageSplitLayout.Height)); string originalFile = Path.GetFileName(_seriesFiles[_currentIndex]); ReportGenerator.GeneratePdfReport(sfd.FileName, originalFile, txtData.Text, screenCapture); SetStatus($"Saved to: {Path.GetFileName(sfd.FileName)}"); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = sfd.FileName, UseShellExecute = true }); } catch (Exception ex) { MessageBox.Show($"Failed to save: {ex.Message}", "Error"); } finally { Cursor = Cursors.Default; } } } }
        private Panel CreateGroupPanel(string title, Control content) { var pnl = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10), BackColor = SDColor.FromArgb(24, 28, 38) }; var lbl = new Label { Text = title, ForeColor = SDColor.FromArgb(130, 150, 180), Font = new SDFont("Segoe UI", 8.5f, FontStyle.Bold), Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(8, 0, 0, 4) }; var contentPadder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) }; content.Dock = DockStyle.Fill; contentPadder.Controls.Add(content); pnl.Controls.Add(contentPadder); pnl.Controls.Add(lbl); return pnl; }
        private void ClearCaches() { lock (_cacheLock) { foreach (var bmp in _tissueCache.Values) bmp?.Dispose(); _tissueCache.Clear(); _tKeys.Clear(); foreach (var bmp in _dopplerCache.Values) bmp?.Dispose(); _dopplerCache.Clear(); _vKeys.Clear(); foreach (var bmp in _heatmapCache.Values) bmp?.Dispose(); _heatmapCache.Clear(); _hKeys.Clear(); foreach (var bmp in _pcCache.Values) bmp?.Dispose(); _pcCache.Clear(); _pKeys.Clear(); } }
        private void SetSliderVisibility(ViewerMode mode) { bool showDicom = (mode == ViewerMode.Dicom); bool showH5 = (mode == ViewerMode.Hdf5Polar); lblContrast.Visible = showDicom; tbContrast.Visible = showDicom; lblBrightness.Visible = showDicom; tbBrightness.Visible = showDicom; lblH5Contrast.Visible = showH5; tbH5Contrast.Visible = showH5; lblH5Brightness.Visible = showH5; tbH5Brightness.Visible = showH5; lblGamma.Visible = showH5; tbGamma.Visible = showH5; lblPowerGate.Visible = showH5; tbPowerGate.Visible = showH5; btnRotateData.Visible = (mode != ViewerMode.None); btnToggleFlow.Visible = showH5; btnPdfReport.Visible = (mode != ViewerMode.None); btnSplitView.Visible = showH5; btnToggleHeatmap.Visible = showH5; lblColormap.Visible = showH5; cbColormap.Visible = showH5; }
        private SDImage ApplyRotation(SDImage img) { if (img == null || _matrixRotation == 0) return img; if (_matrixRotation == 1) img.RotateFlip(RotateFlipType.Rotate90FlipNone); if (_matrixRotation == 2) img.RotateFlip(RotateFlipType.Rotate180FlipNone); if (_matrixRotation == 3) img.RotateFlip(RotateFlipType.Rotate270FlipNone); return img; }
        private void OpenFile() { StopPlayback(); using var d = new OpenFileDialog(); d.Filter = "Medical Files|*.dcm;*.ima;*.png;*.jpg;*.h5;*.vfm;*.itflow2|All Files|*.*"; if (d.ShowDialog() == DialogResult.OK) LoadSingleFile(d.FileName); }
        private void OpenFolder() { StopPlayback(); using var d = new FolderBrowserDialog(); if (d.ShowDialog() == DialogResult.OK) LoadFolder(d.SelectedPath); }
        private void LoadSingleFile(string filePath) { _seriesGroups.Clear(); cbSeries.Items.Clear(); string ext = Path.GetExtension(filePath).ToLower(); string name = (ext == ".h5" || ext == ".vfm" || ext == ".itflow2") ? "Fluid Dynamics Data" : "Image/DICOM"; string cName = $"{name} (1 file)"; _seriesGroups[cName] = new List<string> { filePath }; cbSeries.Items.Add(cName); cbSeries.SelectedIndex = 0; }
        private void CbSeries_SelectedIndexChanged(object sender, EventArgs e) { StopPlayback(); if (cbSeries.SelectedItem == null) return; string sel = cbSeries.SelectedItem.ToString(); if (_seriesGroups.ContainsKey(sel)) { _seriesFiles = _seriesGroups[sel]; _currentIndex = (_jumpToSlice != -1 && _jumpToSlice < _seriesFiles.Count) ? _jumpToSlice : 0; _jumpToSlice = -1; _isNewLoad = true; UpdateSlider(); RouteFile(_seriesFiles[_currentIndex]); } }
        private byte[] DecompressMatrix(byte[] input) { if (input == null || input.Length < 4) return input; if (input[0] == 0x1F && input[1] == 0x8B) { try { using var ms = new MemoryStream(input); using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress); using var outMs = new MemoryStream(); gz.CopyTo(outMs); return outMs.ToArray(); } catch { } } if (input[0] == 0x78) { try { using var ms = new MemoryStream(input); ms.Seek(2, SeekOrigin.Begin); using var def = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress); using var outMs = new MemoryStream(); def.CopyTo(outMs); return outMs.ToArray(); } catch { } } try { using var ms = new MemoryStream(input); using var def = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress); using var outMs = new MemoryStream(); def.CopyTo(outMs); return outMs.ToArray(); } catch { } return input; }

        private void RouteFile(string filePath)
        {
            try
            {
                string ext = Path.GetExtension(filePath).ToLower();
                if (ext == ".itflow2")
                {
                    SetStatus("Extracting Binary Protobuf Stream..."); Cursor = Cursors.WaitCursor;
                    using (var file = File.OpenRead(filePath))
                    {
                        var savefile = ProtoBuf.Serializer.Deserialize<SaveModel>(file);
                        byte[] sourceMatrix = savefile.MagMatrix ?? savefile.CineMatrix;
                        string sourceJson = savefile.Mag ?? savefile.Cine;

                        if (!string.IsNullOrEmpty(sourceJson) && sourceMatrix != null)
                        {
                            var jObj = Newtonsoft.Json.Linq.JObject.Parse(sourceJson);
                            _tWidth = (int?)jObj.GetValue("Columns", StringComparison.OrdinalIgnoreCase) ?? 256;
                            _tDepth = (int?)jObj.GetValue("Rows", StringComparison.OrdinalIgnoreCase) ?? 256;
                            int slices = (int?)jObj.GetValue("Slices", StringComparison.OrdinalIgnoreCase) ?? 1;
                            int phases = (int?)jObj.GetValue("Phases", StringComparison.OrdinalIgnoreCase) ?? 1;

                            byte[] rawBytes = DecompressMatrix(sourceMatrix);
                            _tFrames = Math.Max(1, phases * slices); int pixelCount = _tWidth * _tDepth * _tFrames;

                            _baseMetadataText = $"--- iTFlow2 DATA LOADED ---\r\nTarget: {(savefile.MagMatrix != null ? "Magnitude" : "Cine")}\r\nDimensions: {_tWidth}x{_tDepth}\r\nTotal Frames: {_tFrames}\r\nAnatomy Memory: {rawBytes.Length / 1024 / 1024.0:F1} MB";
                            txtData.Text = _baseMetadataText;

                            _h5TissueRaw = new byte[pixelCount]; short[] tempShorts = new short[pixelCount]; short minVal = short.MaxValue, maxVal = short.MinValue;
                            for (int i = 0; i < pixelCount; i++) { if (i * 2 + 1 < rawBytes.Length) { short val = BitConverter.ToInt16(rawBytes, i * 2); tempShorts[i] = val; if (val > -30000 && val < minVal) minVal = val; if (val < 30000 && val > maxVal) maxVal = val; } }
                            float range = (maxVal - minVal) <= 0 ? 1 : (maxVal - minVal);
                            for (int i = 0; i < pixelCount; i++) _h5TissueRaw[i] = (byte)Math.Max(0, Math.Min(255, ((tempShorts[i] - minVal) / range) * 255f));

                            // --- CINE LOAD ---
                            if (savefile.CineMatrix != null && !string.IsNullOrEmpty(savefile.Cine))
                            {
                                var cObj = Newtonsoft.Json.Linq.JObject.Parse(savefile.Cine);
                                _cWidth = (int?)cObj.GetValue("Columns", StringComparison.OrdinalIgnoreCase) ?? 256;
                                _cDepth = (int?)cObj.GetValue("Rows", StringComparison.OrdinalIgnoreCase) ?? 256;
                                int cSlices = (int?)cObj.GetValue("Slices", StringComparison.OrdinalIgnoreCase) ?? 1;
                                int cPhases = (int?)cObj.GetValue("Phases", StringComparison.OrdinalIgnoreCase) ?? 1;
                                _cFrames = Math.Max(1, cPhases * cSlices);
                                byte[] cineBytes = DecompressMatrix(savefile.CineMatrix);
                                int cPixelCount = _cWidth * _cDepth * _cFrames;
                                _h5CineRaw = new byte[cPixelCount];
                                short[] cShorts = new short[cPixelCount];
                                short cMin = short.MaxValue, cMax = short.MinValue;
                                for (int i = 0; i < cPixelCount; i++)
                                {
                                    if (i * 2 + 1 < cineBytes.Length)
                                    {
                                        short val = BitConverter.ToInt16(cineBytes, i * 2);
                                        cShorts[i] = val;
                                        if (val > -30000 && val < cMin) cMin = val;
                                        if (val < 30000 && val > cMax) cMax = val;
                                    }
                                }
                                float cRange = (cMax - cMin) <= 0 ? 1 : (cMax - cMin);
                                for (int i = 0; i < cPixelCount; i++) _h5CineRaw[i] = (byte)Math.Max(0, Math.Min(255, ((cShorts[i] - cMin) / cRange) * 255f));
                            }
                            else { _h5CineRaw = null; }

                            _h5VxRaw = null; _h5VyRaw = null; _h5VzRaw = null; _h5VelRaw = null;
                            if (savefile.UMatrix != null && savefile.VMatrix != null && savefile.WMatrix != null)
                            {
                                byte[] uBytes = DecompressMatrix(savefile.UMatrix); byte[] vBytes = DecompressMatrix(savefile.VMatrix); byte[] wBytes = DecompressMatrix(savefile.WMatrix);
                                _h5VxRaw = new float[pixelCount]; _h5VyRaw = new float[pixelCount]; _h5VzRaw = new float[pixelCount]; _h5VelRaw = new float[pixelCount];
                                for (int i = 0; i < pixelCount; i++)
                                {
                                    int bi = i * 4;
                                    if (bi + 3 < uBytes.Length && bi + 3 < vBytes.Length && bi + 3 < wBytes.Length)
                                    {
                                        float u = BitConverter.ToSingle(uBytes, bi); float v = BitConverter.ToSingle(vBytes, bi); float w = BitConverter.ToSingle(wBytes, bi);
                                        _h5VxRaw[i] = u; _h5VyRaw[i] = v; _h5VzRaw[i] = w; _h5VelRaw[i] = (float)Math.Sqrt(u * u + v * v + w * w);
                                    }
                                }
                                _hasVfm = true; _vDepth = _tDepth; _vWidth = _tWidth; _vFrames = _tFrames; _vNyquist = 1.5f;
                            }
                            else if (savefile.UMatrix != null && savefile.VMatrix != null)
                            {
                                byte[] uBytes = DecompressMatrix(savefile.UMatrix); byte[] vBytes = DecompressMatrix(savefile.VMatrix);
                                _h5VxRaw = new float[pixelCount]; _h5VyRaw = new float[pixelCount]; _h5VelRaw = new float[pixelCount];
                                for (int i = 0; i < pixelCount; i++)
                                {
                                    int bi = i * 4;
                                    if (bi + 3 < uBytes.Length && bi + 3 < vBytes.Length) { float u = BitConverter.ToSingle(uBytes, bi); float v = BitConverter.ToSingle(vBytes, bi); _h5VxRaw[i] = u; _h5VyRaw[i] = v; _h5VelRaw[i] = (float)Math.Sqrt(u * u + v * v); }
                                }
                                _hasVfm = true; _vDepth = _tDepth; _vWidth = _tWidth; _vFrames = _tFrames; _vNyquist = 1.5f;
                            }

                            _rawMaskData = null;
                            if (savefile.Masks != null && savefile.Masks.Count > 0)
                            { try { var lm = Newtonsoft.Json.JsonConvert.DeserializeObject<SaveMaskLegacy>(savefile.Masks[0]); if (lm?.matrix != null) _rawMaskData = DecompressMatrix(lm.matrix); } catch { } }

                            _currentMode = ViewerMode.Hdf5Polar; _isMMode = true; _totalFrames = _tFrames; _currentFrame = 0; _canvasW = Math.Max(1, _tWidth); _canvasH = Math.Max(1, _tDepth); _lutBuilt = true; SetSliderVisibility(ViewerMode.Hdf5Polar);
                            ShowChannelBar(_hasVfm);
                            if (_channelButtons != null) { _channelButtons[(int)SeriesChannel.SI].Enabled = _h5VzRaw != null; _channelButtons[(int)SeriesChannel.SI].ForeColor = _h5VzRaw != null ? SDColor.FromArgb(160, 185, 230) : SDColor.FromArgb(70, 70, 90); }
                            SetActiveChannel(SeriesChannel.M);
                            ClearCaches(); sliceSlider.Enabled = _totalFrames > 1; sliceSlider.Maximum = Math.Max(0, _totalFrames - 1); sliceSlider.Value = 0; btnPlay.Enabled = _totalFrames > 1; _loadedImage?.Dispose(); _loadedImage = null; FitToWindow(); pictureBox1.Invalidate(); if (_hasVfm) UpdateRealTimeMetrics();
                        }
                        SetStatus("Extraction Complete.");
                    }
                    Cursor = Cursors.Default;
                }
                else if (ext == ".h5" || ext == ".vfm") ProcessHdf5Polar(filePath); else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp") ProcessStandardImage(filePath); else ProcessDicomEngine(filePath);
            }
            catch (Exception ex) { Cursor = Cursors.Default; MessageBox.Show($"Load Error: {ex.Message}", "Crash Report", MessageBoxButtons.OK, MessageBoxIcon.Error); txtData.Text = $"ERROR:\r\n{ex.Message}"; }
        }

        private void SliceSlider_MasterChanged(object sender, EventArgs e) { if (sliceSlider == null || !sliceSlider.Enabled) return; if (_currentMode == ViewerMode.Hdf5Polar) { _currentFrame = sliceSlider.Value; InvalidateViewers(); UpdateRealTimeMetrics(); } else if (_isMultiFrame) { _currentFrame = sliceSlider.Value; LoadDicomFrame(_currentFrame); } else { if (sliceSlider.Value != _currentIndex) LoadSlice(sliceSlider.Value); } }
        private void Navigate(int delta) { StopPlayback(); if (_currentMode == ViewerMode.Hdf5Polar || _isMultiFrame) { int next = _currentFrame + delta; if (next >= 0 && next < _totalFrames) { _currentFrame = next; sliceSlider.Value = next; } } else { int next = _currentIndex + delta; if (next >= 0 && next < _seriesFiles.Count) LoadSlice(next); } }
        private void LoadSlice(int index) { _currentIndex = index; if (!_isMultiFrame && _currentMode != ViewerMode.Hdf5Polar) sliceSlider.Value = index; RouteFile(_seriesFiles[index]); }
        private void UpdateSlider() { if (_currentMode == ViewerMode.Hdf5Polar || _isMultiFrame) return; bool multi = _seriesFiles.Count > 1; sliceSlider.Enabled = multi; btnPrev.Enabled = multi; btnNext.Enabled = multi; btnPlay.Enabled = multi; if (multi) { sliceSlider.Minimum = 0; sliceSlider.Maximum = _seriesFiles.Count - 1; sliceSlider.Value = _currentIndex; } }
        private void UpdateFrameLabel() { if (lblSlice != null) lblSlice.Text = $"{_currentFrame + 1} / {_totalFrames}"; }

        private void PictureBox1_Paint(object sender, PaintEventArgs e)
        {
            try
            {
                var g = e.Graphics; g.InterpolationMode = InterpolationMode.NearestNeighbor;
                var state = g.Save(); g.TranslateTransform(_offset.X, _offset.Y); g.ScaleTransform(_zoom, _zoom);
                if (_currentMode == ViewerMode.Hdf5Polar && _lutBuilt)
                {
                    Bitmap baseBmp = null;
                    switch (_activeChannel)
                    {
                        case SeriesChannel.C: baseBmp = GetCineBitmap(_currentFrame); break;
                        case SeriesChannel.M: baseBmp = GetTissueBitmap(_currentFrame); break;
                        case SeriesChannel.AP: baseBmp = GetPhaseContrastBitmap(_h5VyRaw, _currentFrame, false, SeriesChannel.AP); break;
                        case SeriesChannel.SI: baseBmp = GetPhaseContrastBitmap(_h5VzRaw, _currentFrame, false, SeriesChannel.SI); break;
                        case SeriesChannel.RL: baseBmp = GetPhaseContrastBitmap(_h5VxRaw, _currentFrame, false, SeriesChannel.RL); break;
                        case SeriesChannel.P: baseBmp = GetPhaseContrastBitmap(_h5VelRaw, _currentFrame, true, SeriesChannel.P); break;
                    }
                    if (baseBmp != null) g.DrawImage(baseBmp, 0, 0);

                    if (_showFlow && !_isSplitView && _activeChannel == SeriesChannel.M) { var dbmp = GetDopplerBitmap(_currentFrame); if (dbmp != null) g.DrawImage(dbmp, 0, 0); }
                    if (_showHeatmap && _activeChannel == SeriesChannel.M) { var hbmp = GetHeatmapBitmap(_currentFrame); if (hbmp != null) g.DrawImage(hbmp, 0, 0); }
                    g.Restore(state); DrawCharts(g);
                }
                else if (_loadedImage != null) { g.DrawImage(_loadedImage, new PointF(0, 0)); g.Restore(state); }
            }
            catch { }
        }

        private void PictureBox2_Paint(object sender, PaintEventArgs e)
        {
            try
            {
                if (!_isSplitView) return;
                var g = e.Graphics; g.InterpolationMode = InterpolationMode.NearestNeighbor;
                var state = g.Save(); g.TranslateTransform(_offset.X, _offset.Y); g.ScaleTransform(_zoom, _zoom);
                if (_currentMode == ViewerMode.Hdf5Polar && _lutBuilt)
                {
                    var tbmp = GetTissueBitmap(_currentFrame); if (tbmp != null) g.DrawImage(tbmp, 0, 0);
                    if (_showFlow) { var dbmp = GetDopplerBitmap(_currentFrame); if (dbmp != null) g.DrawImage(dbmp, 0, 0); }
                    if (_showHeatmap) { var hbmp = GetHeatmapBitmap(_currentFrame); if (hbmp != null) g.DrawImage(hbmp, 0, 0); }
                    g.Restore(state);
                }
                else if (_loadedImage != null) { g.DrawImage(_loadedImage, new PointF(0, 0)); g.Restore(state); }
            }
            catch { }
        }

        private void PictureBox_MouseWheel(object sender, MouseEventArgs e) { if (_currentMode == ViewerMode.None) return; bool isMultiFrameContent = (_totalFrames > 1 || _seriesFiles.Count > 1); if (ModifierKeys.HasFlag(Keys.Control) || !isMultiFrameContent) { float zoomChange = (e.Delta > 0) ? 1.15f : 0.85f; float newZoom = Math.Max(0.05f, Math.Min(50f, _zoom * zoomChange)); _offset.X = e.X - (e.X - _offset.X) * (newZoom / _zoom); _offset.Y = e.Y - (e.Y - _offset.Y) * (newZoom / _zoom); _zoom = newZoom; InvalidateViewers(); } else if (isMultiFrameContent) { if (_isPlaying) TogglePlayback(); Navigate(Math.Sign(e.Delta)); } }
        private void PictureBox_MouseDown(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && (_loadedImage != null || _lutBuilt)) { _isDragging = true; _mouseStart = e.Location; if (sender is PictureBox pb) pb.Cursor = Cursors.SizeAll; } else if (e.Button == MouseButtons.Right && _currentMode == ViewerMode.Dicom) { _isAdjustingWL = true; _wlMouseStart = e.Location; if (sender is PictureBox pb) pb.Cursor = Cursors.SizeNS; } }
        private void PictureBox_MouseMove(object sender, MouseEventArgs e) { if (_isDragging) { _offset.X += e.X - _mouseStart.X; _offset.Y += e.Y - _mouseStart.Y; _mouseStart = e.Location; InvalidateViewers(); } else if (_isAdjustingWL && _currentMode == ViewerMode.Dicom) { _currentWW += (e.X - _wlMouseStart.X) * 3.0; _currentWC -= (e.Y - _wlMouseStart.Y) * 3.0; if (_currentWW < 1) _currentWW = 1; _wlMouseStart = e.Location; SyncSlidersToMouse(); RequestContrastUpdate(); } }
        private void PictureBox_MouseUp(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _isDragging = false; if (sender is PictureBox pb) pb.Cursor = Cursors.Default; } if (e.Button == MouseButtons.Right) { _isAdjustingWL = false; if (sender is PictureBox pb) pb.Cursor = Cursors.Default; } }

        private void FitToWindow()
        {
            if (_currentMode == ViewerMode.Hdf5Polar && _canvasW > 0)
            {
                float pWidth = pictureBox1.Width; float avH = pictureBox1.Height - (_h5EcgData != null ? 110 : 0);
                if (_isMMode) { float scaleX = pWidth / _canvasW; float scaleY = avH / _canvasH; _zoom = Math.Min(scaleX, scaleY) * 0.95f; _offset.X = (pWidth - _canvasW * _zoom) / 2f; _offset.Y = (avH - _canvasH * _zoom) / 2f; }
                else
                {
                    int tEffH = (_matrixRotation % 2 != 0) ? _tWidth : _tDepth; double pxPerM = tEffH / Math.Max(0.01, _tDepthEnd - _tDepthStart);
                    float left = (float)(_originX - (tEffH + Math.Abs(_tOrigoY) * pxPerM) * 2.0 * Math.Sin(_tWidthRad / 2)); float right = (float)(_originX + (tEffH + Math.Abs(_tOrigoY) * pxPerM) * 2.0 * Math.Sin(_tWidthRad / 2));
                    float top = _originY; float bottom = (float)(_originY + (tEffH + Math.Abs(_tOrigoY) * pxPerM) * 2.0);
                    float sW = right - left, sH = bottom - top; if (sW > 0 && sH > 0) { _zoom = Math.Min(pWidth / sW, avH / sH) * 0.93f; _offset.X = (pWidth - sW * _zoom) / 2f - left * _zoom; _offset.Y = (avH - sH * _zoom) / 2f - top * _zoom; }
                }
            }
            else if (_loadedImage != null) { float scaleX = (float)pictureBox1.Width / _loadedImage.Width; float scaleY = (float)pictureBox1.Height / _loadedImage.Height; _zoom = Math.Min(scaleX, scaleY); _offset.X = (pictureBox1.Width - _loadedImage.Width * _zoom) / 2f; _offset.Y = (pictureBox1.Height - _loadedImage.Height * _zoom) / 2f; }
            InvalidateViewers();
        }

        private Bitmap GetCineBitmap(int frame)
        {
            if (_h5CineRaw == null) return GetTissueBitmap(frame);
            frame = Math.Max(0, Math.Min(_cFrames - 1, frame));
            double contrast = tbH5Contrast.Value / 10.0; double brightness = tbH5Brightness.Value; double gamma = tbGamma.Value / 10.0; int fOff = frame * _cDepth * _cWidth;

            var rawBmp = new Bitmap(_cWidth, _cDepth, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var rawData = rawBmp.LockBits(new Rectangle(0, 0, _cWidth, _cDepth), System.Drawing.Imaging.ImageLockMode.WriteOnly, rawBmp.PixelFormat);
            byte[] rawPix = new byte[_cDepth * rawData.Stride];

            for (int y = 0; y < _cDepth; y++)
            {
                for (int x = 0; x < _cWidth; x++)
                {
                    int gi = fOff + y * _cWidth + x;
                    double rawNorm = Math.Pow(_h5CineRaw[gi] / 255.0, gamma);
                    byte v = (byte)Math.Max(0, Math.Min(255, rawNorm * 255.0 * contrast + brightness));
                    int idx = y * rawData.Stride + x * 4;
                    rawPix[idx] = _lutB[v]; rawPix[idx + 1] = _lutG[v]; rawPix[idx + 2] = _lutR[v]; rawPix[idx + 3] = 255;
                }
            }
            Marshal.Copy(rawPix, 0, rawData.Scan0, rawPix.Length); rawBmp.UnlockBits(rawData);
            if (_matrixRotation == 1) rawBmp.RotateFlip(RotateFlipType.Rotate90FlipNone); else if (_matrixRotation == 2) rawBmp.RotateFlip(RotateFlipType.Rotate180FlipNone); else if (_matrixRotation == 3) rawBmp.RotateFlip(RotateFlipType.Rotate270FlipNone);
            return rawBmp;
        }

        private Bitmap GetPhaseContrastBitmap(float[] velocityMatrix, int frame, bool isAbsolute, SeriesChannel channel)
        {
            if (velocityMatrix == null || _tWidth <= 0 || _tDepth <= 0) return null;
            frame = Math.Max(0, Math.Min(_tFrames - 1, frame));
            int cacheKey = (int)channel * 10000 + frame;
            lock (_cacheLock) { if (_pcCache.TryGetValue(cacheKey, out var cached)) return cached; }

            int fOff = frame * _tDepth * _tWidth; float maxVel = (float)(_vNyquist <= 0 ? 1.5 : _vNyquist);
            var bmp = new Bitmap(_tWidth, _tDepth, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, _tWidth, _tDepth), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
            byte[] rawPix = new byte[_tDepth * data.Stride];

            for (int y = 0; y < _tDepth; y++)
            {
                for (int x = 0; x < _tWidth; x++)
                {
                    int gi = fOff + y * _tWidth + x;
                    float vel = gi < velocityMatrix.Length ? velocityMatrix[gi] : 0f;
                    if (float.IsNaN(vel) || float.IsInfinity(vel)) vel = 0f;

                    byte px;
                    if (isAbsolute) { float norm = Math.Min(1f, vel / maxVel); px = (byte)(norm * 255); }
                    else { float norm = Math.Max(-1f, Math.Min(1f, vel / maxVel)); px = (byte)(128 + norm * 127f); }

                    int idx = y * data.Stride + x * 4;
                    rawPix[idx] = px; rawPix[idx + 1] = px; rawPix[idx + 2] = px; rawPix[idx + 3] = 255;
                }
            }
            Marshal.Copy(rawPix, 0, data.Scan0, rawPix.Length); bmp.UnlockBits(data);
            if (_matrixRotation == 1) bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); else if (_matrixRotation == 2) bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); else if (_matrixRotation == 3) bmp.RotateFlip(RotateFlipType.Rotate270FlipNone);
            lock (_cacheLock) { if (_pcCache.Count >= MAX_CACHE_FRAMES * 6) { int old = _pKeys.Dequeue(); if (_pcCache.TryGetValue(old, out var oldBmp)) { oldBmp.Dispose(); _pcCache.Remove(old); } } _pKeys.Enqueue(cacheKey); return _pcCache[cacheKey] = bmp; }
        }

        private Bitmap GetTissueBitmap(int frame)
        {
            frame = Math.Max(0, Math.Min(_tFrames - 1, frame)); lock (_cacheLock) { if (_tissueCache.TryGetValue(frame, out var c)) return c; }
            double contrast = tbH5Contrast.Value / 10.0; double brightness = tbH5Brightness.Value; double gamma = tbGamma.Value / 10.0; int fOff = frame * _tDepth * _tWidth; Bitmap bmpOut = null;
            if (_isMMode)
            {
                var rawBmp = new Bitmap(_tWidth, _tDepth, System.Drawing.Imaging.PixelFormat.Format32bppArgb); var rawData = rawBmp.LockBits(new Rectangle(0, 0, _tWidth, _tDepth), System.Drawing.Imaging.ImageLockMode.WriteOnly, rawBmp.PixelFormat); byte[] rawPix = new byte[_tDepth * rawData.Stride];
                for (int y = 0; y < _tDepth; y++)
                {
                    for (int x = 0; x < _tWidth; x++)
                    {
                        int gi = fOff + y * _tWidth + x; double rawNorm = Math.Pow(_h5TissueRaw[gi] / 255.0, gamma); byte v = (byte)Math.Max(0, Math.Min(255, rawNorm * 255.0 * contrast + brightness)); int idx = y * rawData.Stride + x * 4;
                        rawPix[idx] = _lutB[v]; rawPix[idx + 1] = _lutG[v]; rawPix[idx + 2] = _lutR[v]; rawPix[idx + 3] = 255;
                    }
                }
                Marshal.Copy(rawPix, 0, rawData.Scan0, rawPix.Length); rawBmp.UnlockBits(rawData);
                if (_matrixRotation == 1) rawBmp.RotateFlip(RotateFlipType.Rotate90FlipNone); else if (_matrixRotation == 2) rawBmp.RotateFlip(RotateFlipType.Rotate180FlipNone); else if (_matrixRotation == 3) rawBmp.RotateFlip(RotateFlipType.Rotate270FlipNone); bmpOut = rawBmp;
            }
            else
            {
                var bmp = new Bitmap(_canvasW, _canvasH, System.Drawing.Imaging.PixelFormat.Format32bppArgb); var data = bmp.LockBits(new Rectangle(0, 0, _canvasW, _canvasH), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat); byte[] pix = new byte[_canvasH * data.Stride];
                for (int i = 0; i < _canvasW * _canvasH; i++) { int row = _tLutRow[i]; if (row < 0) { pix[i * 4 + 3] = 255; continue; } double rawNorm = Math.Pow(_h5TissueRaw[fOff + row * _tWidth + _tLutCol[i]] / 255.0, gamma); byte v = (byte)Math.Max(0, Math.Min(255, rawNorm * 255.0 * contrast + brightness)); pix[i * 4] = _lutB[v]; pix[i * 4 + 1] = _lutG[v]; pix[i * 4 + 2] = _lutR[v]; pix[i * 4 + 3] = 255; }
                Marshal.Copy(pix, 0, data.Scan0, pix.Length); bmp.UnlockBits(data); bmpOut = bmp;
            }
            lock (_cacheLock) { if (_tissueCache.Count >= MAX_CACHE_FRAMES) { int old = _tKeys.Dequeue(); if (_tissueCache.TryGetValue(old, out var ob)) { ob.Dispose(); _tissueCache.Remove(old); } } _tKeys.Enqueue(frame); return _tissueCache[frame] = bmpOut; }
        }

        private void DrawVfmMModeUnrotated(Graphics g, int frame)
        {
            if (_h5VxRaw == null || _h5VyRaw == null) return;
            int vOff = frame * _vDepth * _vWidth; int step = 15; double pGate = 1.5 + tbPowerGate.Value * 0.05;
            using var arrowPen = new Pen(Color.Yellow, 1.2f) { CustomEndCap = new AdjustableArrowCap(3, 3) };

            for (int y = 0; y < _tDepth; y += step)
            {
                int vY = y * _vDepth / _tDepth; if (vY >= _vDepth) continue;
                for (int x = 0; x < _tWidth; x += step)
                {
                    int vX = x * _vWidth / _tWidth; if (vX >= _vWidth) continue;
                    int vi = vOff + vY * _vWidth + vX; if (vi >= _h5VxRaw.Length) continue;
                    if (_h5PowerRaw != null && Math.Log10(Math.Max(1.0, _h5PowerRaw[vi])) < pGate) continue;

                    float vx = _h5VxRaw[vi]; float vy = _h5VyRaw[vi];
                    if (float.IsNaN(vx) || float.IsInfinity(vx) || float.IsNaN(vy) || float.IsInfinity(vy)) continue;

                    double mag = Math.Sqrt(vx * vx + vy * vy);
                    if (mag > 0.05)
                    {
                        float angle = (float)Math.Atan2(vy, vx); float len = (float)(mag * 25);
                        g.DrawLine(arrowPen, x, y, x + (float)(Math.Cos(angle) * len), y + (float)(Math.Sin(angle) * len));
                    }
                }
            }
        }

        private void DrawVfmPolarRotated(Graphics g, int frame)
        {
            if (_h5VxRaw == null || _h5VyRaw == null) return;
            int vOff = frame * _vDepth * _vWidth; int step = 15; double pGate = 1.5 + tbPowerGate.Value * 0.05;
            using var arrowPen = new Pen(Color.Yellow, 1.2f) { CustomEndCap = new AdjustableArrowCap(3, 3) };
            float angleOffset = _matrixRotation * (float)(Math.PI / 2.0);

            for (int y = 0; y < _canvasH; y += step)
            {
                for (int x = 0; x < _canvasW; x += step)
                {
                    int idx = y * _canvasW + x; if (idx >= _vLutRow.Length) continue;
                    int row = _vLutRow[idx]; if (row < 0) continue;
                    int vi = vOff + row * _vWidth + _vLutCol[idx]; if (vi >= _h5VxRaw.Length) continue;
                    if (_h5PowerRaw != null && Math.Log10(Math.Max(1.0, _h5PowerRaw[vi])) < pGate) continue;

                    float vTheta = _h5VxRaw[vi]; float vRadial = _h5VyRaw[vi];
                    if (float.IsNaN(vTheta) || float.IsInfinity(vTheta) || float.IsNaN(vRadial) || float.IsInfinity(vRadial)) continue;

                    double mag = Math.Sqrt(vTheta * vTheta + vRadial * vRadial);

                    if (mag > 0.05)
                    {
                        float beamAngle = (float)Math.Atan2(y - _originY, x - _originX);
                        float localAngle = (float)Math.Atan2(vTheta, -vRadial);
                        float finalAngle = beamAngle + localAngle + angleOffset;
                        float len = (float)(mag * 25);
                        g.DrawLine(arrowPen, x, y, x + (float)(Math.Cos(finalAngle) * len), y + (float)(Math.Sin(finalAngle) * len));
                    }
                }
            }
        }

        private Bitmap GetDopplerBitmap(int frame)
        {
            frame = Math.Max(0, Math.Min(_vFrames - 1, frame));
            lock (_cacheLock) { if (_dopplerCache.TryGetValue(frame, out var c)) return c; }

            int vOff = frame * _vDepth * _vWidth; double pGate = 1.5 + tbPowerGate.Value * 0.05;
            Bitmap bmpOut = null;

            if (_isMMode)
            {
                var rawBmp = new Bitmap(_tWidth, _tDepth, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                if (_h5VelRaw != null)
                {
                    var rawData = rawBmp.LockBits(new Rectangle(0, 0, _tWidth, _tDepth), System.Drawing.Imaging.ImageLockMode.WriteOnly, rawBmp.PixelFormat);
                    byte[] rawPix = new byte[_tDepth * rawData.Stride];
                    for (int y = 0; y < _tDepth; y++)
                    {
                        int vY = y * _vDepth / _tDepth; if (vY >= _vDepth) continue;
                        for (int x = 0; x < _tWidth; x++)
                        {
                            int vX = x * _vWidth / _tWidth; if (vX >= _vWidth) continue;
                            int vi = vOff + vY * _vWidth + vX; if (vi >= _h5VelRaw.Length || (_h5PowerRaw != null && Math.Log10(Math.Max(1.0, _h5PowerRaw[vi])) < pGate)) continue;
                            float vel = _h5VelRaw[vi];
                            if (float.IsNaN(vel) || float.IsInfinity(vel) || Math.Abs(vel) < 0.04f) continue;
                            float norm = Math.Max(-1f, Math.Min(1f, (float)(vel / (_vNyquist <= 0.01 ? 0.41 : _vNyquist)))); float mag = Math.Abs(norm);
                            int idx = y * rawData.Stride + x * 4;
                            if (norm > 0) { rawPix[idx + 2] = 255; rawPix[idx + 1] = (byte)Math.Max(0, 255 - norm * 220); rawPix[idx] = 0; } else { rawPix[idx + 2] = 0; rawPix[idx + 1] = (byte)Math.Max(0, 255 - mag * 220); rawPix[idx] = 255; }
                            rawPix[idx + 3] = (byte)Math.Min(220, 140 + mag * 80);
                        }
                    }
                    Marshal.Copy(rawPix, 0, rawData.Scan0, rawPix.Length); rawBmp.UnlockBits(rawData);
                }

                if (_hasVfm) { using (var g = Graphics.FromImage(rawBmp)) { g.SmoothingMode = SmoothingMode.AntiAlias; DrawVfmMModeUnrotated(g, frame); } }
                if (_matrixRotation == 1) rawBmp.RotateFlip(RotateFlipType.Rotate90FlipNone); else if (_matrixRotation == 2) rawBmp.RotateFlip(RotateFlipType.Rotate180FlipNone); else if (_matrixRotation == 3) rawBmp.RotateFlip(RotateFlipType.Rotate270FlipNone);
                bmpOut = rawBmp;
            }
            else
            {
                var bmp = new Bitmap(_canvasW, _canvasH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                if (_h5VelRaw != null)
                {
                    var data = bmp.LockBits(new Rectangle(0, 0, _canvasW, _canvasH), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                    byte[] pix = new byte[_canvasH * data.Stride];
                    for (int i = 0; i < _canvasW * _canvasH; i++)
                    {
                        int row = _vLutRow[i]; if (row < 0) continue; int vi = vOff + row * _vWidth + _vLutCol[i];
                        if (vi >= _h5VelRaw.Length || (_h5PowerRaw != null && Math.Log10(Math.Max(1.0, _h5PowerRaw[vi])) < pGate)) continue;
                        float vel = _h5VelRaw[vi];
                        if (float.IsNaN(vel) || float.IsInfinity(vel) || Math.Abs(vel) < 0.04f) continue;
                        float norm = Math.Max(-1f, Math.Min(1f, (float)(vel / (_vNyquist <= 0.01 ? 0.41 : _vNyquist)))); float mag = Math.Abs(norm);
                        if (norm > 0) { pix[i * 4 + 2] = 255; pix[i * 4 + 1] = (byte)Math.Max(0, 255 - norm * 220); pix[i * 4] = 0; } else { pix[i * 4 + 2] = 0; pix[i * 4 + 1] = (byte)Math.Max(0, 255 - mag * 220); pix[i * 4] = 255; }
                        pix[i * 4 + 3] = (byte)Math.Min(220, 140 + mag * 80);
                    }
                    Marshal.Copy(pix, 0, data.Scan0, pix.Length); bmp.UnlockBits(data);
                }

                if (_hasVfm) { using (var g = Graphics.FromImage(bmp)) { g.SmoothingMode = SmoothingMode.AntiAlias; DrawVfmPolarRotated(g, frame); } }
                bmpOut = bmp;
            }

            lock (_cacheLock) { if (_dopplerCache.Count >= MAX_CACHE_FRAMES) { int old = _vKeys.Dequeue(); if (_dopplerCache.TryGetValue(old, out var ob)) { ob.Dispose(); _dopplerCache.Remove(old); } } _vKeys.Enqueue(frame); return _dopplerCache[frame] = bmpOut; }
        }

        private Bitmap GetHeatmapBitmap(int frame)
        {
            if (!_hasVfm) return null;
            frame = Math.Max(0, Math.Min(_vFrames - 1, frame));
            lock (_cacheLock) { if (_heatmapCache.TryGetValue(frame, out var c)) return c; }

            double pGate = 1.5 + tbPowerGate.Value * 0.05;
            var metrics = HemodynamicCalculator.AnalyzeFrame(_h5VxRaw, _h5VyRaw, _vDepth, _vWidth, frame, _h5PowerRaw, pGate);
            if (metrics.SpatialEnergyMap == null) return null;

            double maxEnergy = 0;
            for (int i = 0; i < metrics.SpatialEnergyMap.Length; i++) { if (metrics.SpatialEnergyMap[i] > maxEnergy) maxEnergy = metrics.SpatialEnergyMap[i]; }
            if (maxEnergy == 0) maxEnergy = 0.001;

            Bitmap bmpOut = null;

            if (_isMMode)
            {
                var rawBmp = new Bitmap(_tWidth, _tDepth, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var rawData = rawBmp.LockBits(new Rectangle(0, 0, _tWidth, _tDepth), System.Drawing.Imaging.ImageLockMode.WriteOnly, rawBmp.PixelFormat);
                byte[] rawPix = new byte[_tDepth * rawData.Stride];
                for (int y = 0; y < _tDepth; y++)
                {
                    int vY = y * _vDepth / _tDepth; if (vY >= _vDepth) continue;
                    for (int x = 0; x < _tWidth; x++)
                    {
                        int vX = x * _vWidth / _tWidth; if (vX >= _vWidth) continue;
                        int localIndex = (vY * _vWidth) + vX;
                        double val = metrics.SpatialEnergyMap[localIndex] / maxEnergy;
                        if (val > 0.05)
                        {
                            int idx = y * rawData.Stride + x * 4;
                            rawPix[idx] = (byte)Math.Max(0, 255 - val * 510);
                            rawPix[idx + 1] = (byte)Math.Max(0, 255 - Math.Abs(val - 0.5) * 510);
                            rawPix[idx + 2] = (byte)Math.Min(255, val * 510);
                            rawPix[idx + 3] = (byte)Math.Min(180, val * 255);
                        }
                    }
                }
                Marshal.Copy(rawPix, 0, rawData.Scan0, rawPix.Length); rawBmp.UnlockBits(rawData);
                if (_matrixRotation == 1) rawBmp.RotateFlip(RotateFlipType.Rotate90FlipNone); else if (_matrixRotation == 2) rawBmp.RotateFlip(RotateFlipType.Rotate180FlipNone); else if (_matrixRotation == 3) rawBmp.RotateFlip(RotateFlipType.Rotate270FlipNone);
                bmpOut = rawBmp;
            }
            else
            {
                var bmp = new Bitmap(_canvasW, _canvasH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, _canvasW, _canvasH), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                byte[] pix = new byte[_canvasH * data.Stride];
                for (int i = 0; i < _canvasW * _canvasH; i++)
                {
                    int row = _vLutRow[i]; if (row < 0) continue;
                    int localIndex = (row * _vWidth) + _vLutCol[i];
                    double val = metrics.SpatialEnergyMap[localIndex] / maxEnergy;
                    if (val > 0.05)
                    {
                        pix[i * 4] = (byte)Math.Max(0, 255 - val * 510);
                        pix[i * 4 + 1] = (byte)Math.Max(0, 255 - Math.Abs(val - 0.5) * 510);
                        pix[i * 4 + 2] = (byte)Math.Min(255, val * 510);
                        pix[i * 4 + 3] = (byte)Math.Min(180, val * 255);
                    }
                }
                Marshal.Copy(pix, 0, data.Scan0, pix.Length); bmp.UnlockBits(data);
                bmpOut = bmp;
            }

            lock (_cacheLock) { if (_heatmapCache.Count >= MAX_CACHE_FRAMES) { int old = _hKeys.Dequeue(); if (_heatmapCache.TryGetValue(old, out var ob)) { ob.Dispose(); _heatmapCache.Remove(old); } } _hKeys.Enqueue(frame); return _heatmapCache[frame] = bmpOut; }
        }

        private void DrawCharts(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = 30, w = pictureBox1.Width - pad * 2; float px = pad + (float)_currentFrame / Math.Max(1, _totalFrames - 1) * w;

            if (_isTemporalScanDone && _temporalVelocities != null && _temporalVelocities.Length > 1)
            {
                int vH = 80, vY = pictureBox1.Height - 200;
                using (var p = new Pen(SDColor.FromArgb(40, 0, 200, 255), 1)) for (int i = 0; i <= 4; i++) g.DrawLine(p, pad, vY + i * (vH / 4), pad + w, vY + i * (vH / 4));
                float maxV = (float)_temporalVelocities.Max(); float rangeV = Math.Max(0.01f, maxV);
                var vPts = _temporalVelocities.Select((v, i) => new PointF(pad + (float)i / (_temporalVelocities.Length - 1) * w, vY + vH - (float)v / rangeV * vH)).ToArray();
                using (var glow = new Pen(SDColor.FromArgb(70, 0, 200, 255), 4)) g.DrawLines(glow, vPts);
                using (var core = new Pen(SDColor.Cyan, 1.5f)) g.DrawLines(core, vPts);
                using (var pen = new Pen(SDColor.White, 1.5f)) g.DrawLine(pen, px, vY - 5, px, vY + vH + 5);
                g.DrawString($"Peak Vel: {maxV:F2} m/s", new SDFont("Segoe UI", 8f), Brushes.Cyan, pad, vY - 15);
            }

            if (_h5EcgData != null && _h5EcgData.Length > 1)
            {
                int eH = 80, eY = pictureBox1.Height - eH - 20;
                using (var p = new Pen(SDColor.FromArgb(40, 0, 255, 0), 1)) for (int i = 0; i <= 4; i++) g.DrawLine(p, pad, eY + i * (eH / 4), pad + w, eY + i * (eH / 4));
                float minE = _h5EcgData.Min(), maxE = _h5EcgData.Max(), rangeE = Math.Max(0.01f, maxE - minE);
                var ePts = _h5EcgData.Select((v, i) => new PointF(pad + (float)i / (_h5EcgData.Length - 1) * w, eY + eH - (v - minE) / rangeE * eH)).ToArray();
                using (var glow = new Pen(SDColor.FromArgb(70, 0, 255, 0), 4)) g.DrawLines(glow, ePts);
                using (var core = new Pen(SDColor.LimeGreen, 1.5f)) g.DrawLines(core, ePts);
                using (var glow = new Pen(SDColor.FromArgb(100, 255, 0, 0), 4)) g.DrawLine(glow, px, eY - 5, px, eY + eH + 5);
                using (var pen = new Pen(SDColor.Red, 1.5f)) g.DrawLine(pen, px, eY - 5, px, eY + eH + 5);
                g.DrawString("ECG Signal", new SDFont("Segoe UI", 8f), Brushes.LimeGreen, pad, eY - 15);
            }
        }

        private async void ProcessDicomEngine(string filePath)
        {
            try
            {
                _currentMode = ViewerMode.Dicom; SetSliderVisibility(ViewerMode.Dicom); ShowChannelBar(false);
                if (_isNewLoad) { _matrixRotation = 0; btnRotateData.Text = "🔄 Rotate: 0°"; }
                Cursor = Cursors.WaitCursor; pictureBox1.BackColor = SDColor.FromArgb(15, 15, 15);
                int cIndex = _currentIndex; int sCount = _seriesFiles.Count;
                var result = await Task.Run(() => DicomEngine.Process(filePath, cIndex + 1, sCount));
                Cursor = Cursors.Default; pictureBox1.BackColor = SDColor.FromArgb(8, 8, 8);
                _currentWC = result.DefaultWC; _currentWW = result.DefaultWW; SyncSlidersToMouse();
                if (result.IsMultiFrame)
                {
                    _isMultiFrame = true; _totalFrames = result.TotalFrames; _activeMultiFramePath = filePath; _currentFrame = (_jumpToFrame != -1 && _jumpToFrame < _totalFrames) ? _jumpToFrame : 0; _jumpToFrame = -1;
                    sliceSlider.Enabled = true; sliceSlider.Minimum = 0; sliceSlider.Maximum = _totalFrames - 1; sliceSlider.Value = _currentFrame; btnPrev.Enabled = true; btnNext.Enabled = true; btnPlay.Enabled = true; if (lblSlice != null) lblSlice.Text = $"Frame {_currentFrame + 1} / {_totalFrames}";
                }
                else { _isMultiFrame = false; _totalFrames = 1; _currentFrame = 0; UpdateSlider(); _jumpToFrame = -1; if (lblSlice != null) lblSlice.Text = $"{_currentIndex + 1} / {_seriesFiles.Count}"; }
                var old = _loadedImage; _loadedImage = ApplyRotation(result.Picture); if (_isNewLoad) { FitToWindow(); _isNewLoad = false; } else InvalidateViewers(); old?.Dispose(); txtData.Text = result.MetadataText; SetStatus(result.StatusText);
            }
            catch (Exception ex) { Cursor = Cursors.Default; pictureBox1.BackColor = SDColor.FromArgb(8, 8, 8); SetStatus($"DICOM Error: {ex.Message}"); StopPlayback(); _currentMode = ViewerMode.None; SetSliderVisibility(ViewerMode.None); }
        }

        private async void LoadDicomFrame(int frameIndex)
        {
            if (!_isMultiFrame || _currentMode != ViewerMode.Dicom) return;
            _currentFrame = Math.Max(0, Math.Min(frameIndex, _totalFrames - 1));
            if (sliceSlider != null && sliceSlider.Value != _currentFrame) sliceSlider.Value = _currentFrame;
            if (_isRenderingFrame) { _needsAnotherFrameRender = true; return; }
            _isRenderingFrame = true; _needsAnotherFrameRender = false;
            try { var img = await Task.Run(() => DicomEngine.RenderFrame(_currentFrame)); if (img != null) { var old = _loadedImage; _loadedImage = ApplyRotation(img); InvalidateViewers(); old?.Dispose(); UpdateFrameLabel(); } } catch { } finally { _isRenderingFrame = false; if (_needsAnotherFrameRender) LoadDicomFrame(_currentFrame); }
        }

        private async void RequestContrastUpdate()
        {
            if (_currentMode != ViewerMode.Dicom) return;
            if (_isRenderingContrast) { _needsAnotherRender = true; return; }
            _isRenderingContrast = true; _needsAnotherRender = false;
            double tWC = _currentWC, tWW = _currentWW; int fIdx = _currentFrame;
            try { var img = await Task.Run(() => DicomEngine.AdjustContrast(tWC, tWW, fIdx)); if (img != null) { var old = _loadedImage; _loadedImage = ApplyRotation(img); InvalidateViewers(); old?.Dispose(); } } catch { } finally { _isRenderingContrast = false; if (_needsAnotherRender) RequestContrastUpdate(); }
        }

        private void TbContrast_ValueChanged(object sender, EventArgs e) { if (_isUpdatingSliders) return; _currentWW = tbContrast.Value; lblContrast.Text = $"DICOM WW: {_currentWW}"; if (_currentMode == ViewerMode.Dicom) RequestContrastUpdate(); }
        private void TbBrightness_ValueChanged(object sender, EventArgs e) { if (_isUpdatingSliders) return; _currentWC = tbBrightness.Value; lblBrightness.Text = $"DICOM WC: {_currentWC}"; if (_currentMode == ViewerMode.Dicom) RequestContrastUpdate(); }
        private void SyncSlidersToMouse() { _isUpdatingSliders = true; tbContrast.Value = (int)Math.Max(1, Math.Min(_currentWW, 8000)); tbBrightness.Value = (int)Math.Max(-2000, Math.Min(_currentWC, 6000)); lblContrast.Text = $"DICOM WW: {tbContrast.Value}"; lblBrightness.Text = $"DICOM WC: {tbBrightness.Value}"; _isUpdatingSliders = false; }

        private void ProcessStandardImage(string filePath)
        {
            try { _currentMode = ViewerMode.StandardImage; SetSliderVisibility(ViewerMode.StandardImage); ShowChannelBar(false); if (_isNewLoad) { _matrixRotation = 0; btnRotateData.Text = "🔄 Rotate: 0°"; } _isMultiFrame = false; _totalFrames = 1; _currentFrame = 0; _jumpToFrame = -1; var img = SDImage.FromStream(new MemoryStream(File.ReadAllBytes(filePath))); var bmp = new Bitmap(img); var old = _loadedImage; _loadedImage = ApplyRotation(bmp); if (_isNewLoad) { FitToWindow(); _isNewLoad = false; } else InvalidateViewers(); old?.Dispose(); txtData.Text = $"Standard Image\r\n{Path.GetFileName(filePath)}\r\n{img.Width}x{img.Height}"; UpdateSlider(); } catch (Exception ex) { SetStatus($"Image Error: {ex.Message}"); _currentMode = ViewerMode.None; }
        }

        private void TogglePlayback() { if (_totalFrames <= 1 && _seriesFiles.Count <= 1) return; _isPlaying = !_isPlaying; if (_isPlaying) { btnPlay.Text = "⏸ Pause"; btnPlay.BackColor = SDColor.FromArgb(200, 60, 60); cineTimer.Start(); } else { btnPlay.Text = "▶ Play"; btnPlay.BackColor = SDColor.FromArgb(40, 44, 58); cineTimer.Stop(); } }
        private void StopPlayback() { if (_isPlaying) TogglePlayback(); }
        private void ClearAll() { StopPlayback(); cbSeries.Items.Clear(); _seriesGroups.Clear(); _seriesFiles.Clear(); lstPinned.Items.Clear(); _currentIndex = 0; _currentFrame = 0; _totalFrames = 1; _isMultiFrame = false; _currentMode = ViewerMode.None; SetSliderVisibility(ViewerMode.None); ShowChannelBar(false); _loadedImage?.Dispose(); _loadedImage = null; ClearCaches(); _lutBuilt = false; _h5TissueRaw = null; _h5VxRaw = null; _h5VyRaw = null; _h5VzRaw = null; _h5VelRaw = null; _h5PowerRaw = null; _h5EcgData = null; _rawMaskData = null; _hasVfm = false; sliceSlider.Enabled = false; btnPrev.Enabled = false; btnNext.Enabled = false; btnPlay.Enabled = false; InvalidateViewers(); txtData.Text = "Cleared."; lblSlice.Text = "—"; SetStatus("Ready"); }

        private void BtnPin_Click(object sender, EventArgs e) { if (_seriesFiles.Count == 0 || cbSeries.SelectedItem == null) return; var pin = new PinnedSlice { SeriesName = cbSeries.SelectedItem.ToString(), FilePath = _seriesFiles[_currentIndex], SliceIndex = _currentIndex, FrameIndex = _currentFrame, IsMultiFrame = _isMultiFrame || _currentMode == ViewerMode.Hdf5Polar }; lstPinned.Items.Add(pin); SetStatus("Pinned!"); }
        private void LstPinned_DoubleClick(object sender, EventArgs e) { if (lstPinned.SelectedItem is PinnedSlice pin) { if (cbSeries.SelectedItem?.ToString() != pin.SeriesName) { _jumpToSlice = pin.SliceIndex; _jumpToFrame = pin.IsMultiFrame ? pin.FrameIndex : -1; cbSeries.SelectedItem = pin.SeriesName; } else if (_currentIndex != pin.SliceIndex) { _jumpToFrame = pin.IsMultiFrame ? pin.FrameIndex : -1; LoadSlice(pin.SliceIndex); } else { if (pin.IsMultiFrame) { _currentFrame = pin.FrameIndex; sliceSlider.Value = _currentFrame; if (_currentMode == ViewerMode.Dicom) LoadDicomFrame(_currentFrame); else { InvalidateViewers(); UpdateRealTimeMetrics(); } } } } }
        private void BtnSnapshot_Click(object sender, EventArgs e) { SetStatus("Snapshot function configured."); }
        private Label MakeLabel(string text) => new Label { Text = text, ForeColor = SDColor.FromArgb(180, 210, 255), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new SDFont("Segoe UI", 8.5f) };
        private static double ReadAttr(IH5Dataset ds, string name, double def) { try { return ds.Attribute(name).Read<double>(); } catch { try { return ds.Attribute(name).Read<float>(); } catch { return def; } } }
        private static double ReadAttr1(IH5Dataset ds, string name, double def) { try { return ds.Attribute(name).Read<double[]>().ElementAtOrDefault(1); } catch { try { return ds.Attribute(name).Read<float[]>().ElementAtOrDefault(1); } catch { return def; } } }
        private void SetStatus(string msg) { if (lblStatus != null) lblStatus.Text = " " + msg; }
        private void StyleButton(Button b, string text, SDColor bg) { b.Text = text; b.Font = new SDFont("Segoe UI", 9.5f, FontStyle.Bold); b.Dock = DockStyle.Fill; b.BackColor = bg; b.ForeColor = SDColor.White; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; b.Cursor = Cursors.Hand; b.Margin = new Padding(4); }
        private void StyleNavButton(Button b, string text) { b.Text = text; b.Font = new SDFont("Segoe UI", 9f); b.Dock = DockStyle.Fill; b.BackColor = SDColor.FromArgb(40, 44, 58); b.ForeColor = SDColor.White; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; b.Cursor = Cursors.Hand; b.Margin = new Padding(2); }

        private class FileScanInfo { public string Path; public string SeriesUID; public string DisplayName; public int InstanceNumber; }

        private async void LoadFolder(string folderPath)
        {
            try
            {
                SetStatus("Scanning folder (Multi-Core)...");
                cbSeries.Enabled = false;

                var scanned = new List<FileScanInfo>();

                await Task.Run(() =>
                {
                    var rawFiles = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories)
                        .Where(f => { string ext = Path.GetExtension(f).ToLower(); return ext == ".dcm" || ext == ".ima" || ext == "" || ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".h5" || ext == ".vfm"; }).ToList();

                    if (rawFiles.Count == 0) return;
                    object listLock = new object();

                    Parallel.ForEach(rawFiles, f =>
                    {
                        string ext = Path.GetExtension(f).ToLower();
                        FileScanInfo info = null;

                        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
                        {
                            info = new FileScanInfo { Path = f, SeriesUID = "IMG", DisplayName = "Standard Images" };
                        }
                        else if (ext == ".h5" || ext == ".vfm")
                        {
                            info = new FileScanInfo { Path = f, SeriesUID = f, DisplayName = Path.GetFileNameWithoutExtension(f) };
                        }
                        else
                        {
                            try
                            {
                                var dcm = DicomFile.Open(f);
                                info = new FileScanInfo
                                {
                                    Path = f,
                                    SeriesUID = dcm.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, "Unknown"),
                                    DisplayName = dcm.Dataset.GetSingleValueOrDefault(DicomTag.SeriesDescription, "DICOM Series"),
                                    InstanceNumber = dcm.Dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0)
                                };
                            }
                            catch { }
                        }

                        if (info != null) { lock (listLock) { scanned.Add(info); } }
                    });
                });

                _seriesGroups.Clear(); cbSeries.Items.Clear();
                if (scanned.Count == 0) { SetStatus("No valid files found."); cbSeries.Enabled = true; return; }

                var grouped = scanned.GroupBy(x => x.SeriesUID).ToList();
                foreach (var group in grouped)
                {
                    var sorted = group.OrderBy(x => x.InstanceNumber).Select(x => x.Path).ToList(); string cName = "";
                    if (group.First().SeriesUID.EndsWith(".h5", StringComparison.OrdinalIgnoreCase) || group.First().SeriesUID.EndsWith(".vfm", StringComparison.OrdinalIgnoreCase)) { cName = $"🧬 {group.First().DisplayName} (HDF5/VFM)"; }
                    else { string shortUid = group.Key.Length > 5 ? group.Key.Substring(group.Key.Length - 5) : group.Key; cName = $"{group.First().DisplayName} [{shortUid}] ({sorted.Count} files)"; }
                    _seriesGroups[cName] = sorted; cbSeries.Items.Add(cName);
                }

                cbSeries.Enabled = true;
                if (cbSeries.Items.Count > 0) cbSeries.SelectedIndex = 0;
                SetStatus($"Folder loaded: {grouped.Count} series.");
            }
            catch (Exception ex) { SetStatus($"Folder Scan Error: {ex.Message}"); cbSeries.Enabled = true; }
        }

        private async void ProcessHdf5Polar(string path)
        {
            try
            {
                SetStatus("Reading HDF5 Array (Async)...");
                _currentMode = ViewerMode.Hdf5Polar; ClearCaches(); _lutBuilt = false;
                SetSliderVisibility(ViewerMode.Hdf5Polar);
                Cursor = Cursors.WaitCursor; pictureBox1.BackColor = SDColor.FromArgb(15, 15, 15);

                await Task.Run(() =>
                {
                    using var h5 = H5File.OpenRead(path);
                    var allNames = h5.Children().Select(c => c.Name).ToList();

                    string ecgName = allNames.FirstOrDefault(n => n.Equals("Ecg", StringComparison.OrdinalIgnoreCase) || n.Equals("TimeStamps", StringComparison.OrdinalIgnoreCase) || n.Equals("Physio", StringComparison.OrdinalIgnoreCase));
                    if (ecgName != null) { var ecgDs = h5.Dataset(ecgName); try { _h5EcgData = ecgDs.Read<float[]>(); } catch { try { _h5EcgData = ecgDs.Read<double[]>().Select(v => (float)v).ToArray(); } catch { try { _h5EcgData = ecgDs.Read<short[]>().Select(v => (float)v).ToArray(); } catch { } } } }

                    string tissueName = allNames.FirstOrDefault(n => n.Equals("Tissue", StringComparison.OrdinalIgnoreCase) || n.Equals("MMode", StringComparison.OrdinalIgnoreCase) || n.Equals("VFMTissue", StringComparison.OrdinalIgnoreCase));
                    if (tissueName == null) throw new Exception($"No Tissue dataset found! Available datasets are: {string.Join(", ", allNames)}");

                    _isMMode = tissueName.Equals("MMode", StringComparison.OrdinalIgnoreCase);
                    var tDs = h5.Dataset(tissueName); var tDims = tDs.Space.Dimensions;
                    if (tDims.Length >= 3) { _tFrames = (int)tDims[0]; _tDepth = (int)tDims[1]; _tWidth = (int)tDims[2]; } else if (tDims.Length == 2) { _tFrames = 1; _tDepth = (int)tDims[0]; _tWidth = (int)tDims[1]; }

                    _tWidth = Math.Max(1, _tWidth); _tDepth = Math.Max(1, _tDepth);
                    _h5TissueRaw = tDs.Read<byte[]>();
                    _tWidthRad = ReadAttr(tDs, "Width", 1.57); _tOrigoY = ReadAttr1(tDs, "Origo", 0.0); _tDepthStart = ReadAttr(tDs, "DepthStart", 0.0); _tDepthEnd = ReadAttr(tDs, "DepthEnd", 0.15);

                    _h5VelRaw = null; _vFrames = 1; _vDepth = 1; _vWidth = 1; _vNyquist = 0.41;
                    string velName = allNames.FirstOrDefault(n => n.Equals("FlowVelocity", StringComparison.OrdinalIgnoreCase) || n.Equals("ColorMModeVelocity", StringComparison.OrdinalIgnoreCase) || n.Equals("VFMFlowColorRaw", StringComparison.OrdinalIgnoreCase));
                    if (velName != null) { var vDs = h5.Dataset(velName); var vDims = vDs.Space.Dimensions; if (vDims.Length >= 3) { _vFrames = (int)vDims[0]; _vDepth = (int)vDims[1]; _vWidth = (int)vDims[2]; } else if (vDims.Length == 2) { _vFrames = 1; _vDepth = (int)vDims[0]; _vWidth = (int)vDims[1]; } _h5VelRaw = vDs.Read<float[]>(); _vWidthRad = ReadAttr(vDs, "Width", 1.57); _vDepthStart = ReadAttr(vDs, "DepthStart", 0.0); _vDepthEnd = ReadAttr(vDs, "DepthEnd", 0.15); _vNyquist = ReadAttr(vDs, "VNyquist", 0.41); }

                    _h5PowerRaw = null; string powerName = allNames.FirstOrDefault(n => n.Equals("FlowPower", StringComparison.OrdinalIgnoreCase) || n.Equals("ColorMModePower", StringComparison.OrdinalIgnoreCase));
                    if (powerName != null) { try { _h5PowerRaw = h5.Dataset(powerName).Read<float[]>(); } catch { } }

                    string vxName = allNames.FirstOrDefault(n => n.Equals("Vx", StringComparison.OrdinalIgnoreCase) || n.Equals("VelocityX", StringComparison.OrdinalIgnoreCase) || n.Equals("VFMFlowVelocityTheta", StringComparison.OrdinalIgnoreCase));
                    string vyName = allNames.FirstOrDefault(n => n.Equals("Vy", StringComparison.OrdinalIgnoreCase) || n.Equals("VelocityY", StringComparison.OrdinalIgnoreCase) || n.Equals("VFMFlowVelocityRadial", StringComparison.OrdinalIgnoreCase));

                    if (vxName != null && vyName != null)
                    {
                        try
                        {
                            _h5VxRaw = h5.Dataset(vxName).Read<float[]>(); _h5VyRaw = h5.Dataset(vyName).Read<float[]>(); _hasVfm = true;
                            var vxDs = h5.Dataset(vxName); var vDims = vxDs.Space.Dimensions;
                            if (_vDepth == 1 && _vWidth == 1) { if (vDims.Length >= 3) { _vFrames = (int)vDims[0]; _vDepth = (int)vDims[1]; _vWidth = (int)vDims[2]; } else if (vDims.Length == 2) { _vFrames = 1; _vDepth = (int)vDims[0]; _vWidth = (int)vDims[1]; } }
                        }
                        catch { _hasVfm = false; }
                    }
                    else { _h5VxRaw = null; _h5VyRaw = null; _hasVfm = false; }

                    _vWidth = Math.Max(1, _vWidth); _vDepth = Math.Max(1, _vDepth);
                    _totalFrames = Math.Max(_tFrames, _vFrames);
                    if (_isNewLoad) { _matrixRotation = 1; }
                    if (_isMMode) { _canvasW = Math.Max(1, (_matrixRotation % 2 != 0) ? _tDepth : _tWidth); _canvasH = Math.Max(1, (_matrixRotation % 2 != 0) ? _tWidth : _tDepth); _lutBuilt = true; } else { BuildPolarLuts(); }

                    string ecgStatus = _h5EcgData != null ? "FOUND ✅" : $"NOT FOUND ❌\r\nAvailable Tags: {string.Join(", ", allNames)}";
                    _baseMetadataText = $"─── VFM METADATA ───\r\nType: {(_isMMode ? "M-Mode (Cartesian)" : "2D Sector (Polar)")}\r\nTissue: {_tDepth}x{_tWidth}\r\nDoppler: {(_h5VelRaw != null ? $"{_vDepth}x{_vWidth}" : "None")}\r\nVFM Flow: {(_hasVfm ? "FOUND ✅" : "None ❌")}\r\nNyquist: {_vNyquist:F3}\r\nFrames: {_totalFrames}\r\nECG Signal: {ecgStatus}";
                });

                Cursor = Cursors.Default; pictureBox1.BackColor = SDColor.FromArgb(8, 8, 8);
                if (_isNewLoad) { btnRotateData.Text = "🔄 Rotate: 90°"; }
                _zoom = 1.0f; _offset = new PointF(0, 0);

                sliceSlider.Maximum = Math.Max(0, _totalFrames - 1);
                _currentFrame = (_jumpToFrame != -1 && _jumpToFrame < _totalFrames) ? _jumpToFrame : 0; _jumpToFrame = -1;
                sliceSlider.Value = _currentFrame;
                sliceSlider.Enabled = _totalFrames > 1; btnPrev.Enabled = _totalFrames > 1; btnNext.Enabled = _totalFrames > 1; btnPlay.Enabled = _totalFrames > 1;

                FitToWindow(); txtData.Text = _baseMetadataText; UpdateRealTimeMetrics();

                if (_hasVfm)
                {
                    _temporalVelocities = new double[_totalFrames];
                    _isTemporalScanDone = false;
                    double safeGate = 1.5 + tbPowerGate.Value * 0.05;

                    _ = Task.Run(() =>
                    {
                        Parallel.For(0, _totalFrames, i =>
                        {
                            var m = HemodynamicCalculator.AnalyzeFrame(_h5VxRaw, _h5VyRaw, _vDepth, _vWidth, i, _h5PowerRaw, safeGate);
                            _temporalVelocities[i] = m.PeakVelocity;
                        });
                        _isTemporalScanDone = true;
                        pictureBox1.Invoke(new Action(() => InvalidateViewers()));
                    });
                }

                SetStatus($"HDF5 Loaded: {Path.GetFileName(path)}");
            }
            catch (Exception ex) { Cursor = Cursors.Default; pictureBox1.BackColor = SDColor.FromArgb(8, 8, 8); txtData.Text = $"HDF5 Error:\r\n{ex.Message}\r\n\r\n{ex.StackTrace}"; SetStatus("Load failed."); _currentMode = ViewerMode.None; SetSliderVisibility(ViewerMode.None); }
        }

        private void UpdateRealTimeMetrics()
        {
            if (!_hasVfm || _currentMode != ViewerMode.Hdf5Polar) return;
            double pGate = 1.5 + tbPowerGate.Value * 0.05;
            var metrics = HemodynamicCalculator.AnalyzeFrame(_h5VxRaw, _h5VyRaw, _vDepth, _vWidth, _currentFrame, _h5PowerRaw, pGate);

            txtData.Text = _baseMetadataText +
                $"\r\n\r\n── REAL-TIME HEMODYNAMICS ──\r\n" +
                $"Frame         : {_currentFrame + 1} / {_totalFrames}\r\n" +
                $"Peak Velocity : {metrics.PeakVelocity:F3} m/s\r\n" +
                $"Pressure Drop : {metrics.PressureDrop:F2} mmHg\r\n" +
                $"Mean Velocity : {metrics.MeanVelocity:F3} m/s\r\n" +
                $"Max Vorticity : {metrics.MaxVorticity:F2} 1/s\r\n" +
                $"Energy Loss   : {metrics.TotalEnergyLoss:F5} J/m³s\r\n" +
                $"Active Vectors: {metrics.ValidDataPoints}";
        }

        private void BuildPolarLuts()
        {
            int tEffW = (_matrixRotation % 2 != 0) ? _tDepth : _tWidth; int tEffH = (_matrixRotation % 2 != 0) ? _tWidth : _tDepth;
            double depthRange = Math.Max(0.01, _tDepthEnd - _tDepthStart); double pxPerM = tEffH / depthRange;
            double apexPx = Math.Abs(_tOrigoY) * pxPerM; double totalR = tEffH + apexPx; double halfA_t = _tWidthRad / 2.0; const double SC = 2.0;

            _canvasW = Math.Max(10, Math.Min(4000, (int)(2.0 * totalR * Math.Sin(halfA_t) * SC) + 40));
            _canvasH = Math.Max(10, Math.Min(4000, (int)(totalR * SC) + 20));
            _originX = _canvasW / 2; _originY = (int)(apexPx * SC);

            int npx = _canvasW * _canvasH; _tLutRow = new int[npx]; _tLutCol = new int[npx]; _vLutRow = new int[npx]; _vLutCol = new int[npx];
            for (int i = 0; i < npx; i++) { _tLutRow[i] = -1; _vLutRow[i] = -1; }

            int vEffW = (_matrixRotation % 2 != 0) ? _vDepth : _vWidth; int vEffH = (_matrixRotation % 2 != 0) ? _vWidth : _vDepth;
            double halfA_v = _vWidthRad / 2.0; double v_rowStepM = Math.Max(0.01, _vDepthEnd - _vDepthStart) / vEffH;

            for (int py = 0; py < _canvasH; py++)
            {
                double dy = py - _originY; if (dy < 0) continue;
                for (int px = 0; px < _canvasW; px++)
                {
                    double dx = px - _originX; double r = Math.Sqrt(dx * dx + dy * dy), theta = Math.Atan2(dx, dy); int idx = py * _canvasW + px;
                    if (r >= apexPx * SC && r <= totalR * SC && Math.Abs(theta) <= halfA_t)
                    {
                        int rY = (int)(r / SC - apexPx), rX = (int)((theta + halfA_t) / _tWidthRad * tEffW); int oX = rX, oY = rY;
                        if (_matrixRotation == 1) { oX = rY; oY = tEffW - 1 - rX; } else if (_matrixRotation == 2) { oX = tEffW - 1 - rX; oY = tEffH - 1 - rY; } else if (_matrixRotation == 3) { oX = tEffH - 1 - rY; oY = rX; }
                        _tLutRow[idx] = Math.Max(0, Math.Min(_tDepth - 1, oY)); _tLutCol[idx] = Math.Max(0, Math.Min(_tWidth - 1, oX));
                    }
                    if (Math.Abs(theta) <= halfA_v)
                    {
                        int rY = (int)(((r / SC) / pxPerM + _tDepthStart - _vDepthStart) / v_rowStepM), rX = (int)((theta + halfA_v) / _vWidthRad * vEffW);
                        if (rY >= 0 && rY < vEffH && rX >= 0 && rX < vEffW)
                        {
                            int oX = rX, oY = rY;
                            if (_matrixRotation == 1) { oX = rY; oY = vEffW - 1 - rX; } else if (_matrixRotation == 2) { oX = vEffW - 1 - rX; oY = vEffH - 1 - rY; } else if (_matrixRotation == 3) { oX = vEffH - 1 - rY; oY = rX; }
                            _vLutRow[idx] = oY; _vLutCol[idx] = oX;
                        }
                    }
                }
            }
            _lutBuilt = true;
        }
    }
}