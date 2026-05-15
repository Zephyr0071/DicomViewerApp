#nullable enable

using FellowOakDicom;
using FellowOakDicom.Imaging;
using PureHDF;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using SDColor = System.Drawing.Color;
using SDFont = System.Drawing.Font;
using SDImage = System.Drawing.Image;
using SDSize = System.Drawing.Size;

namespace DicomViewerApp
{
    public class AdvancedPictureBox : PictureBox
    {
        public AdvancedPictureBox()
        {
            this.DoubleBuffered = true;
            this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            this.UpdateStyles();
        }
        protected override void OnPaintBackground(PaintEventArgs pevent) { }
    }

    public class PinnedSlice
    {
        public string SeriesName { get; set; } = "";
        public string FilePath { get; set; } = "";
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
        private TableLayoutPanel? mainLayout, imageSplitLayout, _toolbar;
        private AdvancedPictureBox? pictureBox1, pictureBox2;
        private TextBox? txtData;
        private ListBox? lstPinned;
        private ComboBox? cbSeries, cbColormap;
        private Label? lblStatus, lblSlice;
        private Label? lblContrast, lblBrightness, lblH5Contrast, lblH5Brightness, lblGamma, lblPowerGate, lblColormapLabel;
        private TrackBar? sliceSlider, speedSlider, tbContrast, tbBrightness, tbH5Contrast, tbH5Brightness, tbGamma, tbPowerGate;
        private Button? btnOpenFile, btnOpenFolder, btnClear, btnPlay, btnPrev, btnNext;
        private Button? btnPin, btnSnapshot, btnRotateData, btnToggleFlow, btnToggleHeatmap, btnSplitView, btnPdfReport;
        private Button[]? _channelButtons;
        private NumericUpDown? numSpeed;
        private Panel? navPanel;
        private System.Windows.Forms.Timer cineTimer;

        private ViewerMode _currentMode = ViewerMode.None;
        private SeriesChannel _activeChannel = SeriesChannel.M;
        private Dictionary<string, List<string>> _seriesGroups = new Dictionary<string, List<string>>();
        private List<string> _seriesFiles = new List<string>();
        private int _currentIndex = 0, _currentFrame = 0, _totalFrames = 1;
        private bool _isPlaying = false, _isNewLoad = true, _isSplitView = false;
        private int _jumpToSlice = -1, _jumpToFrame = -1;

        private float _zoom = 1.0f;
        private PointF _offset = new PointF(0, 0);
        private Point _mouseStart;
        private bool _isDragging = false, _isAdjustingWL = false, _isUpdatingSliders = false;
        private bool _isRenderingFrame = false, _needsAnotherFrameRender = false;
        private Point _wlMouseStart;
        private double _currentWC = 0, _currentWW = 0;

        private SDImage? _loadedImage;
        private bool _isMultiFrame = false;
        private string _baseMetadataText = "";

        private byte[]? _h5TissueRaw, _h5CineRaw;
        private float[]? _h5VelRaw, _h5PowerRaw, _h5EcgData, _h5VxRaw, _h5VyRaw, _h5VzRaw;
        private bool _hasVfm = false, _lutBuilt = false, _isMMode = false, _showFlow = false, _showHeatmap = false;

        private int _tFrames = 1, _tDepth = 1, _tWidth = 1, _vFrames = 1, _vDepth = 1, _vWidth = 1;
        private double _tWidthRad, _tOrigoY, _tDepthStart, _tDepthEnd, _vWidthRad, _vDepthStart, _vDepthEnd, _vNyquist = 1.0;
        private int _canvasW = 10, _canvasH = 10, _originX, _originY, _matrixRotation = 0;
        private int[]? _tLutRow, _tLutCol, _vLutRow, _vLutCol;

        private string _currentColormap = "Grayscale";
        private byte[] _lutR = new byte[256], _lutG = new byte[256], _lutB = new byte[256];

        private const int MAX_CACHE_FRAMES = 200;
        private readonly object _cacheLock = new object();
        private Dictionary<int, Bitmap> _tissueCache = new Dictionary<int, Bitmap>(); private Queue<int> _tKeys = new Queue<int>();
        private Dictionary<int, Bitmap> _dopplerCache = new Dictionary<int, Bitmap>(); private Queue<int> _vKeys = new Queue<int>();

        private class FileScanInfo { public string Path = ""; public string SeriesUID = ""; public string DisplayName = ""; public int InstanceNumber; }

        public Form1()
        {
            QuestPDF.Settings.License = LicenseType.Community;
            this.DoubleBuffered = true;
            this.ClientSize = new SDSize(1250, 800);
            this.MinimumSize = new SDSize(1100, 700);
            this.BackColor = SDColor.FromArgb(18, 22, 30);
            this.Text = "Medical 2D/Flow Viewer";

            cineTimer = new System.Windows.Forms.Timer { Interval = 66 };
            BuildUI();
            new DicomSetupBuilder().RegisterServices(s => s.AddFellowOakDicom().AddImageManager<WinFormsImageManager>()).Build();

            SetSliderVisibility(ViewerMode.None);
            SetChannelButtonsVisibility(false);
        }

        private void BuildUI()
        {
            this.SuspendLayout();

            mainLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72f));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340f));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52f));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 60f));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28f));

            pictureBox1 = new AdvancedPictureBox { Dock = DockStyle.Fill, AllowDrop = true };
            pictureBox2 = new AdvancedPictureBox { Dock = DockStyle.Fill };

            txtData = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, BackColor = SDColor.FromArgb(20, 24, 35), ForeColor = SDColor.Cyan };
            lstPinned = new ListBox { Dock = DockStyle.Fill, BackColor = SDColor.FromArgb(26, 30, 42), ForeColor = SDColor.Orange };
            cbSeries = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = SDColor.FromArgb(40, 44, 58), ForeColor = SDColor.White, Margin = new Padding(4, 12, 4, 4) };
            lblStatus = new Label { Dock = DockStyle.Fill, ForeColor = SDColor.Gray, Text = " Ready", TextAlign = ContentAlignment.MiddleLeft };
            lblSlice = new Label { Dock = DockStyle.Fill, ForeColor = SDColor.White, TextAlign = ContentAlignment.MiddleCenter };
            sliceSlider = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 0 };
            speedSlider = new TrackBar { Dock = DockStyle.Fill, Minimum = 1, Maximum = 120, Value = 30 };
            numSpeed = new NumericUpDown { Dock = DockStyle.Fill, Minimum = 1, Maximum = 120, Value = 30 };
            cineTimer.Interval = 33;

            tbContrast = new TrackBar { Dock = DockStyle.Fill, Minimum = 1, Maximum = 8000 };
            tbBrightness = new TrackBar { Dock = DockStyle.Fill, Minimum = -2000, Maximum = 6000 };
            tbH5Contrast = new TrackBar { Dock = DockStyle.Fill, Minimum = 1, Maximum = 200, Value = 20 };
            tbH5Brightness = new TrackBar { Dock = DockStyle.Fill, Minimum = -100, Maximum = 100, Value = 0 };
            tbGamma = new TrackBar { Dock = DockStyle.Fill, Minimum = 1, Maximum = 30, Value = 12 };
            tbPowerGate = new TrackBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 40, Value = 10 };
            cbColormap = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            cbColormap.Items.AddRange(new string[] { "Grayscale", "Sepia", "Jet", "Ocean" }); cbColormap.SelectedIndex = 0;

            lblContrast = MakeLabel("DICOM WW"); lblBrightness = MakeLabel("DICOM WC");
            lblH5Contrast = MakeLabel("H5 Contrast"); lblH5Brightness = MakeLabel("H5 Brightness");
            lblGamma = MakeLabel("Gamma"); lblPowerGate = MakeLabel("Gate"); lblColormapLabel = MakeLabel("Tissue LUT");

            btnOpenFile = new Button(); btnOpenFolder = new Button(); btnClear = new Button(); btnPlay = new Button();
            btnPrev = new Button(); btnNext = new Button(); btnPin = new Button(); btnSnapshot = new Button();
            btnRotateData = new Button(); btnToggleFlow = new Button(); btnToggleHeatmap = new Button();
            btnSplitView = new Button(); btnPdfReport = new Button();

            _toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 17, RowCount = 1, BackColor = SDColor.FromArgb(22, 26, 36) };
            _toolbar.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70f));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70f));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            for (int i = 0; i < 6; i++) _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0f));

            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60f));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60f));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65f));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55f));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55f));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55f));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55f));
            _toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 55f));

            StyleButton(btnOpenFile, "📂 File", SDColor.FromArgb(0, 120, 212)); StyleButton(btnOpenFolder, "📁 Fold", SDColor.FromArgb(0, 150, 136));
            StyleButton(btnPin, "📌 Pin", SDColor.FromArgb(200, 120, 0)); StyleButton(btnSnapshot, "📸 Save", SDColor.FromArgb(0, 150, 100));
            StyleButton(btnRotateData, "🔄 Rot", SDColor.FromArgb(160, 80, 180));
            StyleButton(btnToggleFlow, "🔴 Flow", SDColor.FromArgb(80, 80, 80));
            StyleButton(btnToggleHeatmap, "🔥 Heat", SDColor.FromArgb(80, 80, 80));
            StyleButton(btnSplitView, "◫ Split", SDColor.FromArgb(80, 100, 150));
            StyleButton(btnPdfReport, "📄 PDF", SDColor.FromArgb(200, 50, 50)); StyleButton(btnClear, "❌ Clr", SDColor.FromArgb(140, 50, 50));

            _channelButtons = new Button[6];
            string[] chNames = { "C", "M", "AP", "SI", "RL", "P" };
            for (int i = 0; i < 6; i++)
            {
                _channelButtons[i] = new Button();
                StyleButton(_channelButtons[i], chNames[i], SDColor.FromArgb(40, 44, 58));
                int idx = i;
                _channelButtons[i].Click += (s, e) => SetChannel((SeriesChannel)idx);
            }

            _toolbar.Controls.Add(btnOpenFile, 0, 0); _toolbar.Controls.Add(btnOpenFolder, 1, 0); _toolbar.Controls.Add(cbSeries, 2, 0);
            for (int i = 0; i < 6; i++) _toolbar.Controls.Add(_channelButtons[i], 3 + i, 0);
            _toolbar.Controls.Add(btnPin, 9, 0); _toolbar.Controls.Add(btnSnapshot, 10, 0); _toolbar.Controls.Add(btnRotateData, 11, 0);
            _toolbar.Controls.Add(btnToggleFlow, 12, 0); _toolbar.Controls.Add(btnToggleHeatmap, 13, 0); _toolbar.Controls.Add(btnSplitView, 14, 0);
            _toolbar.Controls.Add(btnPdfReport, 15, 0); _toolbar.Controls.Add(btnClear, 16, 0);
            mainLayout.Controls.Add(_toolbar, 0, 0); mainLayout.SetColumnSpan(_toolbar, 2);

            // ✨ BACK TO NORMAL: We put the PictureBoxes directly into the grid
            imageSplitLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            imageSplitLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            imageSplitLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 0f));
            imageSplitLayout.Controls.Add(pictureBox1, 0, 0);
            imageSplitLayout.Controls.Add(pictureBox2, 1, 0);
            mainLayout.Controls.Add(imageSplitLayout, 0, 1);

            var rightPanel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3 };
            rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 40f));
            rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 30f));
            rightPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 220f));
            rightPanel.Controls.Add(CreateGroupPanel("METADATA", txtData), 0, 0);
            rightPanel.Controls.Add(CreateGroupPanel("PINS", lstPinned), 0, 1);

            var cTable = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 2 };
            cTable.Controls.Add(lblContrast, 0, 0); cTable.Controls.Add(tbContrast, 1, 0);
            cTable.Controls.Add(lblH5Contrast, 0, 0); cTable.Controls.Add(tbH5Contrast, 1, 0);
            cTable.Controls.Add(lblBrightness, 0, 1); cTable.Controls.Add(tbBrightness, 1, 1);
            cTable.Controls.Add(lblH5Brightness, 0, 1); cTable.Controls.Add(tbH5Brightness, 1, 1);
            cTable.Controls.Add(lblGamma, 0, 2); cTable.Controls.Add(tbGamma, 1, 2);
            cTable.Controls.Add(lblPowerGate, 0, 3); cTable.Controls.Add(tbPowerGate, 1, 3);
            cTable.Controls.Add(lblColormapLabel, 0, 4); cTable.Controls.Add(cbColormap, 1, 4);
            rightPanel.Controls.Add(CreateGroupPanel("CONTROLS", cTable), 0, 2);
            mainLayout.Controls.Add(rightPanel, 1, 1);

            navPanel = new Panel { Dock = DockStyle.Fill };
            var nav = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, BackColor = SDColor.FromArgb(22, 26, 36) };
            nav.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60f));
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100f));
            nav.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80f));

            StyleNavButton(btnPlay, "▶ Play"); StyleNavButton(btnPrev, "◀ Prev"); StyleNavButton(btnNext, "Next ▶");
            nav.Controls.Add(btnPlay, 0, 0); nav.Controls.Add(btnPrev, 1, 0); nav.Controls.Add(sliceSlider, 2, 0);
            nav.Controls.Add(btnNext, 3, 0); nav.Controls.Add(numSpeed, 4, 0); nav.Controls.Add(speedSlider, 5, 0); nav.Controls.Add(lblSlice, 6, 0);
            navPanel.Controls.Add(nav);
            mainLayout.Controls.Add(navPanel, 0, 2); mainLayout.SetColumnSpan(navPanel, 2);

            mainLayout.Controls.Add(lblStatus, 0, 3); mainLayout.SetColumnSpan(lblStatus, 2);
            this.Controls.Add(mainLayout);

            // WIRING
            btnOpenFile.Click += (s, e) => OpenFile();
            btnOpenFolder.Click += (s, e) => OpenFolder();
            btnClear.Click += (s, e) => ClearWorkspace();
            btnPlay.Click += (s, e) => TogglePlayback();
            btnPrev.Click += (s, e) => Navigate(-1);
            btnNext.Click += (s, e) => Navigate(1);

            speedSlider.ValueChanged += (s, e) => {
                if (numSpeed != null && speedSlider != null)
                {
                    numSpeed.Value = speedSlider.Value;
                    cineTimer.Interval = Math.Max(1, 1000 / speedSlider.Value);
                }
            };
            numSpeed.ValueChanged += (s, e) => {
                if (numSpeed != null && speedSlider != null)
                {
                    speedSlider.Value = (int)numSpeed.Value;
                    cineTimer.Interval = Math.Max(1, 1000 / (int)numSpeed.Value);
                }
            };

            cbSeries.SelectedIndexChanged += CbSeries_SelectedIndexChanged;
            sliceSlider.ValueChanged += SliceSlider_MasterChanged;

            btnPin.Click += BtnPin_Click;
            btnSnapshot.Click += BtnSnapshot_Click;
            btnRotateData.Click += BtnRotateData_Click;
            btnToggleFlow.Click += BtnToggleFlow_Click;
            btnToggleHeatmap.Click += BtnToggleHeatmap_Click;
            btnSplitView.Click += BtnSplitView_Click;
            btnPdfReport.Click += BtnPdfReport_Click;
            lstPinned.DoubleClick += LstPinned_DoubleClick;

            cbColormap.SelectedIndexChanged += (s, e) => {
                _currentColormap = cbColormap.SelectedItem?.ToString() ?? "Grayscale";
                BuildColormapLUT(); ClearCaches(); InvalidateViewers();
            };

            pictureBox1.Paint += PictureBox1_Paint;
            var unifyMouseWheel = new MouseEventHandler(PictureBox_MouseWheel); var unifyMouseDown = new MouseEventHandler(PictureBox_MouseDown); var unifyMouseMove = new MouseEventHandler(PictureBox_MouseMove); var unifyMouseUp = new MouseEventHandler(PictureBox_MouseUp);
            pictureBox1.MouseWheel += unifyMouseWheel; pictureBox1.MouseDown += unifyMouseDown; pictureBox1.MouseMove += unifyMouseMove; pictureBox1.MouseUp += unifyMouseUp;

            cineTimer.Tick += (s, e) => {
                if (_currentMode == ViewerMode.Hdf5Polar && _totalFrames > 0)
                {
                    _currentFrame = (_currentFrame + 1) % _totalFrames;
                    UpdateSliderSafe(_totalFrames - 1, _currentFrame);
                    InvalidateViewers();
                }
                else if (_isMultiFrame)
                {
                    int next = _currentFrame + 1;
                    if (next >= _totalFrames) next = 0;
                    LoadDicomFrame(next);
                }
                else
                {
                    if (_seriesFiles.Count <= 1) { StopPlayback(); return; }
                    cineTimer.Stop();
                    int next = _currentIndex + 1;
                    if (next >= _seriesFiles.Count) next = 0;
                    LoadSlice(next, true);
                }
            };
            BuildColormapLUT();

            this.ResumeLayout(true);
        }

        private void SetChannelButtonsVisibility(bool visible)
        {
            if (_channelButtons == null || _toolbar == null) return;
            for (int i = 0; i < 6; i++)
            {
                _channelButtons[i].Visible = visible;
                _toolbar.ColumnStyles[3 + i].SizeType = SizeType.Absolute;
                _toolbar.ColumnStyles[3 + i].Width = visible ? (i == 2 ? 40f : 35f) : 0f;
            }
        }

        private void SetChannel(SeriesChannel ch)
        {
            _activeChannel = ch;

            if (_channelButtons != null)
            {
                for (int i = 0; i < 6; i++)
                {
                    _channelButtons[i].BackColor = ((SeriesChannel)i == ch) ? SDColor.FromArgb(0, 120, 212) : SDColor.FromArgb(40, 44, 58);
                }
            }
            ClearCaches();
            InvalidateViewers();
        }

        private void SetSliderVisibility(ViewerMode mode)
        {
            bool showDicom = (mode == ViewerMode.Dicom);
            bool showH5 = (mode == ViewerMode.Hdf5Polar);
            if (lblContrast != null) lblContrast.Visible = showDicom;
            if (tbContrast != null) tbContrast.Visible = showDicom;
            if (lblBrightness != null) lblBrightness.Visible = showDicom;
            if (tbBrightness != null) tbBrightness.Visible = showDicom;

            if (lblH5Contrast != null) lblH5Contrast.Visible = showH5;
            if (tbH5Contrast != null) tbH5Contrast.Visible = showH5;
            if (lblH5Brightness != null) lblH5Brightness.Visible = showH5;
            if (tbH5Brightness != null) tbH5Brightness.Visible = showH5;
            if (lblGamma != null) lblGamma.Visible = showH5;
            if (tbGamma != null) tbGamma.Visible = showH5;
            if (lblPowerGate != null) lblPowerGate.Visible = showH5;
            if (tbPowerGate != null) tbPowerGate.Visible = showH5;
            if (lblColormapLabel != null) lblColormapLabel.Visible = showH5;
            if (cbColormap != null) cbColormap.Visible = showH5;
        }

        private void ClearWorkspace()
        {
            ClearView();
            if (cbSeries != null)
            {
                cbSeries.SelectedIndexChanged -= CbSeries_SelectedIndexChanged;
                cbSeries.Items.Clear();
                cbSeries.SelectedIndexChanged += CbSeries_SelectedIndexChanged;
            }
            _seriesGroups.Clear();
            _seriesFiles.Clear();
            if (lstPinned != null) lstPinned.Items.Clear();
            _currentIndex = 0;
            if (txtData != null) txtData.Text = "Workspace Cleared.";
        }

        private void ClearView(bool keepPlaying = false)
        {
            if (!keepPlaying) StopPlayback();
            DicomEngine.Reset();
            _currentFrame = 0; _totalFrames = 1; _isMultiFrame = false; _currentMode = ViewerMode.None;

            SetSliderVisibility(ViewerMode.None);
            SetChannelButtonsVisibility(false);

            if (pictureBox1 != null) pictureBox1.Image = null;

            _loadedImage?.Dispose(); _loadedImage = null;
            ClearCaches();
            GC.Collect(); GC.WaitForPendingFinalizers();

            _lutBuilt = false; _h5TissueRaw = null; _h5CineRaw = null;
            _h5VxRaw = null; _h5VyRaw = null; _h5VzRaw = null; _h5VelRaw = null;
            _h5PowerRaw = null; _h5EcgData = null; _hasVfm = false;

            _showFlow = false;
            if (btnToggleFlow != null) btnToggleFlow.BackColor = SDColor.FromArgb(80, 80, 80);

            UpdateSliderSafe(0, 0);
            if (!keepPlaying)
            {
                if (btnPrev != null) btnPrev.Enabled = false;
                if (btnNext != null) btnNext.Enabled = false;
                if (btnPlay != null) btnPlay.Enabled = false;
            }

            InvalidateViewers();
            if (lblSlice != null) lblSlice.Text = "—";
            SetStatus("Ready");
        }

        private void UpdateSliderSafe(int max, int val)
        {
            if (sliceSlider == null) return;
            sliceSlider.ValueChanged -= SliceSlider_MasterChanged;
            sliceSlider.Enabled = max > 0;
            sliceSlider.Minimum = 0;
            sliceSlider.Maximum = Math.Max(0, max);
            sliceSlider.Value = Math.Max(0, Math.Min(val, max));
            sliceSlider.ValueChanged += SliceSlider_MasterChanged;
        }

        private void UpdateSlider()
        {
            if (_currentMode == ViewerMode.Hdf5Polar || _isMultiFrame) return;
            bool multi = _seriesFiles.Count > 1;
            UpdateSliderSafe(_seriesFiles.Count - 1, _currentIndex);
            if (btnPrev != null) btnPrev.Enabled = multi;
            if (btnNext != null) btnNext.Enabled = multi;
            if (btnPlay != null) btnPlay.Enabled = multi;
        }

        private void SliceSlider_MasterChanged(object? sender, EventArgs e)
        {
            if (sliceSlider == null || !sliceSlider.Enabled) return;
            if (_currentMode == ViewerMode.Hdf5Polar) { _currentFrame = sliceSlider.Value; InvalidateViewers(); }
            else if (_isMultiFrame) { _currentFrame = sliceSlider.Value; LoadDicomFrame(_currentFrame); }
            else { if (sliceSlider.Value != _currentIndex) LoadSlice(sliceSlider.Value); }
        }

        private void BtnPin_Click(object? sender, EventArgs e)
        {
            if (_seriesFiles.Count == 0 || cbSeries?.SelectedItem == null) return;
            var pin = new PinnedSlice
            {
                SeriesName = cbSeries.SelectedItem.ToString() ?? "",
                FilePath = _seriesFiles[_currentIndex],
                SliceIndex = _currentIndex,
                FrameIndex = _currentFrame,
                IsMultiFrame = _isMultiFrame || _currentMode == ViewerMode.Hdf5Polar
            };
            if (lstPinned != null) lstPinned.Items.Add(pin);
            SetStatus("Frame Pinned!");
        }

        private void LstPinned_DoubleClick(object? sender, EventArgs e)
        {
            if (lstPinned?.SelectedItem is PinnedSlice pin)
            {
                if (cbSeries?.SelectedItem?.ToString() != pin.SeriesName)
                {
                    _jumpToSlice = pin.SliceIndex;
                    _jumpToFrame = pin.IsMultiFrame ? pin.FrameIndex : -1;
                    cbSeries!.SelectedItem = pin.SeriesName;
                }
                else if (_currentIndex != pin.SliceIndex)
                {
                    _jumpToFrame = pin.IsMultiFrame ? pin.FrameIndex : -1;
                    LoadSlice(pin.SliceIndex);
                }
                else
                {
                    if (pin.IsMultiFrame)
                    {
                        _currentFrame = pin.FrameIndex;
                        UpdateSliderSafe(_totalFrames - 1, _currentFrame);
                        if (_currentMode == ViewerMode.Dicom) LoadDicomFrame(_currentFrame);
                        else InvalidateViewers();
                    }
                }
            }
        }

        private void BtnSnapshot_Click(object? sender, EventArgs e)
        {
            if (pictureBox1?.Image == null && _currentMode != ViewerMode.Hdf5Polar) return;
            using var sfd = new SaveFileDialog { Filter = "PNG Image|*.png", FileName = $"Snapshot_{DateTime.Now:yyyyMMdd_HHmmss}.png" };
            if (sfd.ShowDialog() == DialogResult.OK)
            {
                if (_currentMode == ViewerMode.Hdf5Polar)
                {
                    var bmp = new Bitmap(pictureBox1!.Width, pictureBox1.Height);
                    pictureBox1.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                    bmp.Save(sfd.FileName, System.Drawing.Imaging.ImageFormat.Png);
                }
                else
                {
                    pictureBox1!.Image!.Save(sfd.FileName, System.Drawing.Imaging.ImageFormat.Png);
                }
                SetStatus($"Snapshot Saved: {Path.GetFileName(sfd.FileName)}");
            }
        }

        private void BtnRotateData_Click(object? sender, EventArgs e)
        {
            _matrixRotation = (_matrixRotation + 1) % 4;
            if (btnRotateData != null) btnRotateData.Text = $"🔄 Rot: {_matrixRotation * 90}°";
            if (_currentMode == ViewerMode.Hdf5Polar)
            {
                if (_isMMode) BuildCartesianLuts(); else BuildPolarLuts();
                ClearCaches(); FitToWindow();
            }
            else if (_currentMode == ViewerMode.Dicom && _loadedImage != null)
            {
                LoadDicomFrame(_currentFrame);
            }
            else if (_currentMode == ViewerMode.StandardImage && _loadedImage != null)
            {
                _loadedImage.RotateFlip(RotateFlipType.Rotate90FlipNone); InvalidateViewers();
            }
        }

        private void BtnToggleFlow_Click(object? sender, EventArgs e)
        {
            _showFlow = !_showFlow;
            if (btnToggleFlow != null) btnToggleFlow.BackColor = _showFlow ? SDColor.FromArgb(60, 120, 60) : SDColor.FromArgb(80, 80, 80);
            InvalidateViewers();
        }

        private void BtnToggleHeatmap_Click(object? sender, EventArgs e)
        {
            _showHeatmap = !_showHeatmap;
            if (btnToggleHeatmap != null) btnToggleHeatmap.BackColor = _showHeatmap ? SDColor.FromArgb(160, 80, 40) : SDColor.FromArgb(80, 80, 80);
            InvalidateViewers();
        }

        private void BtnSplitView_Click(object? sender, EventArgs e)
        {
            _isSplitView = !_isSplitView;
            if (imageSplitLayout != null)
            {
                imageSplitLayout.ColumnStyles[0].Width = _isSplitView ? 50f : 100f;
                imageSplitLayout.ColumnStyles[1].Width = _isSplitView ? 50f : 0f;
            }
            if (btnSplitView != null) btnSplitView.BackColor = _isSplitView ? SDColor.FromArgb(40, 120, 160) : SDColor.FromArgb(80, 100, 150);
            InvalidateViewers();
        }

        private void BtnPdfReport_Click(object? sender, EventArgs e)
        {
            if (txtData == null || string.IsNullOrEmpty(txtData.Text)) return;
            using var sfd = new SaveFileDialog { Filter = "PDF Report|*.pdf", FileName = $"MedicalReport_{DateTime.Now:yyyyMMdd_HHmmss}.pdf" };
            if (sfd.ShowDialog() == DialogResult.OK)
            {
                SetStatus("Generating PDF..."); Cursor = Cursors.WaitCursor;
                try
                {
                    var doc = QuestPDF.Fluent.Document.Create(container => {
                        container.Page(page => {
                            page.Size(QuestPDF.Helpers.PageSizes.A4);
                            page.Margin(2, QuestPDF.Infrastructure.Unit.Centimetre);
                            page.Header().Text("Medical Imaging Report").SemiBold().FontSize(20);
                            page.Content().PaddingVertical(1, QuestPDF.Infrastructure.Unit.Centimetre).Column(col => {
                                col.Item().Text(txtData.Text).FontSize(12);
                                col.Item().PaddingTop(20).Text($"Generated on: {DateTime.Now}").FontSize(10).FontColor(QuestPDF.Helpers.Colors.Grey.Medium);
                            });
                        });
                    });
                    doc.GeneratePdf(sfd.FileName);
                    SetStatus($"PDF Saved: {Path.GetFileName(sfd.FileName)}");
                }
                catch (Exception ex) { SetStatus($"PDF Error: {ex.Message}"); }
                Cursor = Cursors.Default;
            }
        }

        private void OpenFile()
        {
            StopPlayback();
            using var d = new OpenFileDialog();
            // ✨ ADDED: `.itsf` back to the file filters
            d.Filter = "Medical Files|*.dcm;*.dicom;*.ima;*.png;*.jpg;*.h5;*.vfm;*.itflow2;*.itsp;*.itsf|All Files|*.*";
            if (d.ShowDialog() == DialogResult.OK) LoadSingleFile(d.FileName);
        }

        private void OpenFolder()
        {
            StopPlayback();
            using var d = new FolderBrowserDialog();
            if (d.ShowDialog() == DialogResult.OK) LoadFolder(d.SelectedPath);
        }

        private async void LoadFolder(string folderPath)
        {
            ClearWorkspace();
            SetStatus("Scanning folder...");
            if (cbSeries != null) cbSeries.Enabled = false;
            var scanned = new List<FileScanInfo>();

            await Task.Run(() =>
            {
                var rawFiles = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories)
                .Where(f => {
                    string ext = Path.GetExtension(f).ToLower();
                    return string.IsNullOrEmpty(ext) || ext == ".dcm" || ext == ".dicom" || ext == ".ima" || ext == ".png" || ext == ".jpg" || ext == ".h5" || ext == ".vfm" || ext == ".itflow2" || ext == ".itsp" || ext == ".itsf";
                }).ToList();

                object listLock = new object();
                Parallel.ForEach(rawFiles, f =>
                {
                    string ext = Path.GetExtension(f).ToLower();
                    FileScanInfo? info = null;
                    if (ext == ".png" || ext == ".jpg") info = new FileScanInfo { Path = f, SeriesUID = "IMG", DisplayName = "Images" };
                    else if (ext == ".h5" || ext == ".vfm" || ext == ".itflow2" || ext == ".itsp" || ext == ".itsf") info = new FileScanInfo { Path = f, SeriesUID = f, DisplayName = Path.GetFileNameWithoutExtension(f) };
                    else
                    {
                        try
                        {
                            if (Path.GetFileName(f).Equals("DICOMDIR", StringComparison.OrdinalIgnoreCase)) return;
                            var dcm = DicomFile.Open(f);
                            string seriesDesc = dcm.Dataset.GetSingleValueOrDefault(DicomTag.SeriesDescription, "");
                            if (string.IsNullOrWhiteSpace(seriesDesc)) seriesDesc = dcm.Dataset.GetSingleValueOrDefault(DicomTag.Modality, "DICOM Series");
                            info = new FileScanInfo { Path = f, SeriesUID = dcm.Dataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, Guid.NewGuid().ToString()), DisplayName = seriesDesc, InstanceNumber = dcm.Dataset.GetSingleValueOrDefault(DicomTag.InstanceNumber, 0) };
                        }
                        catch { }
                    }
                    if (info != null) { lock (listLock) { scanned.Add(info); } }
                });
            });

            _seriesGroups.Clear();
            if (cbSeries != null)
            {
                cbSeries.SelectedIndexChanged -= CbSeries_SelectedIndexChanged;
                cbSeries.Items.Clear();
            }

            var grouped = scanned.GroupBy(x => x.SeriesUID).ToList();
            foreach (var group in grouped)
            {
                var sorted = group.OrderBy(x => x.InstanceNumber).Select(x => x.Path).ToList();
                string cName = group.First().SeriesUID.EndsWith(".h5", StringComparison.OrdinalIgnoreCase) ? $"🧬 {group.First().DisplayName}" : (group.First().SeriesUID.EndsWith(".itflow2", StringComparison.OrdinalIgnoreCase) || group.First().SeriesUID.EndsWith(".itsp", StringComparison.OrdinalIgnoreCase) || group.First().SeriesUID.EndsWith(".itsf", StringComparison.OrdinalIgnoreCase) ? $"🫀 {group.First().DisplayName}" : $"{group.First().DisplayName} ({sorted.Count} files)");
                _seriesGroups[cName] = sorted;
                if (cbSeries != null) cbSeries.Items.Add(cName);
            }
            if (cbSeries != null)
            {
                cbSeries.Enabled = true;
                cbSeries.SelectedIndexChanged += CbSeries_SelectedIndexChanged;
                if (cbSeries.Items.Count > 0) cbSeries.SelectedIndex = 0;
            }
            SetStatus($"Folder loaded: {grouped.Count} series.");
        }

        private void LoadSingleFile(string path)
        {
            ClearWorkspace();
            string ext = Path.GetExtension(path).ToLower();
            string name = (ext == ".h5" || ext == ".vfm" || ext == ".itflow2" || ext == ".itsp" || ext == ".itsf") ? "Fluid Dynamics" : "Image/DICOM";
            string cName = $"{name} (1 file)";
            _seriesGroups[cName] = new List<string> { path };
            if (cbSeries != null) { cbSeries.Items.Add(cName); cbSeries.SelectedIndex = 0; }
        }

        private void CbSeries_SelectedIndexChanged(object? sender, EventArgs e)
        {
            StopPlayback();
            if (cbSeries == null || cbSeries.SelectedItem == null) return;
            string sel = cbSeries.SelectedItem.ToString() ?? "";
            if (_seriesGroups.ContainsKey(sel))
            {
                _seriesFiles = _seriesGroups[sel];
                _currentIndex = (_jumpToSlice != -1 && _jumpToSlice < _seriesFiles.Count) ? _jumpToSlice : 0;
                _jumpToSlice = -1;
                _isNewLoad = true;
                RouteFile(_seriesFiles[_currentIndex]);
            }
        }

        private void LoadSlice(int index, bool keepPlaying = false)
        {
            _currentIndex = index;
            if (!_isMultiFrame && _currentMode != ViewerMode.Hdf5Polar && sliceSlider != null) sliceSlider.Value = index;
            RouteFile(_seriesFiles[index], keepPlaying);
        }

        private void RouteFile(string filePath, bool keepPlaying = false)
        {
            ClearView(keepPlaying);
            try
            {
                string ext = Path.GetExtension(filePath).ToLower();
                if (ext == ".h5" || ext == ".vfm") ProcessHdf5Polar(filePath);
                else if (ext == ".itflow2") ProcessItflow2(filePath);
                else if (ext == ".itsf") ProcessItsf3D(filePath); // ✨ ADDED: Route to the 3D pop-up
                else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp") ProcessStandardImage(filePath);
                else ProcessDicomEngine(filePath);
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                if (txtData != null) txtData.Text = $"ERROR:\r\n{ex.Message}";
                SetStatus("Load Error.");
                StopPlayback();
            }
        }

        // ✨ NEW METHOD: Asynchronously loads the 3D data and opens it in the standalone popup
        private async void ProcessItsf3D(string filePath)
        {
            SetStatus("Parsing ITSF surface…");
            Cursor = Cursors.WaitCursor;

            ItsfMesh? mesh = null;
            try
            {
                long size = new System.IO.FileInfo(filePath).Length;
                if (size > 50 * 1024 * 1024)
                {
                    Task.Run(() => ItsfParser.Load(filePath)).ContinueWith(t =>
                    {
                        this.Invoke(() =>
                        {
                            Cursor = Cursors.Default;
                            if (t.Exception != null) { SetStatus("ITSF error: " + t.Exception.InnerException?.Message); return; }
                            OpenItsfViewer(t.Result, filePath);
                        });
                    });
                    return;
                }
                mesh = ItsfParser.Load(filePath);
            }
            catch (Exception ex)
            {
                Cursor = Cursors.Default;
                SetStatus("ITSF parse error: " + ex.Message);
                if (txtData != null) txtData.Text = "ITSF Error:\r\n" + ex.Message;
                return;
            }

            Cursor = Cursors.Default;
            OpenItsfViewer(mesh, filePath);
        }

        private void OpenItsfViewer(ItsfMesh mesh, string filePath)
        {
            if (txtData != null)
            {
                txtData.Text =
                    $"─── ITSF 3D SURFACE ───\r\n" +
                    $"File:      {System.IO.Path.GetFileName(filePath)}\r\n" +
                    $"Format:    {mesh.FormatDetected}\r\n" +
                    $"Vertices:  {mesh.VertexCount:N0}\r\n" +
                    $"Triangles: {mesh.TriangleCount:N0}\r\n" +
                    $"Scalars:   {(mesh.ScalarFields.Count > 0 ? string.Join(", ", mesh.ScalarFields.Keys) : "none")}\r\n" +
                    (string.IsNullOrEmpty(mesh.MetaInfo) ? "" : "\r\n" + mesh.MetaInfo);
            }

            SetStatus($"Opening 3D viewer — {mesh.TriangleCount:N0} triangles…");

            // ✨ Open the dedicated WPF-based Viewer3DForm
            var viewer = new Viewer3DForm(mesh, System.IO.Path.GetFileNameWithoutExtension(filePath));
            viewer.Owner = this;
            viewer.Show();

            SetStatus($"ITSF loaded: {mesh.VertexCount:N0} verts · {mesh.TriangleCount:N0} tris");
        }

        private byte[]? ExtractMatrix(object obj, params string[] names)
        {
            if (obj == null) return null;
            var type = obj.GetType();
            foreach (var name in names)
            {
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null) return prop.GetValue(obj) as byte[];
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field != null) return field.GetValue(obj) as byte[];
            }

            var allProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var name in names)
            {
                var match = allProps.FirstOrDefault(p => p.Name.Contains(name, StringComparison.OrdinalIgnoreCase) && p.PropertyType == typeof(byte[]));
                if (match != null) return match.GetValue(obj) as byte[];
            }
            return null;
        }

        private string? ExtractString(object obj, params string[] names)
        {
            if (obj == null) return null;
            var type = obj.GetType();
            foreach (var name in names)
            {
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null) return prop.GetValue(obj) as string;
                var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field != null) return field.GetValue(obj) as string;
            }
            return null;
        }

        private byte[] DecodeItflowMatrix(byte[] rawBytes, int pixelCount)
        {
            if (rawBytes == null || rawBytes.Length == 0) return new byte[pixelCount];
            byte[] result = new byte[pixelCount];

            bool isFloat = (rawBytes.Length >= pixelCount * 4);
            int step = isFloat ? 4 : 2;

            float[] temp = new float[pixelCount];
            double sum = 0;

            for (int i = 0; i < pixelCount; i++)
            {
                int bi = i * step;
                if (bi + (step - 1) < rawBytes.Length)
                {
                    float val = isFloat ? BitConverter.ToSingle(rawBytes, bi) : BitConverter.ToInt16(rawBytes, bi);
                    temp[i] = val;
                    sum += Math.Abs(val);
                }
            }

            float robustMax = (float)((sum / pixelCount) * 5.0);
            if (robustMax < 10) robustMax = 1000f;

            for (int i = 0; i < pixelCount; i++)
            {
                result[i] = (byte)Math.Max(0, Math.Min(255, (Math.Abs(temp[i]) / robustMax) * 255f));
            }
            return result;
        }

        private void ProcessItflow2(string filePath)
        {
            SetStatus("Extracting Binary Protobuf Stream..."); Cursor = Cursors.WaitCursor;
            try
            {
                using (var file = File.OpenRead(filePath))
                {
                    var savefile = ProtoBuf.Serializer.Deserialize<SaveModel>(file);

                    byte[]? magMatrix = ExtractMatrix(savefile, "MagMatrix", "MagnitudeMatrix", "Mag", "Phase1") ?? ExtractMatrix(savefile, "CineMatrix", "Cine");
                    byte[]? cineMatrix = ExtractMatrix(savefile, "CineMatrix", "Cine") ?? magMatrix;

                    string jsonInfo = "";
                    var jsonProp = savefile.GetType().GetProperty("Mag", BindingFlags.Public | BindingFlags.Instance) ?? savefile.GetType().GetProperty("Cine", BindingFlags.Public | BindingFlags.Instance);
                    if (jsonProp != null) jsonInfo = jsonProp.GetValue(savefile) as string ?? "";

                    if (!string.IsNullOrEmpty(jsonInfo) && magMatrix != null)
                    {
                        var jObj = Newtonsoft.Json.Linq.JObject.Parse(jsonInfo);
                        _tWidth = (int?)jObj.GetValue("Columns", StringComparison.OrdinalIgnoreCase) ?? 256;
                        _tDepth = (int?)jObj.GetValue("Rows", StringComparison.OrdinalIgnoreCase) ?? 256;
                        int slices = (int?)jObj.GetValue("Slices", StringComparison.OrdinalIgnoreCase) ?? 1;
                        int phases = (int?)jObj.GetValue("Phases", StringComparison.OrdinalIgnoreCase) ?? 1;

                        _tFrames = Math.Max(1, phases * slices);
                        int pixelCount = _tWidth * _tDepth * _tFrames;

                        _baseMetadataText = $"--- 4D FLOW DATA ---\r\nDimensions: {_tWidth}x{_tDepth}\r\nFrames: {_tFrames}";

                        _h5TissueRaw = DecodeItflowMatrix(DecompressMatrix(magMatrix), pixelCount);
                        _h5CineRaw = DecodeItflowMatrix(DecompressMatrix(cineMatrix), pixelCount);

                        if (_h5TissueRaw == null && _h5CineRaw != null) _h5TissueRaw = _h5CineRaw;
                        if (_h5CineRaw == null && _h5TissueRaw != null) _h5CineRaw = _h5TissueRaw;

                        byte[]? uMat = ExtractMatrix(savefile, "UMatrix", "VxMatrix", "PhaseXMatrix", "Phase1Matrix", "Vx", "VelocityX");
                        byte[]? vMat = ExtractMatrix(savefile, "VMatrix", "VyMatrix", "PhaseYMatrix", "Phase2Matrix", "Vy", "VelocityY");
                        byte[]? wMat = ExtractMatrix(savefile, "WMatrix", "VzMatrix", "PhaseZMatrix", "Phase3Matrix", "Vz", "VelocityZ");

                        _h5VxRaw = null; _h5VyRaw = null; _h5VzRaw = null; _h5VelRaw = null; _hasVfm = false;

                        if (uMat != null && vMat != null)
                        {
                            byte[] uBytes = DecompressMatrix(uMat);
                            byte[] vBytes = DecompressMatrix(vMat);
                            byte[]? wBytes = wMat != null ? DecompressMatrix(wMat) : null;

                            bool isFloat = (uBytes.Length >= pixelCount * 4);
                            int step = isFloat ? 4 : 2;

                            _h5VxRaw = new float[pixelCount]; _h5VyRaw = new float[pixelCount];
                            _h5VzRaw = new float[pixelCount]; _h5VelRaw = new float[pixelCount];

                            double sumV = 0; int countV = 0;
                            for (int i = 0; i < pixelCount; i++)
                            {
                                int bi = i * step;
                                if (bi + (step - 1) < uBytes.Length && bi + (step - 1) < vBytes.Length)
                                {
                                    float u = isFloat ? BitConverter.ToSingle(uBytes, bi) : BitConverter.ToInt16(uBytes, bi);
                                    float v = isFloat ? BitConverter.ToSingle(vBytes, bi) : BitConverter.ToInt16(vBytes, bi);
                                    float w = 0f;
                                    if (wBytes != null && bi + (step - 1) < wBytes.Length)
                                    {
                                        w = isFloat ? BitConverter.ToSingle(wBytes, bi) : BitConverter.ToInt16(wBytes, bi);
                                    }

                                    _h5VxRaw[i] = u; _h5VyRaw[i] = v; _h5VzRaw[i] = w;
                                    float mag = (float)Math.Sqrt(u * u + v * v + w * w);
                                    _h5VelRaw[i] = mag;

                                    if (mag > 0.05f) { sumV += mag; countV++; }
                                }
                            }

                            float meanV = countV > 0 ? (float)(sumV / countV) : 10f;
                            _vNyquist = meanV * 2.5f;
                            if (_vNyquist < 0.1f) _vNyquist = 1.0f;

                            _hasVfm = true; _vDepth = _tDepth; _vWidth = _tWidth; _vFrames = _tFrames;
                            _baseMetadataText += $"\r\nFlow Channels: ACTIVE\r\nEst. VENC: {_vNyquist:F1} cm/s";
                        }
                        else
                        {
                            _baseMetadataText += "\r\nFlow Channels: NOT FOUND";
                        }

                        if (txtData != null) txtData.Text = _baseMetadataText;

                        _currentMode = ViewerMode.Hdf5Polar; _isMMode = true; _totalFrames = _tFrames; _currentFrame = 0;

                        SetChannelButtonsVisibility(true);
                        BuildCartesianLuts();
                        SetSliderVisibility(ViewerMode.Hdf5Polar);

                        UpdateSliderSafe(_totalFrames - 1, 0);
                        if (btnPlay != null) btnPlay.Enabled = _totalFrames > 1;
                        if (btnPrev != null) btnPrev.Enabled = _totalFrames > 1;
                        if (btnNext != null) btnNext.Enabled = _totalFrames > 1;

                        SetChannel(SeriesChannel.M);
                        _loadedImage?.Dispose(); _loadedImage = null; FitToWindow(); pictureBox1?.Invalidate();
                    }
                    SetStatus("Extraction Complete.");
                }
            }
            catch (Exception ex) { if (txtData != null) txtData.Text = "Error decoding itflow2:\n" + ex.Message; }
            Cursor = Cursors.Default;
            if (_isPlaying) cineTimer.Start();
        }

        // ✨ FIXED: Added byte[]? to suppress the yellow null warning
        private byte[] DecompressMatrix(byte[]? input)
        {
            if (input == null || input.Length < 4) return input ?? Array.Empty<byte>();
            if (input[0] == 0x1F && input[1] == 0x8B) { try { using var ms = new MemoryStream(input); using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress); using var outMs = new MemoryStream(); gz.CopyTo(outMs); return outMs.ToArray(); } catch { } }
            try { using var ms = new MemoryStream(input); using var def = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress); using var outMs = new MemoryStream(); def.CopyTo(outMs); return outMs.ToArray(); } catch { return input; }
        }

        private async void ProcessDicomEngine(string filePath)
        {
            string expectedSeries = cbSeries?.SelectedItem?.ToString() ?? "";

            _currentMode = ViewerMode.Dicom;
            SetSliderVisibility(ViewerMode.Dicom);
            Cursor = Cursors.WaitCursor;

            var result = await Task.Run(() => DicomEngine.Process(filePath, _currentIndex + 1, _seriesFiles.Count));

            if (cbSeries?.SelectedItem?.ToString() != expectedSeries) { Cursor = Cursors.Default; return; }

            Cursor = Cursors.Default;
            if (result != null)
            {
                _currentWC = result.DefaultWC; _currentWW = result.DefaultWW;
                SyncSlidersToMouse();
                _isMultiFrame = result.IsMultiFrame; _totalFrames = result.TotalFrames;
                _currentFrame = (_jumpToFrame != -1 && _jumpToFrame < _totalFrames) ? _jumpToFrame : 0;
                _jumpToFrame = -1;

                if (_isMultiFrame)
                {
                    UpdateSliderSafe(_totalFrames - 1, _currentFrame);
                    if (btnPrev != null) btnPrev.Enabled = true;
                    if (btnNext != null) btnNext.Enabled = true;
                    if (btnPlay != null) btnPlay.Enabled = true;
                    UpdateFrameLabel();
                }
                else UpdateSlider();

                _loadedImage = ApplyRotation(result.Picture);
                if (_isNewLoad) { FitToWindow(); _isNewLoad = false; } else InvalidateViewers();

                if (txtData != null) txtData.Text = result.MetadataText;
                SetStatus(result.StatusText);
            }
            if (_isPlaying) cineTimer.Start();
        }

        private async void ProcessHdf5Polar(string path)
        {
            SetStatus("Reading HDF5 Array (Async)...");
            _currentMode = ViewerMode.Hdf5Polar; ClearCaches(); _lutBuilt = false;
            SetSliderVisibility(ViewerMode.Hdf5Polar);
            Cursor = Cursors.WaitCursor;

            await Task.Run(() =>
            {
                try
                {
                    using var h5 = H5File.OpenRead(path);
                    var allNames = h5.Children().Select(c => c.Name).ToList();

                    string? tissueName = allNames.FirstOrDefault(n => n.Equals("Tissue", StringComparison.OrdinalIgnoreCase) || n.Equals("MMode", StringComparison.OrdinalIgnoreCase) || n.Equals("VFMTissue", StringComparison.OrdinalIgnoreCase));
                    if (tissueName == null) throw new Exception("No Tissue dataset found!");

                    _isMMode = tissueName.Equals("MMode", StringComparison.OrdinalIgnoreCase);
                    var tDs = h5.Dataset(tissueName); var tDims = tDs.Space.Dimensions;
                    if (tDims.Length >= 3) { _tFrames = (int)tDims[0]; _tDepth = (int)tDims[1]; _tWidth = (int)tDims[2]; } else if (tDims.Length == 2) { _tFrames = 1; _tDepth = (int)tDims[0]; _tWidth = (int)tDims[1]; }

                    _tWidth = Math.Max(1, _tWidth); _tDepth = Math.Max(1, _tDepth);
                    _h5TissueRaw = tDs.Read<byte[]>();
                    _tWidthRad = ReadAttr(tDs, "Width", 1.57); _tOrigoY = ReadAttr1(tDs, "Origo", 0.0); _tDepthStart = ReadAttr(tDs, "DepthStart", 0.0); _tDepthEnd = ReadAttr(tDs, "DepthEnd", 0.15);

                    _h5VelRaw = null; _vFrames = 1; _vDepth = 1; _vWidth = 1; _vNyquist = 0.41;
                    string? velName = allNames.FirstOrDefault(n => n.Equals("FlowVelocity", StringComparison.OrdinalIgnoreCase) || n.Equals("ColorMModeVelocity", StringComparison.OrdinalIgnoreCase) || n.Equals("VFMFlowColorRaw", StringComparison.OrdinalIgnoreCase));
                    if (velName != null) { var vDs = h5.Dataset(velName); var vDims = vDs.Space.Dimensions; if (vDims.Length >= 3) { _vFrames = (int)vDims[0]; _vDepth = (int)vDims[1]; _vWidth = (int)vDims[2]; } else if (vDims.Length == 2) { _vFrames = 1; _vDepth = (int)vDims[0]; _vWidth = (int)vDims[1]; } _h5VelRaw = vDs.Read<float[]>(); _vWidthRad = ReadAttr(vDs, "Width", 1.57); _vDepthStart = ReadAttr(vDs, "DepthStart", 0.0); _vDepthEnd = ReadAttr(vDs, "DepthEnd", 0.15); _vNyquist = ReadAttr(vDs, "VNyquist", 0.41); }

                    _totalFrames = Math.Max(_tFrames, _vFrames);
                    if (_isNewLoad) { _matrixRotation = 1; }

                    if (_isMMode) { BuildCartesianLuts(); } else { BuildPolarLuts(); }

                    _baseMetadataText = $"─── VFM METADATA ───\r\nType: {(_isMMode ? "M-Mode" : "Polar")}\r\nTissue: {_tDepth}x{_tWidth}\r\nFrames: {_totalFrames}";
                }
                catch { }
            });

            Cursor = Cursors.Default;
            _zoom = 1.0f; _offset = new PointF(0, 0);

            UpdateSliderSafe(_totalFrames - 1, 0);
            if (btnPrev != null) btnPrev.Enabled = _totalFrames > 1;
            if (btnNext != null) btnNext.Enabled = _totalFrames > 1;
            if (btnPlay != null) btnPlay.Enabled = _totalFrames > 1;

            FitToWindow();
            if (txtData != null) txtData.Text = _baseMetadataText;
            SetStatus($"HDF5 Loaded: {Path.GetFileName(path)}");
            if (_isPlaying) cineTimer.Start();
        }

        private void ProcessStandardImage(string filePath)
        {
            _currentMode = ViewerMode.StandardImage; SetSliderVisibility(ViewerMode.StandardImage);
            _isMultiFrame = false; _totalFrames = 1; _currentFrame = 0; _jumpToFrame = -1;
            var img = SDImage.FromStream(new MemoryStream(File.ReadAllBytes(filePath)));
            _loadedImage = ApplyRotation(new Bitmap(img));
            if (_isNewLoad) { FitToWindow(); _isNewLoad = false; } else InvalidateViewers();
            if (txtData != null) txtData.Text = $"Standard Image\r\n{Path.GetFileName(filePath)}\r\n{img.Width}x{img.Height}";
            UpdateSlider();
            if (_isPlaying) cineTimer.Start();
        }

        private void PictureBox1_Paint(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(SDColor.Black);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            var state = g.Save(); g.TranslateTransform(_offset.X, _offset.Y); g.ScaleTransform(_zoom, _zoom);
            if (_currentMode == ViewerMode.Hdf5Polar && _lutBuilt)
            {
                var tbmp = GetTissueBitmap(_currentFrame); if (tbmp != null) g.DrawImage(tbmp, 0, 0);
                if (_showFlow) { var dbmp = GetDopplerBitmap(_currentFrame); if (dbmp != null) g.DrawImage(dbmp, 0, 0); }
            }
            else if (_loadedImage != null) { g.DrawImage(_loadedImage, new PointF(0, 0)); }
            g.Restore(state);
        }

        private Bitmap? GetTissueBitmap(int frame)
        {
            frame = Math.Max(0, Math.Min(_tFrames - 1, frame)); lock (_cacheLock) { if (_tissueCache.TryGetValue(frame, out var c)) return c; }
            double contrast = (tbH5Contrast?.Value ?? 10) / 10.0, brightness = tbH5Brightness?.Value ?? 0, gamma = (tbGamma?.Value ?? 12) / 10.0;
            int fOff = frame * _tDepth * _tWidth;

            var bmp = new Bitmap(_canvasW, _canvasH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, _canvasW, _canvasH), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat); byte[] pix = new byte[_canvasH * data.Stride];

            bool isVelocityChannel = _activeChannel == SeriesChannel.AP || _activeChannel == SeriesChannel.SI || _activeChannel == SeriesChannel.RL || _activeChannel == SeriesChannel.P;

            if (!isVelocityChannel || _showFlow)
            {
                byte[]? activeTissue = (_activeChannel == SeriesChannel.C && _h5CineRaw != null) ? _h5CineRaw : _h5TissueRaw;
                if (activeTissue != null)
                {
                    for (int i = 0; i < _canvasW * _canvasH; i++)
                    {
                        int row = _tLutRow?[i] ?? -1;
                        if (row < 0) { pix[i * 4 + 3] = 255; continue; }
                        int tIndex = fOff + row * _tWidth + _tLutCol![i];
                        if (tIndex >= activeTissue.Length) continue;

                        double rawNorm = Math.Pow(activeTissue[tIndex] / 255.0, gamma);
                        byte v = (byte)Math.Max(0, Math.Min(255, rawNorm * 255.0 * contrast + brightness));
                        pix[i * 4] = _lutB[v]; pix[i * 4 + 1] = _lutG[v]; pix[i * 4 + 2] = _lutR[v]; pix[i * 4 + 3] = 255;
                    }
                }
            }
            else
            {
                float[]? velSource = _h5VelRaw;
                if (_activeChannel == SeriesChannel.AP) velSource = _h5VyRaw;
                else if (_activeChannel == SeriesChannel.RL) velSource = _h5VxRaw;
                else if (_activeChannel == SeriesChannel.SI) velSource = _h5VzRaw;

                if (velSource != null)
                {
                    int vOff = frame * _vDepth * _vWidth;
                    for (int i = 0; i < _canvasW * _canvasH; i++)
                    {
                        int row = _vLutRow?[i] ?? -1;
                        if (row < 0) { pix[i * 4 + 3] = 255; continue; }
                        int vi = vOff + row * _vWidth + _vLutCol![i];
                        if (vi >= velSource.Length) continue;

                        float vel = velSource[vi];
                        if (float.IsNaN(vel) || float.IsInfinity(vel)) vel = 0f;

                        float norm = Math.Max(-1f, Math.Min(1f, (float)(vel / (_vNyquist <= 0.01 ? 0.41 : _vNyquist))));
                        byte v = (byte)((norm + 1f) * 127.5f);
                        double adjusted = Math.Max(0, Math.Min(255, (v - 128) * contrast + 128 + brightness));
                        byte finalV = (byte)adjusted;

                        pix[i * 4] = _lutB[finalV]; pix[i * 4 + 1] = _lutG[finalV]; pix[i * 4 + 2] = _lutR[finalV]; pix[i * 4 + 3] = 255;
                    }
                }
            }

            Marshal.Copy(pix, 0, data.Scan0, pix.Length); bmp.UnlockBits(data);
            lock (_cacheLock) { if (_tissueCache.Count >= MAX_CACHE_FRAMES) { int old = _tKeys.Dequeue(); if (_tissueCache.TryGetValue(old, out var ob)) { ob.Dispose(); _tissueCache.Remove(old); } } _tKeys.Enqueue(frame); return _tissueCache[frame] = bmp; }
        }

        private Bitmap? GetDopplerBitmap(int frame)
        {
            frame = Math.Max(0, Math.Min(_vFrames - 1, frame)); lock (_cacheLock) { if (_dopplerCache.TryGetValue(frame, out var c)) return c; }
            int vOff = frame * _vDepth * _vWidth; double pGate = 1.5 + (tbPowerGate?.Value ?? 10) * 0.05;

            float[]? velSource = _h5VelRaw;
            if (_activeChannel == SeriesChannel.AP) velSource = _h5VyRaw;
            else if (_activeChannel == SeriesChannel.RL) velSource = _h5VxRaw;
            else if (_activeChannel == SeriesChannel.SI) velSource = _h5VzRaw;
            else if (_activeChannel == SeriesChannel.P) velSource = _h5VelRaw;

            if (velSource == null) return null;
            var bmp = new Bitmap(_canvasW, _canvasH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, _canvasW, _canvasH), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat); byte[] pix = new byte[_canvasH * data.Stride];
            for (int i = 0; i < _canvasW * _canvasH; i++)
            {
                int row = _vLutRow?[i] ?? -1; if (row < 0) continue;
                int vi = vOff + row * _vWidth + _vLutCol![i];
                if (vi >= velSource.Length || (_h5PowerRaw != null && Math.Log10(Math.Max(1.0, _h5PowerRaw[vi])) < pGate)) continue;

                float vel = velSource[vi];
                if (float.IsNaN(vel) || float.IsInfinity(vel)) continue;

                float norm = Math.Max(-1f, Math.Min(1f, (float)(vel / _vNyquist)));
                float mag = Math.Abs(norm);

                if (mag < 0.08f)
                {
                    pix[i * 4 + 3] = 0;
                    continue;
                }

                byte intensity = (byte)(mag * 255);
                if (norm > 0)
                {
                    pix[i * 4 + 2] = 255;
                    pix[i * 4 + 1] = (byte)(255 - intensity);
                    pix[i * 4] = 0;
                }
                else
                {
                    pix[i * 4 + 2] = 0;
                    pix[i * 4 + 1] = (byte)(255 - intensity);
                    pix[i * 4] = 255;
                }
                pix[i * 4 + 3] = (byte)Math.Min(255, 100 + intensity);
            }
            Marshal.Copy(pix, 0, data.Scan0, pix.Length); bmp.UnlockBits(data);
            lock (_cacheLock) { if (_dopplerCache.Count >= MAX_CACHE_FRAMES) { int old = _vKeys.Dequeue(); if (_dopplerCache.TryGetValue(old, out var ob)) { ob.Dispose(); _dopplerCache.Remove(old); } } _vKeys.Enqueue(frame); return _dopplerCache[frame] = bmp; }
        }

        private void BuildPolarLuts()
        {
            int tEffW = (_matrixRotation % 2 != 0) ? _tDepth : _tWidth, tEffH = (_matrixRotation % 2 != 0) ? _tWidth : _tDepth; double depthRange = Math.Max(0.01, _tDepthEnd - _tDepthStart), pxPerM = tEffH / depthRange; double apexPx = Math.Abs(_tOrigoY) * pxPerM, totalR = tEffH + apexPx, halfA_t = _tWidthRad / 2.0; const double SC = 2.0;
            _canvasW = Math.Max(10, Math.Min(4000, (int)(2.0 * totalR * Math.Sin(halfA_t) * SC) + 40)); _canvasH = Math.Max(10, Math.Min(4000, (int)(totalR * SC) + 20)); _originX = _canvasW / 2; _originY = (int)(apexPx * SC); int npx = _canvasW * _canvasH; _tLutRow = new int[npx]; _tLutCol = new int[npx]; _vLutRow = new int[npx]; _vLutCol = new int[npx];
            for (int i = 0; i < npx; i++) { _tLutRow[i] = -1; _vLutRow[i] = -1; }
            int vEffW = (_matrixRotation % 2 != 0) ? _vDepth : _vWidth, vEffH = (_matrixRotation % 2 != 0) ? _vWidth : _vDepth; double halfA_v = _vWidthRad / 2.0, v_rowStepM = Math.Max(0.01, _vDepthEnd - _vDepthStart) / vEffH;
            for (int py = 0; py < _canvasH; py++) { double dy = py - _originY; if (dy < 0) continue; for (int px = 0; px < _canvasW; px++) { double dx = px - _originX, r = Math.Sqrt(dx * dx + dy * dy), theta = Math.Atan2(dx, dy); int idx = py * _canvasW + px; if (r >= apexPx * SC && r <= totalR * SC && Math.Abs(theta) <= halfA_t) { int rY = (int)(r / SC - apexPx), rX = (int)((theta + halfA_t) / _tWidthRad * tEffW); int oX = rX, oY = rY; if (_matrixRotation == 1) { oX = rY; oY = tEffW - 1 - rX; } else if (_matrixRotation == 2) { oX = tEffW - 1 - rX; oY = tEffH - 1 - rY; } else if (_matrixRotation == 3) { oX = tEffH - 1 - rY; oY = rX; } _tLutRow[idx] = Math.Max(0, Math.Min(_tDepth - 1, oY)); _tLutCol[idx] = Math.Max(0, Math.Min(_tWidth - 1, oX)); } if (Math.Abs(theta) <= halfA_v) { int rY = (int)(((r / SC) / pxPerM + _tDepthStart - _vDepthStart) / v_rowStepM), rX = (int)((theta + halfA_v) / _vWidthRad * vEffW); if (rY >= 0 && rY < vEffH && rX >= 0 && rX < vEffW) { int oX = rX, oY = rY; if (_matrixRotation == 1) { oX = rY; oY = vEffW - 1 - rX; } else if (_matrixRotation == 2) { oX = vEffW - 1 - rX; oY = vEffH - 1 - rY; } else if (_matrixRotation == 3) { oX = vEffH - 1 - rY; oY = rX; } _vLutRow[idx] = oY; _vLutCol[idx] = oX; } } } }
            _lutBuilt = true;
        }

        private void BuildCartesianLuts()
        {
            int tEffW = (_matrixRotation % 2 != 0) ? _tDepth : _tWidth;
            int tEffH = (_matrixRotation % 2 != 0) ? _tWidth : _tDepth;
            _canvasW = Math.Max(1, tEffW);
            _canvasH = Math.Max(1, tEffH);

            int npx = _canvasW * _canvasH;
            _tLutRow = new int[npx]; _tLutCol = new int[npx];
            _vLutRow = new int[npx]; _vLutCol = new int[npx];

            for (int py = 0; py < _canvasH; py++)
            {
                for (int px = 0; px < _canvasW; px++)
                {
                    int idx = py * _canvasW + px;

                    int oX = px, oY = py;
                    if (_matrixRotation == 1) { oX = py; oY = tEffW - 1 - px; }
                    else if (_matrixRotation == 2) { oX = tEffW - 1 - px; oY = tEffH - 1 - py; }
                    else if (_matrixRotation == 3) { oX = tEffH - 1 - py; oY = px; }

                    _tLutRow[idx] = Math.Max(0, Math.Min(_tDepth - 1, oY));
                    _tLutCol[idx] = Math.Max(0, Math.Min(_tWidth - 1, oX));

                    int vX = (int)((oX / (float)Math.Max(1, _tWidth)) * _vWidth);
                    int vY = (int)((oY / (float)Math.Max(1, _tDepth)) * _vDepth);

                    _vLutRow[idx] = Math.Max(0, Math.Min(_vDepth - 1, vY));
                    _vLutCol[idx] = Math.Max(0, Math.Min(_vWidth - 1, vX));
                }
            }
            _lutBuilt = true;
        }

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

        private void ClearCaches() { lock (_cacheLock) { foreach (var bmp in _tissueCache.Values) bmp?.Dispose(); _tissueCache.Clear(); _tKeys.Clear(); foreach (var bmp in _dopplerCache.Values) bmp?.Dispose(); _dopplerCache.Clear(); _vKeys.Clear(); } }
        private static double ReadAttr(IH5Dataset ds, string name, double def) { try { return ds.Attribute(name).Read<double>(); } catch { try { return ds.Attribute(name).Read<float>(); } catch { return def; } } }
        private static double ReadAttr1(IH5Dataset ds, string name, double def) { try { return ds.Attribute(name).Read<double[]>().ElementAtOrDefault(1); } catch { try { return ds.Attribute(name).Read<float[]>().ElementAtOrDefault(1); } catch { return def; } } }
        private void FitToWindow() { if (_currentMode == ViewerMode.Hdf5Polar && _canvasW > 0 && pictureBox1 != null) { float pWidth = pictureBox1.Width, avH = pictureBox1.Height - (_h5EcgData != null ? 110 : 0); if (_isMMode) { float scaleX = pWidth / _canvasW, scaleY = avH / _canvasH; _zoom = Math.Min(scaleX, scaleY) * 0.95f; _offset.X = (pWidth - _canvasW * _zoom) / 2f; _offset.Y = (avH - _canvasH * _zoom) / 2f; } else { int tEffH = (_matrixRotation % 2 != 0) ? _tWidth : _tDepth; double pxPerM = tEffH / Math.Max(0.01, _tDepthEnd - _tDepthStart); float left = (float)(_originX - (tEffH + Math.Abs(_tOrigoY) * pxPerM) * 2.0 * Math.Sin(_tWidthRad / 2)), right = (float)(_originX + (tEffH + Math.Abs(_tOrigoY) * pxPerM) * 2.0 * Math.Sin(_tWidthRad / 2)), top = _originY, bottom = (float)(_originY + (tEffH + Math.Abs(_tOrigoY) * pxPerM) * 2.0), sW = right - left, sH = bottom - top; if (sW > 0 && sH > 0) { _zoom = Math.Min(pWidth / sW, avH / sH) * 0.93f; _offset.X = (pWidth - sW * _zoom) / 2f - left * _zoom; _offset.Y = (avH - sH * _zoom) / 2f - top * _zoom; } } } else if (_loadedImage != null && pictureBox1 != null) { float scaleX = (float)pictureBox1.Width / _loadedImage.Width, scaleY = (float)pictureBox1.Height / _loadedImage.Height; _zoom = Math.Min(scaleX, scaleY); _offset.X = (pictureBox1.Width - _loadedImage.Width * _zoom) / 2f; _offset.Y = (pictureBox1.Height - _loadedImage.Height * _zoom) / 2f; } InvalidateViewers(); }
        private void InvalidateViewers() { pictureBox1?.Invalidate(); if (_isSplitView) pictureBox2?.Invalidate(); }
        private SDImage? ApplyRotation(SDImage? img) { if (img == null || _matrixRotation == 0) return img; if (_matrixRotation == 1) img.RotateFlip(RotateFlipType.Rotate90FlipNone); if (_matrixRotation == 2) img.RotateFlip(RotateFlipType.Rotate180FlipNone); if (_matrixRotation == 3) img.RotateFlip(RotateFlipType.Rotate270FlipNone); return img; }
        private void TogglePlayback() { if (_totalFrames <= 1 && _seriesFiles.Count <= 1) return; _isPlaying = !_isPlaying; if (btnPlay != null) { btnPlay.Text = _isPlaying ? "⏸ Pause" : "▶ Play"; btnPlay.BackColor = _isPlaying ? SDColor.FromArgb(200, 60, 60) : SDColor.FromArgb(40, 44, 58); } if (_isPlaying) cineTimer.Start(); else cineTimer.Stop(); }
        private void StopPlayback() { if (_isPlaying) TogglePlayback(); }
        private void Navigate(int delta) { StopPlayback(); if (_currentMode == ViewerMode.Hdf5Polar || _isMultiFrame) { int next = _currentFrame + delta; if (next >= 0 && next < _totalFrames) { _currentFrame = next; if (sliceSlider != null) sliceSlider.Value = next; } } else { int next = _currentIndex + delta; if (next >= 0 && next < _seriesFiles.Count) LoadSlice(next); } }
        private void LoadSlice(int index) { _currentIndex = index; if (!_isMultiFrame && _currentMode != ViewerMode.Hdf5Polar && sliceSlider != null) sliceSlider.Value = index; RouteFile(_seriesFiles[index]); }
        private void UpdateFrameLabel() { if (lblSlice != null) lblSlice.Text = $"{_currentFrame + 1} / {_totalFrames}"; }
        private void SyncSlidersToMouse() { _isUpdatingSliders = true; if (tbContrast != null) tbContrast.Value = (int)Math.Max(1, Math.Min(_currentWW, 8000)); if (tbBrightness != null) tbBrightness.Value = (int)Math.Max(-2000, Math.Min(_currentWC, 6000)); if (lblContrast != null) lblContrast.Text = $"DICOM WW: {tbContrast?.Value}"; if (lblBrightness != null) lblBrightness.Text = $"DICOM WC: {tbBrightness?.Value}"; _isUpdatingSliders = false; }
        private async void LoadDicomFrame(int frameIndex) { if (!_isMultiFrame || _currentMode != ViewerMode.Dicom) return; _currentFrame = Math.Max(0, Math.Min(frameIndex, _totalFrames - 1)); UpdateFrameLabel(); if (sliceSlider != null && sliceSlider.Value != _currentFrame) sliceSlider.Value = _currentFrame; if (_isRenderingFrame) { _needsAnotherFrameRender = true; return; } _isRenderingFrame = true; _needsAnotherFrameRender = false; try { var img = await Task.Run(() => DicomEngine.RenderFrame(_currentFrame)); if (img != null) { var old = _loadedImage; _loadedImage = ApplyRotation(img); InvalidateViewers(); old?.Dispose(); } } catch { } finally { _isRenderingFrame = false; if (_needsAnotherFrameRender) LoadDicomFrame(_currentFrame); } }
        private void PictureBox_MouseWheel(object? sender, MouseEventArgs e) { if (_currentMode == ViewerMode.None) return; bool isMultiFrameContent = (_totalFrames > 1 || _seriesFiles.Count > 1); if (ModifierKeys.HasFlag(Keys.Control) || !isMultiFrameContent) { float zoomChange = (e.Delta > 0) ? 1.15f : 0.85f; float newZoom = Math.Max(0.05f, Math.Min(50f, _zoom * zoomChange)); _offset.X = e.X - (e.X - _offset.X) * (newZoom / _zoom); _offset.Y = e.Y - (e.Y - _offset.Y) * (newZoom / _zoom); _zoom = newZoom; InvalidateViewers(); } else if (isMultiFrameContent) { if (_isPlaying) TogglePlayback(); Navigate(Math.Sign(e.Delta)); } }
        private void PictureBox_MouseDown(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && (_loadedImage != null || _lutBuilt)) { _isDragging = true; _mouseStart = e.Location; if (sender is PictureBox pb) pb.Cursor = Cursors.SizeAll; } else if (e.Button == MouseButtons.Right && _currentMode == ViewerMode.Dicom) { _isAdjustingWL = true; _wlMouseStart = e.Location; if (sender is PictureBox pb) pb.Cursor = Cursors.SizeNS; } }
        private void PictureBox_MouseMove(object? sender, MouseEventArgs e) { if (_isDragging) { _offset.X += e.X - _mouseStart.X; _offset.Y += e.Y - _mouseStart.Y; _mouseStart = e.Location; InvalidateViewers(); } else if (_isAdjustingWL && _currentMode == ViewerMode.Dicom && !_isUpdatingSliders) { _currentWW += (e.X - _wlMouseStart.X) * 3.0; _currentWC -= (e.Y - _wlMouseStart.Y) * 3.0; if (_currentWW < 1) _currentWW = 1; _wlMouseStart = e.Location; SyncSlidersToMouse(); } }
        private void PictureBox_MouseUp(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _isDragging = false; if (sender is PictureBox pb) pb.Cursor = Cursors.Default; } if (e.Button == MouseButtons.Right) { _isAdjustingWL = false; if (sender is PictureBox pb) pb.Cursor = Cursors.Default; } }

        private void SetStatus(string msg) { if (lblStatus != null) lblStatus.Text = " " + msg; }
        private Label MakeLabel(string txt) => new Label { Text = txt, ForeColor = SDColor.White, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new SDFont("Segoe UI", 8.5f) };
        private void StyleButton(Button b, string txt, SDColor clr) { b.Text = txt; b.BackColor = clr; b.ForeColor = SDColor.White; b.FlatStyle = FlatStyle.Flat; b.Dock = DockStyle.Fill; b.Margin = new Padding(2); }
        private void StyleNavButton(Button b, string txt) { b.Text = txt; b.BackColor = SDColor.FromArgb(40, 44, 58); b.ForeColor = SDColor.White; b.FlatStyle = FlatStyle.Flat; b.Dock = DockStyle.Fill; }
        private Panel CreateGroupPanel(string title, Control content) { var pnl = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 10), BackColor = SDColor.FromArgb(24, 28, 38) }; var lbl = new Label { Text = title, ForeColor = SDColor.FromArgb(130, 150, 180), Font = new SDFont("Segoe UI", 8.5f, System.Drawing.FontStyle.Bold), Dock = DockStyle.Top, Height = 28, TextAlign = ContentAlignment.BottomLeft, Padding = new Padding(8, 0, 0, 4) }; var contentPadder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) }; content.Dock = DockStyle.Fill; contentPadder.Controls.Add(content); pnl.Controls.Add(contentPadder); pnl.Controls.Add(lbl); return pnl; }
    }
}