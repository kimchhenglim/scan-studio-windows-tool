using System.Diagnostics;

namespace ScanStudio;

public sealed class MainForm : Form
{
    private sealed class ScannedPage
    {
        public string FilePath;
        public string Name; // null = not renamed ("Page N")
        public Image Thumbnail;
    }

    private static readonly int[] Resolutions = { 75, 100, 150, 200, 300, 600, 1200, 2400 };
    private const int DefaultDpi = 300;
    private static readonly (OutputFormat Format, string Label)[] Formats =
    {
        (OutputFormat.Pdf, "PDF document (*.pdf)"),
        (OutputFormat.Tiff, "TIFF image (*.tif)"),
        (OutputFormat.Jpeg, "JPEG image (*.jpg)"),
        (OutputFormat.Png, "PNG image (*.png)"),
        (OutputFormat.Bmp, "Bitmap image (*.bmp)"),
    };
    private static readonly (ColorMode Mode, string Label)[] ColorModes =
    {
        (ColorMode.Color, "Color"),
        (ColorMode.Grayscale, "Grayscale"),
        (ColorMode.BlackAndWhite, "Black & White"),
    };

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly List<ScannedPage> _pages = new();
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "ScanStudio", Guid.NewGuid().ToString("N"));

    private readonly ComboBox _cboScanner = DropDown();
    private readonly ComboBox _cboFormat = DropDown();
    private readonly ComboBox _cboColor = DropDown();
    private readonly ComboBox _cboDpi = DropDown();
    private readonly TextBox _txtFolder = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtFileName = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _chkMultiPage = new() { Text = "Multi-page: wait until I click Save", AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
    private readonly CheckBox _chkOpenFolder = new() { Text = "Open folder after saving", AutoSize = true };
    private readonly Button _btnRefresh = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _btnBrowse = new() { Text = "Browse…", AutoSize = true };
    // AutoSize + MinimumSize: the button grows with its font/DPI so the text is never clipped.
    private readonly Button _btnScan = new() { Text = "Scan", Dock = DockStyle.Fill, AutoSize = true, MinimumSize = new Size(0, 48), Padding = new Padding(4) };
    private readonly Button _btnSave = new() { Text = "Save", Dock = DockStyle.Fill, AutoSize = true, MinimumSize = new Size(0, 40), Padding = new Padding(4) };
    private readonly Button _btnClear = new() { Text = "Discard all pages", Dock = DockStyle.Fill, AutoSize = true, MinimumSize = new Size(0, 34), Padding = new Padding(4) };
    private readonly Label _lblHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 10, 3, 3), MaximumSize = new Size(320, 0) };

    private readonly ListView _lstPages = new()
    {
        Dock = DockStyle.Fill,
        View = View.LargeIcon,
        MultiSelect = false,
        HideSelection = false,
        BackColor = Color.FromArgb(245, 246, 248),
    };
    private readonly ImageList _thumbs = new() { ImageSize = new Size(120, 160), ColorDepth = ColorDepth.Depth24Bit };
    private readonly PageViewer _viewer = new() { Dock = DockStyle.Fill };
    private readonly ToolStripButton _tbRotateLeft = new("⟲ Rotate left");
    private readonly ToolStripButton _tbRotateRight = new("⟳ Rotate right");
    private readonly ToolStripButton _tbMoveUp = new("▲ Move up");
    private readonly ToolStripButton _tbMoveDown = new("▼ Move down");
    private readonly ToolStripButton _tbDelete = new("✕ Delete page");
    private readonly ToolStripButton _tbRename = new("✎ Rename") { ToolTipText = "Rename the selected page (F2, or double-click it)" };
    private bool _editingLabel; // a page name is being typed: leave keys to the edit box
    private readonly ToolStripButton _tbCopy = new("Copy");
    private readonly ToolStripButton _tbCut = new("Cut");
    private readonly ToolStripButton _tbPaste = new("Paste");

    // Preview toolbar: zoom + crop
    private readonly ToolStripButton _tbZoomOut = new("−") { ToolTipText = "Zoom out (Ctrl+−, or Ctrl+mouse wheel)" };
    private readonly ToolStripLabel _lblZoom = new("100%");
    private readonly ToolStripButton _tbZoomIn = new("+") { ToolTipText = "Zoom in (Ctrl++, or Ctrl+mouse wheel)" };
    private readonly ToolStripButton _tbFit = new("Fit") { ToolTipText = "Fit the page in the window (Ctrl+0)" };
    private readonly ToolStripButton _tbActual = new("100%") { ToolTipText = "Actual size (one scan pixel per screen pixel)" };
    private readonly ToolStripButton _tbCrop = new("✂ Crop") { ToolTipText = "Crop this page to one area" };
    private readonly ToolStripButton _tbMultiCrop = new("▦ Multi-crop") { ToolTipText = "Mark several areas and save each one as a separate file or page" };

    // Crop action bar (shown below the preview while cropping)
    private readonly ToolStrip _cropBar = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Bottom, Visible = false, Padding = new Padding(4, 4, 4, 4) };
    private readonly ToolStripLabel _lblCropHint = new();
    private readonly ToolStripButton _tbShapeRect = new("▭ Rectangle") { ToolTipText = "Straight boxes" };
    private readonly ToolStripButton _tbShapeQuad = new("◇ 4 corners")
    {
        ToolTipText = "Drag each corner separately onto the page's real corners (for crooked or angled pages).\nThe area is straightened into a flat rectangle when applied.",
    };
    private readonly ToolStripButton _tbCropApply = new("✔ Apply crop") { Alignment = ToolStripItemAlignment.Right };
    private readonly ToolStripButton _tbCropToFiles = new("Save as separate files") { Alignment = ToolStripItemAlignment.Right };
    private readonly ToolStripButton _tbCropToPages = new("Split into pages") { Alignment = ToolStripItemAlignment.Right };
    private readonly ToolStripButton _tbCropCancel = new("Cancel") { Alignment = ToolStripItemAlignment.Right };

    // Lets Paste use the exact page bytes (with DPI) when the clipboard still holds our own copy.
    private byte[] _copiedPage;
    private string _copiedName;
    private uint _copiedSequence;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 160 };
    private CancellationTokenSource _scanCancel;
    private bool _busy;
    private readonly ToolTip _tips = new();

    public MainForm()
    {
        SuspendLayout();
        // All pixel sizes in this file are written for 96 DPI (100%); WinForms scales them
        // to the monitor's DPI on ResumeLayout and again when moved to another monitor.
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        Text = "Scan Studio";
        Size = new Size(1150, 760);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10.5f);
        KeyPreview = true;

        BuildLayout();
        ResumeLayout(false);
        PerformLayout();
        LoadSettingsIntoControls();
        WireEvents();
        UpdateUi();
    }

    // ---------------------------------------------------------------- layout

    private static ComboBox DropDown() => new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };

    private void BuildLayout()
    {
        // Root: draggable splitter between settings (left) and pages (right)
        var root = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            Padding = new Padding(8),
            SplitterWidth = 6,
        };

        // Left: settings
        var settingsBox = new GroupBox { Text = "Scan settings", Dock = DockStyle.Fill, Padding = new Padding(10) };
        var s = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, AutoScroll = true };
        s.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        void AddLabel(string text) => s.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(3, 8, 3, 2) });

        AddLabel("Scanner");
        s.Controls.Add(Row(_cboScanner, _btnRefresh));
        AddLabel("File type");
        s.Controls.Add(_cboFormat);
        AddLabel("Color mode");
        s.Controls.Add(_cboColor);
        AddLabel("Resolution (DPI)");
        s.Controls.Add(_cboDpi);
        AddLabel("Save file to");
        s.Controls.Add(Row(_txtFolder, _btnBrowse));
        AddLabel("File name");
        s.Controls.Add(_txtFileName);
        s.Controls.Add(_chkMultiPage);
        s.Controls.Add(_chkOpenFolder);

        _btnScan.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        _btnScan.BackColor = Color.FromArgb(0, 103, 192);
        _btnScan.ForeColor = Color.White;
        _btnScan.FlatStyle = FlatStyle.Flat;
        _btnScan.FlatAppearance.BorderSize = 0;
        _btnScan.Margin = new Padding(3, 16, 3, 6);
        _btnSave.Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold);
        s.Controls.Add(_btnScan);
        s.Controls.Add(_btnSave);
        s.Controls.Add(_btnClear);
        s.Controls.Add(_lblHint);
        for (int i = 0; i < s.Controls.Count; i++) s.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        s.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        settingsBox.Controls.Add(s);
        root.Panel1.Controls.Add(settingsBox);
        // Let the hint text wrap to whatever width the panel is dragged to.
        s.SizeChanged += (_, _) => _lblHint.MaximumSize = new Size(Math.Max(100, s.ClientSize.Width - 30), 0);

        // Right: pages + preview
        var pagesBox = new GroupBox { Text = "Scanned pages", Dock = DockStyle.Fill, Padding = new Padding(6) };
        // ToolStrips use the small system menu font by default; match the form font instead.
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, Font = Font, Padding = new Padding(2, 4, 2, 4) };
        _lstPages.Font = Font;
        toolbar.Items.AddRange(new ToolStripItem[]
        {
            _tbRotateLeft, _tbRotateRight, new ToolStripSeparator(), _tbMoveUp, _tbMoveDown, new ToolStripSeparator(),
            _tbRename, _tbCopy, _tbCut, _tbPaste, new ToolStripSeparator(), _tbDelete,
        });

        _lstPages.LargeImageList = _thumbs;
        _lstPages.ContextMenuStrip = BuildPageMenu();
        _lstPages.LabelEdit = true;
        _lstPages.ShowItemToolTips = true;
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(_lstPages);

        var viewBar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, Font = Font, Padding = new Padding(2, 2, 2, 2) };
        viewBar.Items.AddRange(new ToolStripItem[]
        {
            _tbZoomOut, _lblZoom, _tbZoomIn, _tbFit, _tbActual, new ToolStripSeparator(), _tbCrop, _tbMultiCrop,
        });
        _tbZoomOut.Font = _tbZoomIn.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);

        _cropBar.Font = Font;
        _cropBar.BackColor = Color.FromArgb(232, 240, 252);
        _tbCropApply.Font = new Font(Font, FontStyle.Bold);
        // Right-aligned items are laid out from the right edge inward.
        _cropBar.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripLabel("Shape:"), _tbShapeRect, _tbShapeQuad, new ToolStripSeparator(), _lblCropHint,
            _tbCropCancel, _tbCropToPages, _tbCropToFiles, _tbCropApply,
        });

        split.Panel2.Controls.Add(_viewer);
        split.Panel2.Controls.Add(_cropBar);
        split.Panel2.Controls.Add(viewBar);
        pagesBox.Controls.Add(split);
        pagesBox.Controls.Add(toolbar);
        root.Panel2.Controls.Add(pagesBox);
        Load += (_, _) =>
        {
            // Values set here run after auto-scaling, so convert from 96-DPI units ourselves.
            split.SplitterDistance = LogicalToDeviceUnits(330);
            root.Panel1MinSize = LogicalToDeviceUnits(260);
            root.Panel2MinSize = LogicalToDeviceUnits(400);
            int max = root.Width - root.Panel2MinSize - root.SplitterWidth;
            int wanted = LogicalToDeviceUnits(_settings.SettingsPanelWidth);
            root.SplitterDistance = Math.Clamp(wanted, root.Panel1MinSize, Math.Max(root.Panel1MinSize, max));
            // Persist in 96-DPI units so the width means the same on every monitor.
            root.SplitterMoved += (_, _) => _settings.SettingsPanelWidth = root.SplitterDistance * 96 / DeviceDpi;
        };

        var statusStrip = new StatusStrip { Font = Font };
        statusStrip.Items.Add(_status);
        statusStrip.Items.Add(_progress);

        Controls.Add(root);
        Controls.Add(statusStrip);
    }

    private static TableLayoutPanel Row(Control fill, Control fixedRight)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fill.Margin = new Padding(3, 4, 3, 3);
        row.Controls.Add(fill, 0, 0);
        row.Controls.Add(fixedRight, 1, 0);
        return row;
    }

    private void LoadSettingsIntoControls()
    {
        foreach (var f in Formats) _cboFormat.Items.Add(f.Label);
        foreach (var c in ColorModes) _cboColor.Items.Add(c.Label);
        foreach (var r in Resolutions) _cboDpi.Items.Add(r == DefaultDpi ? $"{r} [Default]" : r.ToString());

        _cboFormat.SelectedIndex = Math.Max(0, Array.FindIndex(Formats, f => f.Format == _settings.Format));
        _cboColor.SelectedIndex = Math.Max(0, Array.FindIndex(ColorModes, c => c.Mode == _settings.ColorMode));
        int dpiIdx = Array.IndexOf(Resolutions, _settings.Dpi);
        _cboDpi.SelectedIndex = dpiIdx >= 0 ? dpiIdx : Array.IndexOf(Resolutions, DefaultDpi);
        _txtFolder.Text = _settings.SaveFolder;
        _txtFileName.Text = DefaultFileName();
        _chkMultiPage.Checked = _settings.MultiPage;
        _chkOpenFolder.Checked = _settings.OpenFolderAfterSave;
        _viewer.Shape = _settings.CropShape;

        _tips.SetToolTip(_chkMultiPage, "When checked, each scan is added as a page. Scan as many pages as you need, then click Save.\nWhen unchecked, every scan is saved immediately.");
        _tips.SetToolTip(_btnScan, "Scan a page (Ctrl+Enter)");
        _tips.SetToolTip(_btnSave, "Save all scanned pages (Ctrl+S)");
        _tbCopy.ToolTipText = "Copy the selected page (Ctrl+C)";
        _tbCut.ToolTipText = "Cut the selected page (Ctrl+X)";
        _tbPaste.ToolTipText = "Paste a page after the selected one (Ctrl+V). Also accepts images copied from other apps or image files copied in Explorer.";
    }

    private ContextMenuStrip BuildPageMenu()
    {
        var menu = new ContextMenuStrip { Font = Font };
        var copy = new ToolStripMenuItem("Copy", null, (_, _) => CopySelected()) { ShortcutKeyDisplayString = "Ctrl+C" };
        var cut = new ToolStripMenuItem("Cut", null, (_, _) => CutSelected()) { ShortcutKeyDisplayString = "Ctrl+X" };
        var paste = new ToolStripMenuItem("Paste", null, (_, _) => PastePages()) { ShortcutKeyDisplayString = "Ctrl+V" };
        var crop = new ToolStripMenuItem("Crop…", null, (_, _) => StartCrop(CropMode.Single));
        var multi = new ToolStripMenuItem("Multi-crop…", null, (_, _) => StartCrop(CropMode.Multi));
        var delete = new ToolStripMenuItem("Delete", null, (_, _) => DeleteSelected()) { ShortcutKeyDisplayString = "Del" };
        var rename = new ToolStripMenuItem("Rename", null, (_, _) => RenameSelected()) { ShortcutKeyDisplayString = "F2" };
        menu.Items.AddRange(new ToolStripItem[] { rename, new ToolStripSeparator(), copy, cut, paste, new ToolStripSeparator(), crop, multi, new ToolStripSeparator(), delete });
        menu.Opening += (_, _) =>
        {
            bool sel = SelectedIndex >= 0;
            rename.Enabled = copy.Enabled = cut.Enabled = crop.Enabled = multi.Enabled = delete.Enabled = sel;
            paste.Enabled = ClipboardHasPage();
        };
        return menu;
    }

    private void WireEvents()
    {
        Shown += (_, _) => RefreshScanners();
        _btnRefresh.Click += (_, _) => RefreshScanners();
        _btnBrowse.Click += (_, _) => BrowseFolder();
        _btnScan.Click += (_, _) => Scan();
        _btnSave.Click += (_, _) => SavePages();
        _btnClear.Click += (_, _) => DiscardAll();
        _chkMultiPage.CheckedChanged += (_, _) => UpdateUi();
        _cboFormat.SelectedIndexChanged += (_, _) => UpdateUi();
        _cboDpi.SelectedIndexChanged += (_, _) => UpdateUi();
        _cboColor.SelectedIndexChanged += (_, _) => UpdateUi();
        _lstPages.SelectedIndexChanged += (_, _) => { ShowPreview(); UpdateUi(); };
        _tbRotateLeft.Click += (_, _) => RotateSelected(RotateFlipType.Rotate270FlipNone);
        _tbRotateRight.Click += (_, _) => RotateSelected(RotateFlipType.Rotate90FlipNone);
        _tbMoveUp.Click += (_, _) => MoveSelected(-1);
        _tbMoveDown.Click += (_, _) => MoveSelected(+1);
        _tbDelete.Click += (_, _) => DeleteSelected();
        _lstPages.KeyDown += (_, e) =>
        {
            if (_editingLabel) return;
            if (e.KeyCode == Keys.Delete) DeleteSelected();
            else if (e.KeyCode == Keys.F2) { RenameSelected(); e.Handled = true; }
        };
        _lstPages.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) RenameSelected(); };
        _lstPages.BeforeLabelEdit += (_, _) => _editingLabel = true;
        _lstPages.AfterLabelEdit += (s, e) => { _editingLabel = false; OnAfterPageLabelEdit(s, e); };
        _tbRename.Click += (_, _) => RenameSelected();
        _tbCopy.Click += (_, _) => CopySelected();
        _tbCut.Click += (_, _) => CutSelected();
        _tbPaste.Click += (_, _) => PastePages();

        _tbZoomIn.Click += (_, _) => _viewer.ZoomIn();
        _tbZoomOut.Click += (_, _) => _viewer.ZoomOut();
        _tbFit.Click += (_, _) => _viewer.ZoomToFit();
        _tbActual.Click += (_, _) => _viewer.ZoomActualSize();
        _viewer.ZoomChanged += (_, _) => { _lblZoom.Text = $"{_viewer.Zoom * 100:0}%"; _tbFit.Checked = _viewer.FitToWindow; };
        _viewer.RegionsChanged += (_, _) => UpdateCropBar();
        _tbCrop.Click += (_, _) => StartCrop(_viewer.CropMode == CropMode.Single ? CropMode.None : CropMode.Single);
        _tbMultiCrop.Click += (_, _) => StartCrop(_viewer.CropMode == CropMode.Multi ? CropMode.None : CropMode.Multi);
        _tbCropApply.Click += (_, _) => ApplySingleCrop();
        _tbCropToFiles.Click += (_, _) => SaveRegionsAsFiles();
        _tbCropToPages.Click += (_, _) => SplitRegionsIntoPages();
        _tbCropCancel.Click += (_, _) => StartCrop(CropMode.None);
        _tbShapeRect.Click += (_, _) => { _viewer.Shape = CropShape.Rectangle; _viewer.Focus(); };
        _tbShapeQuad.Click += (_, _) => { _viewer.Shape = CropShape.FourCorners; _viewer.Focus(); };

        KeyDown += OnKeyDown;
        FormClosing += OnFormClosing;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_editingLabel) return; // typing a page name
        if (e.Control && e.KeyCode == Keys.S && _btnSave.Enabled) { SavePages(); e.SuppressKeyPress = true; return; }
        if (e.Control && e.KeyCode == Keys.Enter && _btnScan.Enabled) { Scan(); e.SuppressKeyPress = true; return; }

        // Leave text editing keys (Ctrl+C/V, Enter, Esc…) to text boxes and drop-downs.
        if (_editingLabel || FocusedControl() is TextBoxBase or ComboBox) return;

        bool handled = true;
        if (e.Control && e.KeyCode == Keys.C) CopySelected();
        else if (e.Control && e.KeyCode == Keys.X) CutSelected();
        else if (e.Control && e.KeyCode == Keys.V) PastePages();
        else if (e.Control && e.KeyCode is Keys.Oemplus or Keys.Add) _viewer.ZoomIn();
        else if (e.Control && e.KeyCode is Keys.OemMinus or Keys.Subtract) _viewer.ZoomOut();
        else if (e.Control && e.KeyCode is Keys.D0 or Keys.NumPad0) _viewer.ZoomToFit();
        else if (e.KeyCode == Keys.Escape && _viewer.CropMode != CropMode.None) StartCrop(CropMode.None);
        else if (e.KeyCode == Keys.Enter && e.Modifiers == Keys.None && _viewer.CropMode == CropMode.Single && _tbCropApply.Enabled) ApplySingleCrop();
        else handled = false;
        if (handled) e.SuppressKeyPress = true;
    }

    private Control FocusedControl()
    {
        Control c = ActiveControl;
        while (c is ContainerControl cc && cc.ActiveControl != null) c = cc.ActiveControl;
        return c;
    }

    // ---------------------------------------------------------------- state helpers

    private OutputFormat SelectedFormat => Formats[Math.Max(0, _cboFormat.SelectedIndex)].Format;
    private ColorMode SelectedColor => ColorModes[Math.Max(0, _cboColor.SelectedIndex)].Mode;
    private int SelectedDpi => Resolutions[Math.Max(0, _cboDpi.SelectedIndex)];
    private ScannerInfo SelectedScanner => _cboScanner.SelectedItem as ScannerInfo;
    private int SelectedIndex => _lstPages.SelectedIndices.Count > 0 ? _lstPages.SelectedIndices[0] : -1;

    private static string DefaultFileName() => $"Scan_{DateTime.Now:yyyy-MM-dd_HHmmss}";

    private void SetStatus(string text) => _status.Text = text;

    private void UpdateUi()
    {
        if (_busy) return; // SetBusy(false) refreshes everything when the operation ends.
        bool hasPages = _pages.Count > 0;
        bool hasSel = SelectedIndex >= 0;
        _btnScan.Enabled = SelectedScanner != null;
        _btnSave.Enabled = hasPages;
        _btnClear.Enabled = hasPages;
        _btnSave.Text = hasPages ? $"Save ({_pages.Count} page{(_pages.Count == 1 ? "" : "s")})" : "Save";
        _btnScan.Text = _chkMultiPage.Checked && hasPages ? $"Scan page {_pages.Count + 1}" : "Scan";
        _tbRotateLeft.Enabled = _tbRotateRight.Enabled = _tbDelete.Enabled = hasSel;
        _tbMoveUp.Enabled = hasSel && SelectedIndex > 0;
        _tbMoveDown.Enabled = hasSel && SelectedIndex < _pages.Count - 1;
        _tbCopy.Enabled = _tbCut.Enabled = _tbRename.Enabled = hasSel;
        bool hasImage = _viewer.Image != null;
        _tbZoomIn.Enabled = _tbZoomOut.Enabled = _tbFit.Enabled = _tbActual.Enabled = hasImage;
        _tbCrop.Enabled = _tbMultiCrop.Enabled = hasImage;
        _lblZoom.Visible = hasImage;

        string hint = _chkMultiPage.Checked
            ? "Place a page on the scanner and click Scan. Repeat for every page, then click Save to create the file."
            : "Each scan is saved to a file immediately.";
        if (!DocumentWriter.SupportsMultiPage(SelectedFormat) && _chkMultiPage.Checked)
            hint += "\n\nNote: JPEG/PNG/BMP hold one page per file, so multiple pages are saved as numbered files (_001, _002…). Choose PDF or TIFF for a single multi-page file.";
        if (SelectedDpi >= 1200)
        {
            // Full 8.5" x 11.7" page, uncompressed.
            double pixels = 8.5 * SelectedDpi * 11.7 * SelectedDpi;
            int bytesPerPixel = SelectedColor switch { ColorMode.Color => 3, ColorMode.Grayscale => 1, _ => 0 };
            double mb = SelectedColor == ColorMode.BlackAndWhite ? pixels / 8 / 1e6 : pixels * bytesPerPixel / 1e6;
            hint += $"\n\n⚠ {SelectedDpi} DPI is very high: a full page is about {mb:N0} MB and can take several minutes to scan. " +
                    "Use it for photos or small originals. 300 DPI is plenty for documents."
                    + (mb > 1000 ? " A page this large may be too big to preview or save; try Grayscale or a lower DPI if it fails." : "");
        }
        _lblHint.Text = hint;
    }

    private void SaveSettings()
    {
        _settings.ScannerId = SelectedScanner?.DeviceId ?? _settings.ScannerId;
        _settings.Format = SelectedFormat;
        _settings.ColorMode = SelectedColor;
        _settings.Dpi = SelectedDpi;
        _settings.SaveFolder = _txtFolder.Text.Trim();
        _settings.MultiPage = _chkMultiPage.Checked;
        _settings.OpenFolderAfterSave = _chkOpenFolder.Checked;
        _settings.CropShape = _viewer.Shape;
        _settings.Save();
    }

    // ---------------------------------------------------------------- actions

    private async void RefreshScanners()
    {
        string previous = SelectedScanner?.DeviceId ?? _settings.ScannerId;
        _cboScanner.Items.Clear();
        _btnRefresh.Enabled = false;
        SetStatus("Looking for scanners…");
        try
        {
            UseWaitCursor = true;
            var scanners = await ScannerService.GetScannersAsync();
            foreach (var sc in scanners) _cboScanner.Items.Add(sc);
            if (scanners.Count > 0)
            {
                int idx = scanners.FindIndex(x => x.DeviceId == previous);
                _cboScanner.SelectedIndex = idx >= 0 ? idx : 0;
                SetStatus($"{scanners.Count} scanner(s) found. Ready.");
            }
            else
            {
                SetStatus("No scanner found. Connect and turn on your scanner, then click Refresh.");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Could not list scanners: " + ScannerService.DescribeError(ex));
        }
        finally
        {
            UseWaitCursor = false;
            _btnRefresh.Enabled = true;
            UpdateUi();
        }
    }

    private void BrowseFolder()
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Choose where scanned files are saved",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        if (Directory.Exists(_txtFolder.Text)) dlg.SelectedPath = _txtFolder.Text;
        if (dlg.ShowDialog(this) == DialogResult.OK) _txtFolder.Text = dlg.SelectedPath;
    }

    private async void Scan()
    {
        // While a scan runs, the Scan button acts as "Cancel scan".
        if (_scanCancel != null)
        {
            _scanCancel.Cancel();
            _btnScan.Enabled = false;
            SetStatus("Cancelling scan…");
            return;
        }

        var scanner = SelectedScanner;
        if (scanner == null) return;

        Directory.CreateDirectory(_tempDir);
        string pageBase = Path.Combine(_tempDir, $"page_{Guid.NewGuid():N}");

        _scanCancel = new CancellationTokenSource();
        SetBusy(true);
        _btnScan.Enabled = true;
        _btnScan.Text = "Cancel scan";
        _progress.Visible = true;
        SetStatus($"Scanning page {_pages.Count + 1} on {scanner.Name}…");
        try
        {
            var (pagePath, actualDpi) = await ScannerService.ScanPageAsync(scanner.DeviceId, SelectedColor, SelectedDpi,
                _tempDir, pageBase, _scanCancel.Token,
                secs => SetStatus($"Waiting for the scanner to get ready for page {_pages.Count + 1}… ({secs}s) Click Cancel scan to stop."));

            AddPage(pagePath);
            string dpiNote = actualDpi != SelectedDpi
                ? $" (this scanner doesn't support {SelectedDpi} DPI, so it used {actualDpi} DPI)"
                : "";

            if (_chkMultiPage.Checked)
            {
                SetStatus($"Page {_pages.Count} scanned{dpiNote}. Place the next page and click Scan, or click Save when you're done.");
            }
            else
            {
                SavePages();
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Scan cancelled.");
        }
        catch (Exception ex)
        {
            string msg = ScannerService.DescribeError(ex);
            SetStatus("Scan failed: " + msg);
            MessageBox.Show(this, msg, "Scan failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _scanCancel?.Dispose();
            _scanCancel = null;
            _progress.Visible = false;
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        foreach (Control c in new Control[] { _btnScan, _btnSave, _btnClear, _btnRefresh, _cboScanner, _cboFormat, _cboColor, _cboDpi, _chkMultiPage })
            c.Enabled = !busy;
        if (!busy) UpdateUi();
    }

    /// <summary>Validates the Save folder / File name boxes. Returns false (after telling the user) if they're unusable.</summary>
    private bool TryGetSaveTarget(out string folder, out string name)
    {
        folder = _txtFolder.Text.Trim();
        name = _txtFileName.Text.Trim();
        if (string.IsNullOrEmpty(folder))
        {
            MessageBox.Show(this, "Choose a folder to save to.", "Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "Enter a valid file name (it can't contain \\ / : * ? \" < > |).", "Save", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _txtFileName.Focus();
            return false;
        }
        // Strip an extension the user may have typed; the File type setting decides it.
        string ext = Path.GetExtension(name);
        if (Formats.Any(f => DocumentWriter.Extension(f.Format).Equals(ext, StringComparison.OrdinalIgnoreCase)) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
            name = Path.GetFileNameWithoutExtension(name);
        return true;
    }

    private void SavePages()
    {
        if (_pages.Count == 0) return;
        if (!TryGetSaveTarget(out string folder, out string name)) return;

        SetBusy(true);
        SetStatus("Saving…");
        try
        {
            var files = DocumentWriter.Save(_pages.Select(p => p.FilePath).ToList(), SelectedFormat, folder, name, SelectedDpi,
                _pages.Select(p => p.Name).ToList());
            int count = _pages.Count;
            ClearPages();
            _txtFileName.Text = DefaultFileName();
            SaveSettings();

            SetStatus(files.Count == 1
                ? $"Saved {count} page{(count == 1 ? "" : "s")} to {files[0]}"
                : $"Saved {files.Count} files to {folder}");

            if (_chkOpenFolder.Checked)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{files[0]}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Save failed: " + ex.Message);
            MessageBox.Show(this, "Could not save the file:\n\n" + ex.Message, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void DiscardAll()
    {
        if (_pages.Count == 0) return;
        if (MessageBox.Show(this, $"Discard all {_pages.Count} scanned page(s)? This cannot be undone.", "Discard pages",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        ClearPages();
        SetStatus("All pages discarded.");
    }

    // ---------------------------------------------------------------- page list

    private void AddPage(string path) => InsertPages(_pages.Count, new[] { path });

    /// <summary>
    /// Inserts pages at <paramref name="index"/> (with optional names; null = "Page N") and selects the last one
    /// (or the first, if <paramref name="selectFirst"/>).
    /// </summary>
    private void InsertPages(int index, IReadOnlyList<string> paths, bool selectFirst = false, IReadOnlyList<string> names = null)
    {
        index = Math.Clamp(index, 0, _pages.Count);
        for (int k = 0; k < paths.Count; k++)
            _pages.Insert(index + k, new ScannedPage
            {
                FilePath = paths[k],
                Thumbnail = MakeThumbnail(paths[k]),
                Name = names != null && k < names.Count ? names[k] : null,
            });
        RebuildList(selectFirst ? index : index + paths.Count - 1);
    }

    private string PageLabel(int i) => _pages[i].Name ?? $"Page {i + 1}";

    // ---------------------------------------------------------------- rename

    private void RenameSelected()
    {
        int i = SelectedIndex;
        if (i < 0) return;
        _lstPages.Focus();
        _lstPages.Items[i].BeginEdit();
    }

    private void OnAfterPageLabelEdit(object sender, LabelEditEventArgs e)
    {
        if (e.Label == null) return; // edit cancelled (Esc) or unchanged
        e.CancelEdit = true;         // we set the label ourselves after validating
        int i = e.Item;
        if (i < 0 || i >= _pages.Count) return;

        string name = e.Label.Trim();
        if (name.Length == 0 || name == $"Page {i + 1}")
        {
            _pages[i].Name = null; // back to the automatic "Page N"
        }
        else if (!DocumentWriter.IsValidFileName(name))
        {
            // The name may become a file name, so it must be one.
            MessageBox.Show(this, "A page name can't contain any of these characters:\n\n\\ / : * ? \" < > |", "Rename page",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            BeginInvoke(() => { if (i < _lstPages.Items.Count) _lstPages.Items[i].BeginEdit(); });
            return;
        }
        else
        {
            _pages[i].Name = name.Length > 120 ? name[..120].Trim() : name;
        }
        _lstPages.Items[i].Text = PageLabel(i);
        _lstPages.Items[i].ToolTipText = _pages[i].Name != null ? $"Page {i + 1}: {_pages[i].Name}" : null;

        string effect = SelectedFormat switch
        {
            OutputFormat.Pdf => " It will be a bookmark in the PDF.",
            OutputFormat.Tiff => "",
            _ => $" It will be saved as \"{_pages[i].Name}{DocumentWriter.Extension(SelectedFormat)}\".",
        };
        SetStatus(_pages[i].Name == null ? $"Page {i + 1} name cleared." : $"Page {i + 1} renamed to \"{_pages[i].Name}\".{effect}");
    }

    private string NewTempPagePath()
    {
        Directory.CreateDirectory(_tempDir);
        return Path.Combine(_tempDir, $"page_{Guid.NewGuid():N}.bmp");
    }

    /// <summary>Replaces the page at <paramref name="index"/> with the given page files (used by crop/split).</summary>
    private void ReplacePage(int index, IReadOnlyList<string> newPaths)
    {
        var old = _pages[index];
        _pages.RemoveAt(index);
        old.Thumbnail?.Dispose();
        if (!newPaths.Contains(old.FilePath)) TryDelete(old.FilePath);
        // A renamed page passes its name on: "Invoice" → "Invoice 1", "Invoice 2"…
        var names = old.Name == null ? null
            : newPaths.Count == 1 ? new[] { old.Name }
            : Enumerable.Range(1, newPaths.Count).Select(k => $"{old.Name} {k}").ToArray();
        InsertPages(index, newPaths, selectFirst: true, names: names);
    }

    // ---------------------------------------------------------------- crop

    private void StartCrop(CropMode mode)
    {
        if (mode != CropMode.None && _viewer.Image == null) return;
        _viewer.CropMode = mode;
        if (mode != CropMode.None) _viewer.Focus();
        UpdateCropBar();
    }

    private void UpdateCropBar()
    {
        var mode = _viewer.CropMode;
        int n = _viewer.RegionCount;
        bool quad = _viewer.Shape == CropShape.FourCorners;
        _tbShapeRect.Checked = !quad;
        _tbShapeQuad.Checked = quad;
        _tbCrop.Checked = mode == CropMode.Single;
        _tbMultiCrop.Checked = mode == CropMode.Multi;
        _cropBar.Visible = mode != CropMode.None;

        _tbCropApply.Visible = mode == CropMode.Single;
        _tbCropApply.Enabled = n == 1;
        _tbCropToFiles.Visible = _tbCropToPages.Visible = mode == CropMode.Multi;
        _tbCropToFiles.Enabled = _tbCropToPages.Enabled = n > 0;
        _tbCropToFiles.Text = n > 1 ? $"Save as {n} separate files" : "Save as separate file";
        _tbCropToPages.Text = n > 1 ? $"Split into {n} pages" : "Split into pages";
        _tbCropToFiles.ToolTipText = $"Save each box as its own {DocumentWriter.Extension(SelectedFormat)} file in the save folder, " +
                                     "named after File name with _01, _02… The page itself is not changed.";
        _tbCropToPages.ToolTipText = "Replace this page with one page per box, in the numbered order.";

        _lblCropHint.Text = mode switch
        {
            CropMode.Single when n == 0 => "Drag a box around the part to keep.",
            CropMode.Single when quad => "Drag each corner onto the page's corner.  Enter = apply, Esc = cancel.",
            CropMode.Single => "Drag the edges to adjust.  Enter = apply, Esc = cancel.",
            CropMode.Multi when n == 0 => "Drag a box around each item.",
            CropMode.Multi when quad => $"{n} area{(n == 1 ? "" : "s")}.  Drag the corners into place; right-click removes one.",
            CropMode.Multi => $"{n} area{(n == 1 ? "" : "s")}.  Drag to add more; right-click or Del removes one.",
            _ => "",
        };
        _lblCropHint.ToolTipText = quad
            ? "Each corner moves on its own. The round handles on the sides move a whole side. Drag inside to move the area.\n" +
              "Zoom in (Ctrl+wheel) to place corners precisely. The area is straightened into a flat rectangle."
            : "";
    }

    private void ApplySingleCrop()
    {
        int i = SelectedIndex;
        if (i < 0 || _viewer.CropMode != CropMode.Single || _viewer.RegionCount != 1) return;
        var area = _viewer.Regions[0];
        try
        {
            var page = _pages[i];
            bool straightened = !DocumentWriter.IsAxisRectangle(area);
            ReleasePreview(); // the viewer shows this file's image; crop rewrites it
            if (straightened) UseWaitCursor = true;
            var size = DocumentWriter.CropToFile(page.FilePath, area, page.FilePath);
            page.Thumbnail?.Dispose();
            page.Thumbnail = MakeThumbnail(page.FilePath);
            RebuildList(i);
            SetStatus($"Page {i + 1} {(straightened ? "cropped and straightened" : "cropped")} to {size.Width} × {size.Height} pixels.");
        }
        catch (Exception ex)
        {
            UseWaitCursor = false;
            ShowPreview();
            MessageBox.Show(this, "Could not crop the page:\n\n" + ex.Message, "Crop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            UseWaitCursor = false;
        }
    }

    /// <summary>Crops every marked area to its own temp page file, in numbered order.</summary>
    private List<string> CropRegionsToTemp(int pageIndex)
    {
        var paths = new List<string>();
        try
        {
            foreach (var r in _viewer.Regions)
            {
                string p = NewTempPagePath();
                DocumentWriter.CropToFile(_pages[pageIndex].FilePath, r, p);
                paths.Add(p);
            }
            return paths;
        }
        catch
        {
            foreach (var p in paths) TryDelete(p);
            throw;
        }
    }

    private void SaveRegionsAsFiles()
    {
        int i = SelectedIndex;
        if (i < 0 || _viewer.RegionCount == 0 || _busy) return;
        if (!TryGetSaveTarget(out string folder, out string name)) return;
        if (_pages[i].Name != null) name = _pages[i].Name; // renamed page: "Invoice_01", "Invoice_02"…

        List<string> crops = null;
        var written = new List<string>();
        SetBusy(true);
        SetStatus("Saving cropped areas…");
        try
        {
            crops = CropRegionsToTemp(i);
            for (int k = 0; k < crops.Count; k++)
                written.AddRange(DocumentWriter.Save(new[] { crops[k] }, SelectedFormat, folder, $"{name}_{k + 1:D2}", SelectedDpi));
            SaveSettings();
            StartCrop(CropMode.None);
            SetStatus($"Saved {written.Count} file{(written.Count == 1 ? "" : "s")} to {folder}");
            if (_chkOpenFolder.Checked && written.Count > 0)
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{written[0]}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            string done = written.Count > 0 ? $"\n\n{written.Count} file(s) were saved before the error." : "";
            SetStatus("Save failed: " + ex.Message);
            MessageBox.Show(this, "Could not save the cropped areas:\n\n" + ex.Message + done, "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (crops != null) foreach (var p in crops) TryDelete(p);
            SetBusy(false);
        }
    }

    private void SplitRegionsIntoPages()
    {
        int i = SelectedIndex;
        if (i < 0 || _viewer.RegionCount == 0) return;
        try
        {
            var crops = CropRegionsToTemp(i);
            ReleasePreview();
            ReplacePage(i, crops);
            SetStatus($"Page {i + 1} split into {crops.Count} page{(crops.Count == 1 ? "" : "s")}. {_pages.Count} page(s) in total.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not split the page:\n\n" + ex.Message, "Multi-crop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------------------------------------------------------------- clipboard

    private void CopySelected()
    {
        int i = SelectedIndex;
        if (i < 0) return;
        try
        {
            byte[] bytes = File.ReadAllBytes(_pages[i].FilePath);
            using (var img = DocumentWriter.LoadImage(_pages[i].FilePath))
            using (var bmp = new Bitmap(img)) // 32-bit copy: other apps don't all accept 1-bit/indexed images
                Clipboard.SetImage(bmp);
            _copiedPage = bytes;
            _copiedName = _pages[i].Name;
            _copiedSequence = GetClipboardSequenceNumber();
            SetStatus($"{PageLabel(i)} copied. Select a page and press Ctrl+V to paste it after that page.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not copy the page:\n\n" + ex.Message, "Copy", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CutSelected()
    {
        int i = SelectedIndex;
        if (i < 0) return;
        string label = PageLabel(i);
        CopySelected();
        if (_copiedPage == null || _copiedSequence != GetClipboardSequenceNumber()) return; // copy failed
        DeleteSelected();
        SetStatus($"{label} cut. Select a page and press Ctrl+V to paste it after that page.");
    }

    private static readonly string[] ImageExtensions = { ".bmp", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".gif" };

    private bool ClipboardHasPage()
    {
        try
        {
            return Clipboard.ContainsImage() ||
                   (Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().Any(IsImageFile));
        }
        catch { return false; }
    }

    private static bool IsImageFile(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Pastes after the selected page (or at the end): our own copied page, an image, or image files from Explorer.</summary>
    private void PastePages()
    {
        var newPaths = new List<string>();
        var newNames = new List<string>();
        try
        {
            if (_copiedPage != null && _copiedSequence == GetClipboardSequenceNumber())
            {
                // Still our copy: use the original file bytes so DPI and B&W format are kept exactly.
                string p = NewTempPagePath();
                File.WriteAllBytes(p, _copiedPage);
                newPaths.Add(p);
                newNames.Add(_copiedName);
            }
            else if (Clipboard.ContainsFileDropList())
            {
                foreach (string file in Clipboard.GetFileDropList())
                {
                    if (!IsImageFile(file)) continue;
                    using var img = DocumentWriter.LoadImage(file); // multi-page TIFF/GIF: first page only
                    string p = NewTempPagePath();
                    img.Save(p, System.Drawing.Imaging.ImageFormat.Bmp);
                    newPaths.Add(p);
                    newNames.Add(Path.GetFileNameWithoutExtension(file)); // pasted files keep their own name
                }
            }
            else if (Clipboard.ContainsImage())
            {
                using var img = Clipboard.GetImage();
                string p = NewTempPagePath();
                img.Save(p, System.Drawing.Imaging.ImageFormat.Bmp);
                newPaths.Add(p);
                newNames.Add(null);
            }

            if (newPaths.Count == 0)
            {
                SetStatus("Nothing to paste: copy a page or an image first.");
                return;
            }
            int at = SelectedIndex >= 0 ? SelectedIndex + 1 : _pages.Count;
            InsertPages(at, newPaths, names: newNames);
            SetStatus(newPaths.Count == 1
                ? $"Pasted as page {at + 1}. {_pages.Count} page(s) in total."
                : $"Pasted {newPaths.Count} pages after page {at}. {_pages.Count} page(s) in total.");
        }
        catch (Exception ex)
        {
            foreach (var p in newPaths) TryDelete(p);
            MessageBox.Show(this, "Could not paste:\n\n" + ex.Message, "Paste", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ClearPages()
    {
        foreach (var p in _pages)
        {
            p.Thumbnail?.Dispose();
            TryDelete(p.FilePath);
        }
        _pages.Clear();
        RebuildList(-1);
    }

    private void RebuildList(int selectIndex)
    {
        _lstPages.BeginUpdate();
        _lstPages.Items.Clear();
        _thumbs.Images.Clear();
        for (int i = 0; i < _pages.Count; i++)
        {
            _thumbs.Images.Add(_pages[i].Thumbnail);
            _lstPages.Items.Add(new ListViewItem(PageLabel(i), i) { ToolTipText = _pages[i].Name != null ? $"Page {i + 1}: {_pages[i].Name}" : null });
        }
        _lstPages.EndUpdate();

        if (selectIndex >= 0 && selectIndex < _pages.Count)
        {
            _lstPages.Items[selectIndex].Selected = true;
            _lstPages.EnsureVisible(selectIndex);
        }
        ShowPreview();
        UpdateUi();
    }

    private void ShowPreview()
    {
        ReleasePreview();
        int i = SelectedIndex;
        if (i >= 0)
        {
            try { _viewer.Image = DocumentWriter.LoadImage(_pages[i].FilePath); }
            catch { /* ignore preview failures */ }
        }
        UpdateUi();
    }

    /// <summary>Clears the preview (ending any crop in progress) and frees its image.</summary>
    private void ReleasePreview()
    {
        StartCrop(CropMode.None);
        var old = _viewer.Image;
        _viewer.Image = null;
        old?.Dispose();
    }

    private void RotateSelected(RotateFlipType rotation)
    {
        int i = SelectedIndex;
        if (i < 0) return;
        try
        {
            var page = _pages[i];
            using (var img = DocumentWriter.LoadImage(page.FilePath))
            {
                img.RotateFlip(rotation);
                img.Save(page.FilePath, System.Drawing.Imaging.ImageFormat.Bmp);
            }
            page.Thumbnail.Dispose();
            page.Thumbnail = MakeThumbnail(page.FilePath);
            RebuildList(i);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Could not rotate the page:\n\n" + ex.Message, "Rotate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void MoveSelected(int delta)
    {
        int i = SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _pages.Count) return;
        (_pages[i], _pages[j]) = (_pages[j], _pages[i]);
        RebuildList(j);
    }

    private void DeleteSelected()
    {
        int i = SelectedIndex;
        if (i < 0) return;
        var page = _pages[i];
        _pages.RemoveAt(i);
        page.Thumbnail.Dispose();
        ReleasePreview();
        TryDelete(page.FilePath);
        RebuildList(Math.Min(i, _pages.Count - 1));
        SetStatus($"Page {i + 1} deleted. {_pages.Count} page(s) remaining.");
    }

    private Image MakeThumbnail(string path)
    {
        var size = _thumbs.ImageSize;
        var thumb = new Bitmap(size.Width, size.Height);
        using var g = Graphics.FromImage(thumb);
        g.Clear(_lstPages.BackColor);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        Image img;
        try { img = DocumentWriter.LoadImage(path); }
        catch (Exception ex) when (ex is OutOfMemoryException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            // Very large scans (e.g. 2400 DPI color) can exceed what GDI+ can open. Keep the page; show a placeholder.
            using var f = new Font(Font.FontFamily, 8.5f);
            g.DrawRectangle(Pens.Silver, 2, 2, size.Width - 5, size.Height - 5);
            g.DrawString("Too large\nto preview", f, Brushes.DimGray, new RectangleF(4, 0, size.Width - 8, size.Height),
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            return thumb;
        }
        using var _ = img;
        float scale = Math.Min((size.Width - 4f) / img.Width, (size.Height - 4f) / img.Height);
        int w = Math.Max(1, (int)(img.Width * scale)), h = Math.Max(1, (int)(img.Height * scale));
        var rect = new Rectangle((size.Width - w) / 2, (size.Height - h) / 2, w, h);
        g.DrawImage(img, rect);
        g.DrawRectangle(Pens.Silver, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        return thumb;
    }

    // ---------------------------------------------------------------- shutdown

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_scanCancel != null)
        {
            _scanCancel.Cancel();
            e.Cancel = true; // let the scan wind down; the user can close again afterwards
            SetStatus("Cancelling scan… close the window again once it stops.");
            return;
        }

        if (_pages.Count > 0)
        {
            var answer = MessageBox.Show(this,
                $"You have {_pages.Count} unsaved scanned page(s).\n\nSave them before closing?",
                "Unsaved pages", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.Cancel) { e.Cancel = true; return; }
            if (answer == DialogResult.Yes)
            {
                SavePages();
                if (_pages.Count > 0) { e.Cancel = true; return; } // save failed
            }
        }

        SaveSettings();
        _viewer.Image?.Dispose();
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
