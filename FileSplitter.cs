using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

class FileSplitter : Form
{
    static readonly string[] Presets = { "25 МБ (почта)", "100 МБ", "500 МБ", "1 ГБ", "4 ГБ (FAT32)", "Свой размер" };
    static readonly long[] PresetSizes = { 25, 100, 500, 1024, 4095, 0 };

    TabControl tabs;
    TextBox splitFile, joinFile;
    ComboBox presetBox;
    NumericUpDown customBox;
    Label splitInfo, joinInfo, statusLabel;
    ProgressBar progress;
    Button splitButton, joinButton;
    volatile bool busy;
    string lastResult;

    FileSplitter(string start)
    {
        Text = "FileSplitter";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(520, 330);
        Font = new Font("Segoe UI", 10);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragDrop += (s, e) => Pick(((string[])e.Data.GetData(DataFormats.FileDrop))[0]);

        tabs = new TabControl { Location = new Point(12, 12), Size = new Size(496, 230) };
        Controls.Add(tabs);

        var split = new TabPage("Разрезать");
        tabs.TabPages.Add(split);
        split.Controls.Add(new Label { Text = "Файл:", Location = new Point(10, 18), AutoSize = true });
        splitFile = new TextBox { Location = new Point(60, 15), Width = 318, ReadOnly = true, BackColor = SystemColors.Window };
        split.Controls.Add(splitFile);
        split.Controls.Add(BrowseButton(() => { using (var d = new OpenFileDialog()) if (d.ShowDialog(this) == DialogResult.OK) Pick(d.FileName); }));
        split.Controls.Add(new Label { Text = "Размер части:", Location = new Point(10, 60), AutoSize = true });
        presetBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(118, 57), Width = 160 };
        presetBox.Items.AddRange(Presets);
        presetBox.SelectedIndex = 0;
        presetBox.SelectedIndexChanged += (s, e) => { customBox.Enabled = presetBox.SelectedIndex == Presets.Length - 1; UpdateSplitInfo(); };
        split.Controls.Add(presetBox);
        customBox = new NumericUpDown { Location = new Point(288, 57), Width = 80, Minimum = 1, Maximum = 100000, Value = 50, Enabled = false };
        customBox.ValueChanged += (s, e) => UpdateSplitInfo();
        split.Controls.Add(customBox);
        split.Controls.Add(new Label { Text = "МБ", Location = new Point(372, 60), AutoSize = true });
        splitInfo = new Label { Location = new Point(10, 96), Size = new Size(466, 44), ForeColor = Color.DimGray };
        split.Controls.Add(splitInfo);
        splitButton = new Button { Text = "Разрезать", Location = new Point(10, 148), Size = new Size(466, 38) };
        splitButton.Click += (s, e) => StartSplit();
        split.Controls.Add(splitButton);

        var join = new TabPage("Склеить");
        tabs.TabPages.Add(join);
        join.Controls.Add(new Label { Text = "Часть:", Location = new Point(10, 18), AutoSize = true });
        joinFile = new TextBox { Location = new Point(60, 15), Width = 318, ReadOnly = true, BackColor = SystemColors.Window };
        join.Controls.Add(joinFile);
        join.Controls.Add(BrowseButton(() => { using (var d = new OpenFileDialog { Filter = "Первая часть (*.001)|*.001|Все файлы|*.*" }) if (d.ShowDialog(this) == DialogResult.OK) Pick(d.FileName); }));
        joinInfo = new Label { Location = new Point(10, 56), Size = new Size(466, 84), ForeColor = Color.DimGray, Text = "Выбери любую часть (например, video.mp4.001). Остальные части программа найдёт сама" };
        join.Controls.Add(joinInfo);
        joinButton = new Button { Text = "Склеить", Location = new Point(10, 148), Size = new Size(466, 38) };
        joinButton.Click += (s, e) => StartJoin();
        join.Controls.Add(joinButton);

        progress = new ProgressBar { Location = new Point(12, 254), Size = new Size(496, 18) };
        Controls.Add(progress);
        statusLabel = new Label { Location = new Point(12, 280), Size = new Size(496, 44), ForeColor = Color.DimGray, Text = "Можно просто перетащить файл в окно" };
        statusLabel.Click += (s, e) => { if (lastResult != null && File.Exists(lastResult)) Process.Start("explorer.exe", "/select,\"" + lastResult + "\""); };
        Controls.Add(statusLabel);

        if (start != null) Pick(start);
    }

    Button BrowseButton(Action click)
    {
        var b = new Button { Text = "Обзор…", Location = new Point(386, 13), Size = new Size(90, 29) };
        b.Click += (s, e) => click();
        return b;
    }

    static readonly Regex PartName = new Regex(@"^(.*)\.(\d{3})$");

    void Pick(string path)
    {
        if (busy || !File.Exists(path)) return;
        if (PartName.IsMatch(path))
        {
            tabs.SelectedIndex = 1;
            joinFile.Text = path;
            UpdateJoinInfo();
        }
        else
        {
            tabs.SelectedIndex = 0;
            splitFile.Text = path;
            UpdateSplitInfo();
        }
    }

    long PartSize()
    {
        long mb = presetBox.SelectedIndex == Presets.Length - 1 ? (long)customBox.Value : PresetSizes[presetBox.SelectedIndex];
        return mb * 1024 * 1024;
    }

    void UpdateSplitInfo()
    {
        if (!File.Exists(splitFile.Text)) { splitInfo.Text = "Выбери файл, который нужно разрезать на части"; return; }
        long size = new FileInfo(splitFile.Text).Length, part = PartSize();
        long count = (size + part - 1) / part;
        splitInfo.Text = "Размер файла " + FormatSize(size) + " → частей: " + count + ". Рядом появятся " +
            Path.GetFileName(splitFile.Text) + ".001, .002… и файл .sha256 для проверки при склейке";
    }

    static List<string> FindParts(string anyPart, out string target)
    {
        var m = PartName.Match(anyPart);
        target = m.Groups[1].Value;
        var parts = new List<string>();
        for (int i = 1; i < 1000; i++)
        {
            string p = target + "." + i.ToString("000");
            if (!File.Exists(p)) break;
            parts.Add(p);
        }
        return parts;
    }

    void UpdateJoinInfo()
    {
        string target;
        var parts = FindParts(joinFile.Text, out target);
        if (parts.Count == 0) { joinInfo.Text = "Не найдена первая часть (.001)"; return; }
        long total = 0;
        foreach (var p in parts) total += new FileInfo(p).Length;
        joinInfo.Text = "Найдено частей: " + parts.Count + " (" + FormatSize(total) + ")\nРезультат: " + Path.GetFileName(target) +
            (File.Exists(target + ".sha256") ? "\nЕсть контрольная сумма, после склейки файл будет проверен" : "") +
            (File.Exists(target) ? "\n⚠ Такой файл уже есть и будет перезаписан" : "");
    }

    void SetBusy(bool value)
    {
        busy = value;
        splitButton.Enabled = joinButton.Enabled = presetBox.Enabled = !value;
        tabs.Enabled = !value;
        if (!value) progress.Value = 0;
    }

    void Progress(long done, long total, string text)
    {
        int p = (int)(done * 100 / Math.Max(1, total));
        BeginInvoke(new Action(() => { progress.Value = Math.Min(100, p); statusLabel.Text = text; }));
    }

    void Finish(string text, string select)
    {
        BeginInvoke(new Action(() =>
        {
            SetBusy(false);
            statusLabel.Text = text;
            lastResult = select;
            statusLabel.Cursor = select != null ? Cursors.Hand : Cursors.Default;
            if (select != null) statusLabel.Text += "\nНажми здесь, чтобы открыть папку";
            UpdateJoinInfo();
            UpdateSplitInfo();
        }));
    }

    void StartSplit()
    {
        string path = splitFile.Text;
        if (!File.Exists(path)) { MessageBox.Show(this, "Сначала выбери файл", Text); return; }
        long part = PartSize();
        SetBusy(true);
        new Thread(() =>
        {
            try
            {
                var buffer = new byte[4 << 20];
                using (var sha = SHA256.Create())
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                {
                    long total = input.Length, done = 0;
                    int index = 0;
                    while (done < total)
                    {
                        index++;
                        using (var output = new FileStream(path + "." + index.ToString("000"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                        {
                            long written = 0;
                            while (written < part && done < total)
                            {
                                int n = input.Read(buffer, 0, (int)Math.Min(buffer.Length, part - written));
                                if (n == 0) break;
                                output.Write(buffer, 0, n);
                                sha.TransformBlock(buffer, 0, n, null, 0);
                                written += n; done += n;
                                Progress(done, total, "Часть " + index + ": " + FormatSize(done) + " из " + FormatSize(total));
                            }
                        }
                    }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    File.WriteAllText(path + ".sha256", BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant() + " *" + Path.GetFileName(path) + "\n");
                    Finish("Готово: " + index + " частей рядом с исходным файлом. Сам файл не тронут", path + ".001");
                }
            }
            catch (Exception ex) { Finish("Ошибка: " + ex.Message, null); }
        }) { IsBackground = true }.Start();
    }

    void StartJoin()
    {
        string target;
        var parts = FindParts(joinFile.Text, out target);
        if (parts.Count == 0) { MessageBox.Show(this, "Не найдена первая часть (.001)", Text); return; }
        if (File.Exists(target) && MessageBox.Show(this, Path.GetFileName(target) + " уже существует. Перезаписать?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        SetBusy(true);
        new Thread(() =>
        {
            string tmp = target + ".joining";
            try
            {
                long total = 0, done = 0;
                foreach (var p in parts) total += new FileInfo(p).Length;
                var buffer = new byte[4 << 20];
                using (var sha = SHA256.Create())
                {
                    using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
                        foreach (var p in parts)
                            using (var input = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                            {
                                int n;
                                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    output.Write(buffer, 0, n);
                                    sha.TransformBlock(buffer, 0, n, null, 0);
                                    done += n;
                                    Progress(done, total, "Склеиваю " + Path.GetFileName(p) + ": " + FormatSize(done) + " из " + FormatSize(total));
                                }
                            }
                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    string actual = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                    string check = "";
                    if (File.Exists(target + ".sha256"))
                    {
                        string expected = File.ReadAllText(target + ".sha256").Split(' ')[0].Trim().ToLowerInvariant();
                        if (expected != actual)
                        {
                            File.Delete(tmp);
                            Finish("✘ Контрольная сумма не совпала: какая-то часть повреждена или не докачалась", null);
                            return;
                        }
                        check = " ✔ Контрольная сумма совпала.";
                    }
                    if (File.Exists(target)) File.Delete(target);
                    File.Move(tmp, target);
                    Finish("Готово: " + Path.GetFileName(target) + " (" + FormatSize(total) + ")." + check, target);
                }
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                Finish("Ошибка: " + ex.Message, null);
            }
        }) { IsBackground = true }.Start();
    }

    static string FormatSize(long bytes)
    {
        string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return (u == 0 ? v.ToString("0") : v.ToString("0.0")) + " " + units[u];
    }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.Run(new FileSplitter(args.Length > 0 && File.Exists(args[0]) ? args[0] : null));
    }
}
