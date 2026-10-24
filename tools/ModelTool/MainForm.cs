using System.Diagnostics;

namespace GrannyModelTool
{
    /// <summary>The tool's window: pick the character FBX, its texture and animations, convert to a .gmdl.</summary>
    sealed class MainForm : Form
    {
        readonly TextBox _model = Box(), _texture = Box(), _idle = Box(), _walk = Box(), _crouch = Box(), _crouchWalk = Box(), _name = Box(), _folder = Box();
        readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9f), BackColor = Color.FromArgb(24, 24, 28), ForeColor = Color.Gainsboro };
        readonly Button _convert = new() { Text = "Convert to .gmdl", AutoSize = true, Padding = new Padding(16, 4, 16, 4) };
        readonly CheckBox _openFolder = new() { Text = "Open the folder afterwards", AutoSize = true, Checked = true, Margin = new Padding(12, 9, 3, 3) };
        bool _nameEdited;

        static TextBox Box() => new() { Dock = DockStyle.Fill, Margin = new Padding(3, 4, 3, 4) };

        public MainForm()
        {
            Text = "Granny Co-op – Model Tool";
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(820, 780);
            MinimumSize = new Size(640, 640);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;
            Font = new Font("Segoe UI", 9.5f);

            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(10, 10, 10, 0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            Header(grid, "Character");
            Row(grid, "Character FBX:", _model, () => PickFile(_model, "FBX|*.fbx", true));
            Row(grid, "Texture (optional):", _texture, () => PickFile(_texture, "Images" + "|*.png;*.jpg;*.jpeg;*.bmp;*.tga", false));
            Hint(grid, "Empty = the texture packed in the FBX, or a PNG / JPG with the same name next to it (also in a Textures folder).");
            Header(grid, "Animations (all optional – Mixamo FBX, same skeleton)");
            Row(grid, "Standing (Idle):", _idle, () => PickFile(_idle, "FBX|*.fbx", false));
            Row(grid, "Walking:", _walk, () => PickFile(_walk, "FBX|*.fbx", false));
            Row(grid, "Crouching:", _crouch, () => PickFile(_crouch, "FBX|*.fbx", false));
            Row(grid, "Crouched walking:", _crouchWalk, () => PickFile(_crouchWalk, "FBX|*.fbx", false));
            Hint(grid, "Empty = the built-in animation (Mixamo \"Idle\", \"Walking\", \"Crouching Idle\" – made for the bundled characters). " +
                       "Crouched walking: Mixamo \"Crouched Walking\"; empty = the mod makes it from walking + crouching.");
            _idle.PlaceholderText = "built-in: Idle"; _walk.PlaceholderText = "built-in: Walking"; _crouch.PlaceholderText = "built-in: Crouching Idle";
            _crouchWalk.PlaceholderText = "made from walking + crouching";
            var auto = new Button { Text = "Find animations next to the model", AutoSize = true };
            auto.Click += (_, _) => AutoFill(true, true);
            var builtIn = new Button { Text = "Use the built-in animations", AutoSize = true };
            builtIn.Click += (_, _) => { _idle.Text = _walk.Text = _crouch.Text = _crouchWalk.Text = ""; Log("Animations: the built-in ones."); };
            var animButtons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            animButtons.Controls.Add(auto); animButtons.Controls.Add(builtIn);
            grid.Controls.Add(new Label(), 0, grid.RowCount); grid.Controls.Add(animButtons, 1, grid.RowCount); grid.RowCount++;
            Header(grid, "Result");
            Row(grid, "Character name:", _name, null);
            Hint(grid, "The file name is the character's name in the game (Settings → Your character).");
            Row(grid, "Save to folder:", _folder, PickFolder);
            Hint(grid, "The mod's models folder: Granny\\BepInEx\\plugins\\GrannyMP\\models – every player who should see the character needs the file.");

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 8, 10, 8), FlowDirection = FlowDirection.LeftToRight };
            buttons.Controls.Add(_convert); buttons.Controls.Add(_openFolder);
            _convert.Click += async (_, _) => await Convert();
            _name.TextChanged += (_, _) => { if (_name.Focused) _nameEdited = true; };
            _model.TextChanged += (_, _) => { if (!_nameEdited && File.Exists(_model.Text)) _name.Text = Path.GetFileNameWithoutExtension(_model.Text); };

            var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 10) };
            logPanel.Controls.Add(_log);
            Controls.Add(logPanel); Controls.Add(buttons); Controls.Add(grid);

            DragEnter += (_, e) => e.Effect = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] files) Dropped(files); };

            _folder.Text = Settings.Get("folder") ?? FindModelsFolder() ?? "";
            Log("Drop the character FBX (and its animation FBX files) onto this window, or use the Browse buttons.");
            Log("Animations are optional: the built-in ones are used for every empty field. Your own: Mixamo \"Idle\", \"Walking\" and \"Crouching Idle\" (FBX, \"Without Skin\" is enough), same skeleton as the character.");
        }

        // ---------------- layout helpers ----------------
        static void Header(TableLayoutPanel g, string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", 10f, FontStyle.Bold), Margin = new Padding(3, g.RowCount == 0 ? 0 : 10, 3, 2) };
            g.Controls.Add(l, 0, g.RowCount); g.SetColumnSpan(l, 3); g.RowCount++;
        }

        static void Hint(TableLayoutPanel g, string text)
        {
            var l = new Label { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(620, 0), Margin = new Padding(3, 0, 3, 4) };
            g.Controls.Add(l, 1, g.RowCount); g.SetColumnSpan(l, 2); g.RowCount++;
        }

        static void Row(TableLayoutPanel g, string label, TextBox box, Action browse)
        {
            g.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 8, 3) }, 0, g.RowCount);
            g.Controls.Add(box, 1, g.RowCount);
            if (browse != null)
            {
                var b = new Button { Text = "…", Width = 36, Margin = new Padding(3, 3, 3, 3) };
                b.Click += (_, _) => browse();
                g.Controls.Add(b, 2, g.RowCount);
            }
            g.RowCount++;
        }

        // ---------------- picking files ----------------
        void PickFile(TextBox box, string filter, bool isModel)
        {
            using var d = new OpenFileDialog { Filter = filter + "|" + "All files" + "|*.*" };
            var start = File.Exists(box.Text) ? Path.GetDirectoryName(box.Text) : File.Exists(_model.Text) ? Path.GetDirectoryName(_model.Text) : Settings.Get("last");
            if (Directory.Exists(start)) d.InitialDirectory = start;
            if (d.ShowDialog(this) != DialogResult.OK) return;
            box.Text = d.FileName;
            Settings.Set("last", Path.GetDirectoryName(d.FileName));
            if (isModel) AutoFill(false, false);
        }

        void PickFolder()
        {
            using var d = new FolderBrowserDialog { SelectedPath = Directory.Exists(_folder.Text) ? _folder.Text : "" };
            if (d.ShowDialog(this) == DialogResult.OK) _folder.Text = d.SelectedPath;
        }

        /// <summary>Dropped files: animation FBX files by their names, the rest is the character (and images its texture).</summary>
        void Dropped(string[] files)
        {
            bool newModel = false;
            foreach (var f in files)
            {
                var name = Path.GetFileName(f);
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".tga") { _texture.Text = f; continue; }
                if (ext != ".fbx") continue;
                if (Has(name, "Crouch") && Has(name, "Walk")) _crouchWalk.Text = f;
                else if (Has(name, "Crouch")) _crouch.Text = f;
                else if (Has(name, "Walk")) _walk.Text = f;
                else if (Has(name, "Idle")) _idle.Text = f;
                else { _model.Text = f; newModel = true; }
            }
            if (newModel) AutoFill(false, false);
        }

        static bool Has(string s, string part) => s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Texture (and with <paramref name="anims"/> the animations) next to the model – only the empty fields, unless <paramref name="force"/>.</summary>
        void AutoFill(bool force, bool anims)
        {
            if (!File.Exists(_model.Text)) { if (force) Log("Choose the character FBX first."); return; }
            var job = new ExportJob { Model = _model.Text };
            void Set(TextBox b, string v) { if ((force || string.IsNullOrWhiteSpace(b.Text) || !File.Exists(b.Text)) && v != null) b.Text = v; }
            if (anims)
            {
                Set(_idle, job.FindAnimation("Idle"));
                Set(_walk, job.FindAnimation("Walking") ?? job.FindAnimation("Walk"));
                Set(_crouch, job.FindAnimation("Crouch"));
                Set(_crouchWalk, job.FindAnimation("CrouchWalk"));
            }
            if (string.IsNullOrWhiteSpace(_texture.Text) || force) { var t = job.FindTexture(); if (t != null) _texture.Text = t; }
            if (force) Log("Animations looked up next to the model.");
        }

        /// <summary>The mod's models folder, looked for from where the tool runs (it may sit inside the game folder).</summary>
        static string FindModelsFolder()
        {
            try
            {
                for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
                {
                    var m = Path.Combine(d.FullName, "BepInEx", "plugins", "GrannyMP", "models");
                    if (Directory.Exists(m)) return m;
                }
            }
            catch { }
            return null;
        }

        // ---------------- converting ----------------
        async Task Convert()
        {
            if (!File.Exists(_model.Text)) { Log("Choose the character FBX."); return; }
            var name = string.Join("_", (_name.Text ?? "").Trim().Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            if (name.Length == 0) name = Path.GetFileNameWithoutExtension(_model.Text);
            var folder = string.IsNullOrWhiteSpace(_folder.Text) ? Path.GetDirectoryName(_model.Text) : _folder.Text.Trim();
            var output = Path.Combine(folder, name + ".gmdl");
            if (File.Exists(output) && MessageBox.Show(this, $"{output} already exists. Replace it?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var job = new ExportJob
            {
                Model = _model.Text, Output = output,
                Texture = NullIfEmpty(_texture.Text), Idle = NullIfEmpty(_idle.Text), Walk = NullIfEmpty(_walk.Text), Crouch = NullIfEmpty(_crouch.Text),
                CrouchWalk = NullIfEmpty(_crouchWalk.Text),
            };
            _convert.Enabled = false;
            _log.Clear();
            try
            {
                var result = await Task.Run(() => Exporter.Export(job, s => BeginInvoke(() => Log(s))));
                Log(result);
                Log(GmdlInfo.Describe(output).TrimEnd());
                Settings.Set("folder", folder);
                Log("Done. Copy the file to every player's models folder; it appears in Settings → Your character → Change.");
                if (_openFolder.Checked) try { Process.Start("explorer.exe", $"/select,\"{output}\""); } catch { }
            }
            catch (Exception e)
            {
                Log("ERROR: " + e.Message);
                MessageBox.Show(this, e.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { _convert.Enabled = true; }
        }

        static string NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        void Log(string s)
        {
            _log.AppendText(s.Replace("\n", Environment.NewLine) + Environment.NewLine);
        }
    }

    /// <summary>A few remembered values (last folders) in %APPDATA%\GrannyModelTool\settings.txt.</summary>
    static class Settings
    {
        static readonly string File_ = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GrannyModelTool", "settings.txt");
        static Dictionary<string, string> _v;

        static Dictionary<string, string> Values()
        {
            if (_v != null) return _v;
            _v = new();
            try { foreach (var l in File.ReadAllLines(File_)) { int i = l.IndexOf('='); if (i > 0) _v[l[..i]] = l[(i + 1)..]; } } catch { }
            return _v;
        }

        public static string Get(string k) => Values().TryGetValue(k, out var v) ? v : null;
        public static void Set(string k, string v)
        {
            Values()[k] = v;
            try { Directory.CreateDirectory(Path.GetDirectoryName(File_)); File.WriteAllLines(File_, _v.Select(x => x.Key + "=" + x.Value)); } catch { }
        }
    }
}
