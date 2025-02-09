/*----------------------------------------------------------------------------*/
/*--  This program is free software: you can redistribute it and/or modify  --*/
/*--  it under the terms of the GNU General Public License as published by  --*/
/*--  the Free Software Foundation, either version 3 of the License, or     --*/
/*--  (at your option) any later version.                                   --*/
/*--                                                                        --*/
/*--  This program is distributed in the hope that it will be useful,       --*/
/*--  but WITHOUT ANY WARRANTY; without even the implied warranty of        --*/
/*--  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the          --*/
/*--  GNU General Public License for more details.                          --*/
/*--                                                                        --*/
/*--  You should have received a copy of the GNU General Public License     --*/
/*--  along with this program. If not, see <http://www.gnu.org/licenses/>.  --*/
/*----------------------------------------------------------------------------*/

using pk3DS.Core;
using pk3DS.Core.CTR;
using pk3DS.Core.Structures.PersonalInfo;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace pk3DS
{
    public sealed partial class Main : Form
    {
        public Main()
        {
            // Initialize the Main Form
            InitializeComponent();

            // Prepare DragDrop Functionality
            AllowDrop = TB_Path.AllowDrop = true;
            DragEnter += TabMain_DragEnter;
            DragDrop += TabMain_DragDrop;
            TB_Path.DragEnter += TabMain_DragEnter;
            TB_Path.DragDrop += TabMain_DragDrop;
            foreach (var t in TC_RomFS.TabPages.OfType<TabPage>())
            {
                t.AllowDrop = true;
                t.DragEnter += TabMain_DragEnter;
                t.DragDrop += TabMain_DragDrop;
            }

            // Reload Previous Editing Files if the file exists
            var settings = Properties.Settings.Default;
            CB_Lang.SelectedIndex = settings.Language;
            var path = settings.GamePath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    OpenQuick(path);
                }
                catch (Exception ex)
                {
                    WinFormsUtil.Error($"Unable to automatically load the previously opened ROM dump located at -- {path}.", ex.Message);
                    ResetStatus();
                }
            }

            string[] args = Environment.GetCommandLineArgs();
            string filename = args.Length > 0 ? Path.GetFileNameWithoutExtension(args[0])?.ToLower() : "";
            skipBoth = filename.IndexOf("3DSkip", StringComparison.Ordinal) >= 0;

            var randset = RandSettings.FileName;
            if (File.Exists(randset))
                RandSettings.Load(File.ReadAllLines(randset));
        }

        internal static GameConfig Config;
        public static string RomFSPath;
        public static string ExeFSPath;
        public static string ExHeaderPath;
        private volatile int threads;
        internal static volatile int Language;
        internal static SMDH SMDH;
        private uint HANSgameID; // for exporting RomFS/ExeFS with correct X8 gameID
        private readonly bool skipBoth;
        public static PersonalInfo[] SpeciesStat => Config.Personal.Table;

        // Main Form Methods
        private void L_About_Click(object sender, EventArgs e)
        {
            new About().ShowDialog();
        }

        private void L_GARCInfo_Click(object sender, EventArgs e)
        {
            if (RomFSPath == null)
                return;

            string s = "Game Type: " + Config.Version + Environment.NewLine;
            s = Config.Files.Select(file => file.Name).Aggregate(s, (current, t) => current + string.Format(Environment.NewLine + "{0} - {1}", t, Config.GetGARCFileName(t)));

            var copyPrompt = WinFormsUtil.Prompt(MessageBoxButtons.YesNo, s, "Copy to Clipboard?");
            if (copyPrompt != DialogResult.Yes)
                return;

            try { Clipboard.SetText(s); }
            catch { WinFormsUtil.Alert("Unable to copy to Clipboard."); }
        }

        private void L_Game_Click(object sender, EventArgs e) => new EnhancedRestore(Config).ShowDialog();

        private void B_Open_Click(object sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog();
            if (fbd.ShowDialog() == DialogResult.OK)
                OpenQuick(fbd.SelectedPath);
        }

        private void ChangeLanguage(object sender, EventArgs e)
        {
            if (InvokeRequired)
                Invoke((MethodInvoker)delegate { Language = CB_Lang.SelectedIndex; });
            else Language = CB_Lang.SelectedIndex;
            if (Config != null)
                Config.Language = Language;
            Menu_Options.DropDown.Close();
            if (!Tab_RomFS.Enabled || Config == null)
                return;

            if ((Config.XY || Config.ORAS) && Language > 7)
            {
                WinFormsUtil.Alert("Language not available for games. Defaulting to English.");
                if (InvokeRequired)
                    Invoke((MethodInvoker)delegate { CB_Lang.SelectedIndex = 2; });
                else CB_Lang.SelectedIndex = 2;
                return; // set event re-triggers this method
            }

            UpdateProgramTitle();
            Config.InitializeGameText();
            Properties.Settings.Default.Language = Language;
            Properties.Settings.Default.Save();
        }

        private void Menu_Exit_Click(object sender, EventArgs e)
        {
            Close();
        }

        private void CloseForm(object sender, FormClosingEventArgs e)
        {
            if (Config == null)
                return;
            var g = Config.GARCGameText;
            string[][] files = Config.GameTextStrings;
            g.Files = files.Select(x => TextFile.GetBytes(Config, x)).ToArray();
            g.Save();

            try
            {
                var text = RandSettings.Save();
                File.WriteAllLines(RandSettings.FileName, text, Encoding.Unicode);
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch
#pragma warning restore CA1031 // Do not catch general exception types
            {
                // ignored
            }
        }

        private void OpenQuick(string path)
        {
            if (ThreadActive())
                return;

            try
            {
                if (!Directory.Exists(path)) // File
                    OpenFile(path);
                else // Directory
                    OpenDirectory(path);
            }
            catch (Exception ex)
            {
                WinFormsUtil.Error($"Failed to open -- {path}", ex.Message);
                ResetStatus();
            }
        }

        private void OpenFile(string path)
        {
            if (!File.Exists(path))
                return;

            FileInfo fi = new FileInfo(path);
            if (fi.Name.Contains("code.bin")) // Compress/Decompress .code.bin
            {
                OpenExeFSCodeBinary(path, fi);
            }
            else if (fi.Name.IndexOf("exe", StringComparison.OrdinalIgnoreCase) >= 0) // Unpack exefs
            {
                OpenExeFSCombined(path, fi);
            }
            else if (fi.Name.IndexOf("rom", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                WinFormsUtil.Alert("RomFS unpacking not implemented.");
            }
            else
            {
                var dr = WinFormsUtil.Prompt(MessageBoxButtons.YesNoCancel, "Unpack sub-files?", "Cancel: Abort");
                if (dr == DialogResult.Cancel)
                    return;
                bool recurse = dr == DialogResult.Yes;
                ToolsUI.OpenARC(path, pBar1, recurse);
            }
        }

        private void OpenExeFSCombined(string path, FileInfo fi)
        {
            if (fi.Length % 0x200 != 0)
                return;

            var prompt = WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Detected ExeFS.bin.", "Unpack?");
            if (prompt != DialogResult.Yes)
                return;

            new Thread(() =>
            {
                Interlocked.Increment(ref threads);
                ExeFS.UnpackExeFS(path, Path.GetDirectoryName(path));
                Interlocked.Decrement(ref threads);
                WinFormsUtil.Alert("Unpacked!");
            }).Start();
        }

        private void OpenExeFSCodeBinary(string path, FileInfo fi)
        {
            if (fi.Length % 0x200 == 0)
            {
                var prompt = WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Detected Decompressed code.bin.", "Compress? File will be replaced.");
                if (prompt != DialogResult.Yes)
                    return;
                new Thread(() =>
                {
                    Interlocked.Increment(ref threads);
                    new BLZCoder(new[] {"-en", path}, pBar1);
                    Interlocked.Decrement(ref threads);
                    WinFormsUtil.Alert("Compressed!");
                }).Start();
            }
            else
            {
                var prompt = WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Detected Compressed code.bin.", "Decompress? File will be replaced.");
                if (prompt != DialogResult.Yes)
                    return;
                new Thread(() =>
                {
                    Interlocked.Increment(ref threads);
                    new BLZCoder(new[] { "-d", path }, pBar1);
                    Interlocked.Decrement(ref threads);
                    WinFormsUtil.Alert("Decompressed!");
                }).Start();
            }
        }

        private void OpenDirectory(string path)
        {
            if (!Directory.Exists(path))
                return;

            // Check for ROMFS/EXEFS/EXHEADER
            RomFSPath = ExeFSPath = null; // Reset
            Config = null;

            string[] folders = Directory.GetDirectories(path);
            int count = folders.Length;

            // Find RomFS folder
            foreach (string f in folders.Where(f => new DirectoryInfo(f).Name.IndexOf("rom", StringComparison.OrdinalIgnoreCase) >= 0 && Directory.Exists(f)))
                CheckIfRomFS(f);
            // Find ExeFS folder
            foreach (string f in folders.Where(f => new DirectoryInfo(f).Name.IndexOf("exe", StringComparison.OrdinalIgnoreCase) >= 0 && Directory.Exists(f)))
                CheckIfExeFS(f);

            if (count > 3)
                WinFormsUtil.Alert("pk3DS will function best if you keep your Game Files folder clean and free of unnecessary folders.");

            // Enable buttons if applicable
            Tab_RomFS.Enabled = Menu_Restore.Enabled = Tab_CRO.Enabled = Menu_CRO.Enabled = Menu_Shuffler.Enabled = RomFSPath != null;
            Tab_ExeFS.Enabled = RomFSPath != null && ExeFSPath != null;
            if (RomFSPath != null && Config != null)
            {
                ToggleSubEditors();
                string newtext = $"Game Loaded: {Config.Version}";
                if (L_Game.Text != newtext && Directory.Exists("personal"))
                {
                    Directory.Delete("personal", true);
                } // Force reloading of personal data if the game is switched.

                L_Game.Text = newtext;
                TB_Path.Text = path;
            }
            else if (ExeFSPath != null)
            {
                L_Game.Text = "ExeFS loaded - no RomFS";
                TB_Path.Text = path;
            }
            else
            {
                L_Game.Text = "No Game Loaded";
                TB_Path.Text = "";
            }

            if (RomFSPath != null)
            {
                // Trigger Data Loading
                if (RTB_Status.Text.Length > 0)
                    RTB_Status.Clear();

                UpdateStatus("Data found! Loading persistent data for subforms...", false);
                try
                {
                    Config.Initialize(RomFSPath, ExeFSPath, Language);
                    Config.BackupFiles();
                }
                catch (Exception ex)
                {
                    WinFormsUtil.Error("Failed to load game data from romfs. Please double check your ROM dump is correct.", ex.Message);
                    ResetStatus();
                    return;
                }
            }

            UpdateProgramTitle();

            // Enable Rebuilding options if all files have been found
            CheckIfExHeader(path);
            Menu_ExeFS.Enabled =                                                                  ExeFSPath != null;
            Menu_RomFS.Enabled = Menu_Restore.Enabled = Menu_GARCs.Enabled = RomFSPath != null;
            Menu_Patch.Enabled =                                             RomFSPath != null && ExeFSPath != null;
            Menu_3DS.Enabled   =                                             RomFSPath != null && ExeFSPath != null && ExHeaderPath != null;
            Menu_Trimmed3DS.Enabled =                                        RomFSPath != null && ExeFSPath != null && ExHeaderPath != null;

            // Change L_Game if RomFS and ExeFS exists to a better descriptor
            SMDH = ExeFSPath != null
                ? File.Exists(Path.Combine(ExeFSPath, "icon.bin")) ? new SMDH(Path.Combine(ExeFSPath, "icon.bin")) : null
                : null;
            HANSgameID = SMDH != null ? (SMDH.AppSettings?.StreetPassID ?? 0) : 0;
            L_Game.Visible = SMDH == null && RomFSPath != null;
            TB_Path.Select(TB_Path.TextLength, 0);
            // Method finished.
            System.Media.SystemSounds.Asterisk.Play();
            ResetStatus();
            Properties.Settings.Default.GamePath = path;
            Properties.Settings.Default.Save();
        }

        private void B_ExtractCXI_Click(object sender, EventArgs e)
        {
            const string l1 = "Extracting a CXI requires multiple GB of disc space and takes some time to complete.";
            const string l2 = "If you want to continue, press OK to select your CXI and then select your output directory. For best results, make sure the output directory is an empty directory.";
            var prompt = WinFormsUtil.Prompt(MessageBoxButtons.OKCancel, l1, l2);
            if (prompt != DialogResult.OK)
                return;

            using var ofd = new OpenFileDialog {Title = "Select CXI", Filter = "CXI files (*.cxi)|*.cxi"};
            if (ofd.ShowDialog() != DialogResult.OK)
                return;

            using var fbd = new FolderBrowserDialog();
            DialogResult result = fbd.ShowDialog();
            if (result != DialogResult.OK)
                return;

            var inputCXI = ofd.FileName;
            ExtractNCCH(inputCXI, fbd.SelectedPath);
        }

        private void B_Extract3DS_Click(object sender, EventArgs e)
        {
            const string l1 = "Extracting a 3DS file requires multiple GB of disc space and takes some time to complete.";
            const string l2 = "If you want to continue, press OK to select your CXI and then select your output directory. For best results, make sure the output directory is an empty directory.";
            var prompt = WinFormsUtil.Prompt(MessageBoxButtons.OKCancel, l1, l2);
            if (prompt != DialogResult.OK)
                return;

            using var ofd = new OpenFileDialog {Title = "Select 3DS", Filter = "3DS files (*.3ds)|*.3ds"};
            if (ofd.ShowDialog() != DialogResult.OK)
                return;

            using var fbd = new FolderBrowserDialog();
            DialogResult result = fbd.ShowDialog();
            if (result != DialogResult.OK)
                return;

            var input3DS = ofd.FileName;
            ExtractNCSD(input3DS, fbd.SelectedPath);
        }

        private void ExtractNCCH(string ncchPath, string outputDirectory)
        {
            if (!File.Exists(ncchPath))
                return;

            NCCH ncch = new NCCH();

            new Thread(() =>
            {
                Interlocked.Increment(ref threads);
                ncch.ExtractNCCHFromFile(ncchPath, outputDirectory, RTB_Status, pBar1);
                Interlocked.Decrement(ref threads);
                WinFormsUtil.Alert("Extraction complete!");
            }).Start();
        }

        private void ExtractNCSD(string ncsdPath, string outputDirectory)
        {
            if (!File.Exists(ncsdPath))
                return;

            NCSD ncsd = new NCSD();
            new Thread(() =>
            {
                Interlocked.Increment(ref threads);
                ncsd.ExtractFilesFromNCSD(ncsdPath, outputDirectory, RTB_Status, pBar1);
                Interlocked.Decrement(ref threads);
                WinFormsUtil.Alert("Extraction complete!");
            }).Start();
        }

        private void ToggleSubEditors()
        {
            // Hide all buttons
            foreach (var f in from TabPage t in TC_RomFS.TabPages from f in t.Controls.OfType<FlowLayoutPanel>() select f)
            {
                for (int i = f.Controls.Count - 1; i >= 0; i--)
                    f.Controls.Remove(f.Controls[i]);
            }

            B_MoveTutor.Visible = Config.ORAS; // Default false unless loaded

            Control[] romfs, exefs, cro;

            switch (Config.Generation)
            {
                case 6:
                    romfs = new Control[] {B_GameText, B_StoryText, B_Personal, B_Evolution, B_LevelUp, B_Wild, B_MegaEvo, B_EggMove, B_Trainer, B_Item, B_Move, B_Maison, B_TitleScreen, B_OWSE};
                    exefs = new Control[] {B_MoveTutor, B_TMHM, B_Mart, B_Pickup, B_OPower, B_ShinyRate};
                    cro = new Control[] {B_TypeChart, B_Starter, B_Gift, B_Static};
                    B_MoveTutor.Visible = Config.ORAS; // Default false unless loaded
                    break;
                case 7:
                    romfs = new Control[] {B_GameText, B_StoryText, B_Personal, B_Evolution, B_LevelUp, B_Wild, B_MegaEvo, B_EggMove, B_Trainer, B_Item, B_Move, B_Royal, B_Pickup, B_OWSE };
                    exefs = new Control[] {B_TM, B_TypeChart, B_ShinyRate};
                    cro = new Control[] {B_Mart, B_MoveTutor};
                    B_MoveTutor.Visible = Config.USUM;

                    if (Config.Version != GameVersion.SMDEMO)
                        romfs = romfs.Concat(new[] {B_Static}).ToArray();
                    break;
                default:
                    romfs = exefs = cro = new Control[] {new Label {Text = "No editors available."}};
                    break;
            }

            FLP_RomFS.Controls.AddRange(romfs);
            FLP_ExeFS.Controls.AddRange(exefs);
            FLP_CRO.Controls.AddRange(cro);
        }

        private void UpdateProgramTitle() => Text = GetProgramTitle();

        private static string GetProgramTitle()
        {
            // 0 - JP
            // 1 - EN
            // 2 - FR
            // 3 - DE
            // 4 - IT
            // 5 - ES
            // 6 - CHS
            // 7 - KO
            // 8 -
            // 11 - CHT
            if (SMDH?.AppSettings == null)
                return "pk3DS";
            int[] AILang = { 0, 0, 1, 2, 4, 3, 5, 7, 8, 9, 6, 11 };
            return "pk3DS - " + SMDH.AppInfo[AILang[Language]].ShortDescription;
        }

        private static GameConfig CheckGameType(string[] files)
        {
            try
            {
                if (files.Length > 1000)
                    return null;
                string[] fileArr = Directory.GetFiles(Path.Combine(Directory.GetParent(files[0]).FullName, "a"), "*", SearchOption.AllDirectories);
                int fileCount = fileArr.Count(file => Path.GetFileName(file)?.Length == 1);
                return new GameConfig(fileCount);
            }
            catch { }
            return null;
        }

        private bool CheckIfRomFS(string path)
        {
            string[] top = Directory.GetDirectories(path);
            FileInfo fi = new FileInfo(top[top.Length > 1 ? 1 : 0]);
            // Check to see if the folder is romfs
            if (fi.Name == "a")
            {
                string[] files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
                var cfg = CheckGameType(files);

                if (cfg == null)
                {
                    RomFSPath = null;
                    Config = null;
                    WinFormsUtil.Error("File count does not match expected game count.", "Files: " + files.Length);
                    return false;
                }

                RomFSPath = path;
                Config = cfg;
                return true;
            }
            WinFormsUtil.Error("Folder does not contain an 'a' folder in the top level.");
            RomFSPath = null;
            return false;
        }

        private bool CheckIfExeFS(string path)
        {
            string[] files = Directory.GetFiles(path);
            if (files.Length == 1 && string.Equals(Path.GetFileName(files[0]), "exefs.bin", StringComparison.OrdinalIgnoreCase))
            {
                // Prompt if the user wants to unpack the ExeFS.
                if (DialogResult.Yes != WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Detected ExeFS binary.", "Unpack?"))
                    return false;

                // User wanted to unpack. Unpack.
                if (!ExeFS.UnpackExeFS(files[0], path))
                    return false; // on unpack fail

                // Remove ExeFS binary after unpacking
                File.Delete(files[0]);

                files = Directory.GetFiles(path);
                // unpack successful, continue onward!
            }

            if (files.Length != 3 && files.Length != 4)
                return false;

            FileInfo fi = new FileInfo(files[0]);
            if (!fi.Name.Contains("code"))
            {
                if (new FileInfo(files[1]).Name != "code.bin")
                    return false;

                File.Move(files[1], Path.Combine(Path.GetDirectoryName(files[1]), ".code.bin"));
                files = Directory.GetFiles(path);
                fi = new FileInfo(files[0]);
            }
            if (fi.Length % 0x200 != 0 && WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Detected Compressed code binary.", "Decompress? File will be replaced.") == DialogResult.Yes)
                new Thread(() => { Interlocked.Increment(ref threads); new BLZCoder(new[] { "-d", files[0] }, pBar1); Interlocked.Decrement(ref threads); WinFormsUtil.Alert("Decompressed!"); }).Start();

            ExeFSPath = path;
            return true;
        }

        private bool CheckIfExHeader(string path)
        {
            ExHeaderPath = null;
            // Input folder path should contain the ExHeader.
            string[] files = Directory.GetFiles(path);
            foreach (string fp in from s in files let f = new FileInfo(s) where (f.Name.StartsWith("exh", StringComparison.OrdinalIgnoreCase) || f.Name.StartsWith("decryptedexh", StringComparison.OrdinalIgnoreCase)) && f.Length == 0x800 select s)
                ExHeaderPath = fp;

            return ExHeaderPath != null;
        }

        private bool ThreadActive()
        {
            if (threads <= 0)
                return false;
            WinFormsUtil.Alert("Please wait for all operations to finish first."); return true;
        }

        private void TabMain_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void TabMain_DragDrop(object sender, DragEventArgs e)
        {
            string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
            string path = files[0]; // open first D&D
            OpenQuick(path);
        }

        // RomFS Subform Items
        private void RebuildRomFS(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            if (RomFSPath == null)
                return;
            if (WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Rebuild RomFS?") != DialogResult.Yes)
                return;

            SaveFileDialog sfd = new SaveFileDialog
            {
                FileName = HANSgameID != 0 ? HANSgameID.ToString("X8") + ".romfs" : "romfs.bin",
                Filter = "HANS RomFS|*.romfs|Binary File|*.bin|All Files|*.*"
            };
            sfd.FilterIndex = HANSgameID != 0 ? 0 : sfd.Filter.Length - 1;

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                new Thread(() =>
                {
                    UpdateStatus(Environment.NewLine + "Building RomFS binary. Please wait until the program finishes.");

                    Interlocked.Increment(ref threads);
                    RomFS.BuildRomFS(RomFSPath, sfd.FileName, RTB_Status, pBar1);
                    Interlocked.Decrement(ref threads);

                    UpdateStatus("RomFS binary saved." + Environment.NewLine);
                    WinFormsUtil.Alert("Wrote RomFS binary:", sfd.FileName);
                }).Start();
            }
        }

        private void B_GameText_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            new Thread(() =>
            {
                var g = Config.GARCGameText;
                string[][] files = Config.GameTextStrings;
                Invoke((Action)(() => new TextEditor(files, "gametext").ShowDialog()));
                g.Files = TryWriteText(files, g);
                g.Save();
            }).Start();
        }

        private void B_StoryText_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            new Thread(() =>
            {
                var g = Config.GetGARCData("storytext");
                string[][] files = g.Files.Select(file => new TextFile(Config, file).Lines).ToArray();
                Invoke((Action)(() => new TextEditor(files, "storytext").ShowDialog()));
                g.Files = TryWriteText(files, g);
                g.Save();
            }).Start();
        }

        private static byte[][] TryWriteText(string[][] files, GARCFile g)
        {
            byte[][] data = new byte[files.Length][];
            var errata = new List<string>();
            for (int i = 0; i < data.Length; i++)
            {
                try
                {
                    data[i] = TextFile.GetBytes(Config, files[i]);
                }
                catch (Exception ex)
                {
                    errata.Add($"File {i:000} | {ex.Message}");
                    // revert changes
                    data[i] = g.GetFile(i);
                }
            }
            if (errata.Count == 0)
                return data;

            string[] options =
            {
                "Cancel: Discard all changes",
                "Yes: Save changes, dump errata/failed text",
                "No: Save changes, don't dump errata/failed text"
            };
            var dr = WinFormsUtil.Prompt(MessageBoxButtons.YesNoCancel, "Errors found while attempting to save text."
                + Environment.NewLine + "Example: " + errata[0],
                string.Join(Environment.NewLine, options));
            if (dr == DialogResult.Cancel)
                return g.Files; // discard
            if (dr == DialogResult.No)
                return data;

            const string txt_errata = "text_errata.txt";
            const string txt_failed = "text_failed.txt";
            File.WriteAllLines(txt_errata, errata);
            TextEditor.ExportTextFile(txt_failed, true, files);

            WinFormsUtil.Alert("Saved text files to path: " + Application.StartupPath,
                txt_errata + Environment.NewLine + txt_failed);

            return data;
        }

        private void B_Maison_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            DialogResult dr;
            switch (Config.Generation)
            {
                case 6:
                    dr = WinFormsUtil.Prompt(MessageBoxButtons.YesNoCancel, "Edit Super Maison instead of Normal Maison?", "Yes = Super, No = Normal, Cancel = Abort");
                    break;
                case 7:
                    dr = WinFormsUtil.Prompt(MessageBoxButtons.YesNoCancel, "Edit Battle Royal instead of Battle Tree?", "Yes = Royal, No = Tree, Cancel = Abort");
                    break;
                default:
                    return;
            }
            if (dr == DialogResult.Cancel)
                return;

            new Thread(() =>
            {
                bool super = dr == DialogResult.Yes;
                string c = super ? "S" : "N";
                var trdata = Config.GetGARCData("maisontr"+c);
                var trpoke = Config.GetGARCData("maisonpk"+c);
                byte[][] trd = trdata.Files;
                byte[][] trp = trpoke.Files;
                switch (Config.Generation)
                {
                    case 6:
                        Invoke((Action)(() => new MaisonEditor6(trd, trp, super).ShowDialog()));
                        break;
                    case 7:
                        Invoke((Action)(() => new MaisonEditor7(trd, trp, super).ShowDialog()));
                        break;
                }
                trdata.Files = trd;
                trpoke.Files = trp;
                trdata.Save();
                trpoke.Save();
            }).Start();
        }

        private void B_Personal_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            new Thread(() =>
            {
                byte[][] d = Config.GARCPersonal.Files;
                switch (Config.Generation)
                {
                    case 6:
                        Invoke((Action)(() => new PersonalEditor6(d).ShowDialog()));
                        break;
                    case 7:
                        Invoke((Action)(() => new PersonalEditor7(d).ShowDialog()));
                        break;
                }
                // Set Master Table back
                for (int i = 0; i < d.Length - 1; i++)
                    d[i].CopyTo(d[d.Length-1], i * d[i].Length);

                Config.GARCPersonal.Files = d;
                Config.GARCPersonal.Save();
                Config.InitializePersonal();
            }).Start();
        }

        private void B_Trainer_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            new Thread(() =>
            {
                var trclass = Config.GetGARCData("trclass");
                var trdata = Config.GetGARCData("trdata");
                var trpoke = Config.GetGARCData("trpoke");
                byte[][] trc = trclass.Files;
                byte[][] trd = trdata.Files;
                byte[][] trp = trpoke.Files;

                switch (Config.Generation)
                {
                    case 6:
                        Invoke((Action)(() => new RSTE(trd, trp).ShowDialog()));
                        break;
                    case 7:
                        Invoke((Action)(() => new SMTE(trd, trp).ShowDialog()));
                        break;
                }
                trclass.Files = trc;
                trdata.Files = trd;
                trpoke.Files = trp;
                trclass.Save();
                trdata.Save();
                trpoke.Save();
            }).Start();
        }

        private void B_Wild_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            new Thread(() =>
            {
                string[] files;
                Action action;
                switch (Config.Generation)
                {
                    case 6:
                        files = new[] { "encdata" };
                        if (Config.ORAS)
                            action = () => new RSWE().ShowDialog();
                        else if (Config.XY)
                            action = () => new XYWE().ShowDialog();
                        else return;

                        Invoke((MethodInvoker)delegate { Enabled = false; });
                        FileGet(files, false);
                        Invoke(action);
                        FileSet(files);
                        Invoke((MethodInvoker)delegate { Enabled = true; });
                        break;
                    case 7:
                        Invoke((MethodInvoker)delegate { Enabled = false; });
                        Interlocked.Increment(ref threads);

                        files = new [] { "encdata", "zonedata", "worlddata" };
                        UpdateStatus($"GARC Get: {files[0]}... ");
                        var ed = Config.GetlzGARCData(files[0]);
                        UpdateStatus($"GARC Get: {files[1]}... ");
                        var zd = Config.GetlzGARCData(files[1]);
                        UpdateStatus($"GARC Get: {files[2]}... ");
                        var wd = Config.GetlzGARCData(files[2]);
                        UpdateStatus("Running SMWE... ");
                        action = () => new SMWE(ed, zd, wd).ShowDialog();
                        Invoke(action);

                        UpdateStatus($"GARC Set: {files[0]}... ");
                        ed.Save();
                        ResetStatus();
                        Interlocked.Decrement(ref threads);
                        Invoke((MethodInvoker)delegate { Enabled = true; });
                        break;
                    default:
                        return;
                }
            }).Start();
        }

        private void B_OWSE_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            if (DialogResult.Yes != WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "The OverWorld/Script Editor is not recommended for most users and is still a work-in-progress.", "Continue anyway?"))
                return;
            switch (Config.Generation)
            {
                case 6:
                    RunOWSE6();
                    return;
                case 7:
                    RunOWSE7();
                    return;
            }
        }

        private void RunOWSE6()
        {
            Enabled = false;
            new Thread(() =>
            {
                bool reload = ModifierKeys == Keys.Control || ModifierKeys == (Keys.Alt | Keys.Control);
                string[] files = {"encdata", "storytext", "mapGR", "mapMatrix"};
                if (reload || files.Sum(t => Directory.Exists(t) ? 0 : 1) != 0) // Dev bypass if all exist already
                    FileGet(files, false);

                // Don't set any data back. Just view.
                {
                    var g = Config.GetGARCData("storytext");
                    string[][] tfiles = g.Files.Select(file => new TextFile(Config, file).Lines).ToArray();
                    Invoke((Action)(() => new OWSE().Show()));
                    Invoke((Action)(() => new TextEditor(tfiles, "storytext").Show()));
                    while (Application.OpenForms.Count > 1)
                        Thread.Sleep(200);
                }
                Invoke((MethodInvoker) delegate { Enabled = true; });
                FileSet(files);
            }).Start();
        }

        private void RunOWSE7()
        {
            Enabled = false;
            new Thread(() =>
            {
                var files = new[] { "encdata", "zonedata", "worlddata" };
                UpdateStatus($"GARC Get: {files[0]}... ");
                var ed = Config.GetlzGARCData(files[0]);
                UpdateStatus($"GARC Get: {files[1]}... ");
                var zd = Config.GetlzGARCData(files[1]);
                UpdateStatus($"GARC Get: {files[2]}... ");
                var wd = Config.GetlzGARCData(files[2]);

                var g = Config.GetGARCData("storytext");
                string[][] tfiles = g.Files.Select(file => new TextFile(Config, file).Lines).ToArray();
                Invoke((Action)(() => new OWSE7(ed, zd, wd).Show()));
                while (Application.OpenForms.Count > 1)
                    Thread.Sleep(200);
                Invoke((MethodInvoker)delegate { Enabled = true; });
            }).Start();
        }

        private void B_Evolution_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            new Thread(() =>
            {
                var g = Config.GetGARCData("evolution");
                byte[][] d = g.Files;
                switch (Config.Generation)
                {
                    case 6:
                        Invoke((Action)(() => new EvolutionEditor6(d).ShowDialog()));
                        break;
                    case 7:
                        Invoke((Action)(() => new EvolutionEditor7(d).ShowDialog()));
                        break;
                }
                g.Files = d;
                Config.InitializeEvos();
                g.Save();
            }).Start();
        }

        private void B_MegaEvo_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            new Thread(() =>
            {
                var g = Config.GetGARCData("megaevo");
                byte[][] d = g.Files;
                switch (Config.Generation)
                {
                    case 6:
                        Invoke((Action)(() => new MegaEvoEditor6(d).ShowDialog()));
                        break;
                    case 7:
                        Invoke((Action)(() => new MegaEvoEditor7(d).ShowDialog()));
                        break;
                }
                g.Files = d;
                g.Save();
            }).Start();
        }

        private void B_Item_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            new Thread(() =>
            {
                var g = Config.GetGARCData("item");
                byte[][] d = g.Files;
                switch (Config.Generation)
                {
                    case 6:
                        Invoke((Action)(() => new ItemEditor6(d).ShowDialog()));
                        break;
                    case 7:
                        Invoke((Action)(() => new ItemEditor7(d).ShowDialog()));
                        break;
                }
                g.Files = d;
                g.Save();
            }).Start();
        }

        private void B_Move_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            new Thread(() =>
            {
                var g = Config.GARCMoves;
                byte[][] Moves;
                switch (Config.Generation)
                {
                    case 6:
                        bool isMini = Config.ORAS;
                        Moves = isMini ? Mini.UnpackMini(g.GetFile(0), "WD") : g.Files;
                        Invoke((Action)(() => new MoveEditor6(Moves).ShowDialog()));
                        g.Files = isMini ? new[] { Mini.PackMini(Moves, "WD") } : Moves;
                        break;
                    case 7:
                        Moves = Mini.UnpackMini(g.GetFile(0), "WD");
                        Invoke((Action)(() => new MoveEditor7(Moves).ShowDialog()));
                        g.Files = new[] {Mini.PackMini(Moves, "WD")};
                        break;
                }
                g.Save();
                Config.InitializeMoves();
            }).Start();
        }

        private void B_LevelUp_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            new Thread(() =>
            {
                byte[][] d = Config.GARCLearnsets.Files;
                switch (Config.Generation)
                {
                    case 6:
                        Invoke((Action)(() => new LevelUpEditor6(d).ShowDialog()));
                        break;
                    case 7:
                        Invoke((Action)(() => new LevelUpEditor7(d).ShowDialog()));
                        break;
                }
                Config.GARCLearnsets.Files = d;
                Config.GARCLearnsets.Save();
                Config.InitializeLearnset();
            }).Start();
        }

        private void B_EggMove_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            new Thread(() =>
            {
                var g = Config.GetGARCData("eggmove");
                byte[][] d = g.Files;
                switch (Config.Generation)
                {
                    case 6:
                        Invoke((Action)(() => new EggMoveEditor6(d).ShowDialog()));
                        break;
                    case 7:
                        Invoke((Action)(() => new EggMoveEditor7(d).ShowDialog()));
                        break;
                }
                g.Files = d;
                g.Save();
            }).Start();
        }

        private void B_TitleScreen_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            new Thread(() =>
            {
                string[] files = { "titlescreen" };
                FileGet(files); // Compressed files exist, handled in the other form since there's so many
                Invoke((Action)(() => new TitleScreenEditor6().ShowDialog()));
                FileSet(files);
            }).Start();
        }
        // RomFS File Requesting Method Wrapper
        private void FileGet(string[] files, bool skipDecompression = true, bool skipGet = false)
        {
            if (skipGet || skipBoth)
                return;
            foreach (string toEdit in files)
            {
                string GARC = Config.GetGARCFileName(toEdit);
                UpdateStatus($"GARC Get: {toEdit} @ {GARC}... ");
                ThreadGet(Path.Combine(RomFSPath, GARC), toEdit, true, skipDecompression);
                while (threads > 0) Thread.Sleep(50);
                ResetStatus();
            }
        }

        private void FileSet(IEnumerable<string> files, bool keep = false)
        {
            if (skipBoth)
                return;
            foreach (string toEdit in files)
            {
                string GARC = Config.GetGARCFileName(toEdit);
                UpdateStatus($"GARC Set: {toEdit} @ {GARC}... ");
                ThreadSet(Path.Combine(RomFSPath, GARC), toEdit, 4); // 4 bytes for Gen6
                while (threads > 0) Thread.Sleep(50);
                if (!keep && Directory.Exists(toEdit)) Directory.Delete(toEdit, true);
                ResetStatus();
            }
        }

        // ExeFS Subform Items
        private void RebuildExeFS(object sender, EventArgs e)
        {
            if (ExeFSPath == null)
                return;
            if (WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Rebuild ExeFS?") != DialogResult.Yes)
                return;

            string[] files = Directory.GetFiles(ExeFSPath);
            int file = 0; if (files[1].Contains("code")) file = 1;

            SaveFileDialog sfd = new SaveFileDialog
            {
                FileName = HANSgameID != 0 ? HANSgameID.ToString("X8") + ".exefs" : "exefs.bin",
                Filter = "HANS ExeFS|*.exefs|Binary File|*.bin|All Files|*.*"
            };
            sfd.FilterIndex = HANSgameID != 0 ? 0 : sfd.Filter.Length - 1;

            if (sfd.ShowDialog() == DialogResult.OK)
            {
                new Thread(() =>
                {
                    Interlocked.Increment(ref threads);
                    new BLZCoder(new[] { "-en", files[file] }, pBar1);
                    WinFormsUtil.Alert("Compressed!");
                    ExeFS.PackExeFS(Directory.GetFiles(ExeFSPath), sfd.FileName);
                    Interlocked.Decrement(ref threads);
                }).Start();
            }
        }

        private void B_Pickup_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            switch (Config.Generation)
            {
                case 6:
                    if (ExeFSPath != null) new PickupEditor6().Show();
                    break;
                case 7:
                    var pickup = Config.GetlzGARCData("pickup");
                    Invoke((Action)(() => new PickupEditor7(pickup).ShowDialog()));
                    break;
            }
        }

        private void B_TMHM_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            if (ExeFSPath == null)
                return;
            switch (Config.Generation)
            {
                case 6: new TMHMEditor6().Show(); break;
                case 7: new TMEditor7().Show(); break;
            }
        }

        private void B_Mart_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            switch (Config.Generation)
            {
                case 6:
                    if (ExeFSPath != null) new MartEditor6().Show();
                    break;

                case 7:
                    if (ThreadActive())
                        return;
                    if (DialogResult.Yes != WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "CRO Editing causes crashes if you do not patch the RO module.", "In order to patch the RO module, your device must be running Custom Firmware (for example, Luma3DS).", "Continue anyway?"))
                        return;
                    if (RomFSPath != null) (Config.USUM ? new MartEditor7UU() : (Form)new MartEditor7()).Show();
                    break;
            }
        }

        private void B_MoveTutor_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            switch (Config.Generation)
            {
                case 6:
                    if (ExeFSPath != null) new TutorEditor6().Show();
                    break;
                case 7:
                    if (DialogResult.Yes != WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "CRO Editing causes crashes if you do not patch the RO module.", "In order to patch the RO module, your device must be running Custom Firmware (for example, Luma3DS).", "Continue anyway?"))
                        return;
                    if (RomFSPath != null) new TutorEditor7().Show();
                    break;
            }
        }

        private void B_OPower_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            if (ExeFSPath != null) new OPower().Show();
        }

        private void B_ShinyRate_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            if (ExeFSPath != null) new ShinyRate().ShowDialog();
        }

        // CRO Subform Items
        private void PatchCRO_CRR(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            if (RomFSPath == null)
                return;
            if (DialogResult.Yes != WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Rebuilding CRO/CRR is not necessary if you patch the RO module.", "Continue?"))
                return;
            new Thread(() =>
            {
                Interlocked.Increment(ref threads);
                CRO.E_HashCRR(Path.Combine(RomFSPath, ".crr", "static.crr"), RomFSPath, true, /* true // don't patch crr for now */ false, RTB_Status, pBar1);
                Interlocked.Decrement(ref threads);

                WinFormsUtil.Alert("CRO's and CRR have been updated.",
                        "If you have made any modifications, it is required that the RSA Verification check be patched on the system in order for the modified CROs to load (ie, no file redirection like NTR's layeredFS).");
            }).Start();
        }

        private void B_Starter_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            if (DialogResult.Yes != WinFormsUtil.Prompt(MessageBoxButtons.YesNo,                 "CRO Editing causes crashes if you do not patch the RO module.", "In order to patch the RO module, your device must be running Custom Firmware (for example, Luma3DS).", "Continue anyway?"))
                return;
            string CRO = Path.Combine(RomFSPath, "DllPoke3Select.cro");
            string CRO2 = Path.Combine(RomFSPath, "DllField.cro");
            if (!File.Exists(CRO))
            {
                WinFormsUtil.Error("File Missing!", "DllPoke3Select.cro was not found in your RomFS folder!");
                return;
            }
            if (!File.Exists(CRO2))
            {
                WinFormsUtil.Error("File Missing!", "DllField.cro was not found in your RomFS folder!");
                return;
            }
            new StarterEditor6().ShowDialog();
        }

        private void B_TypeChart_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;

            switch (Config.Generation)
            {
                case 6:
                    if (DialogResult.Yes != WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "CRO Editing causes crashes if you do not patch the RO module.", "In order to patch the RO module, your device must be running Custom Firmware (for example, Luma3DS).", "Continue anyway?"))
                        return;
                    string CRO = Path.Combine(RomFSPath, "DllBattle.cro");
                    if (!File.Exists(CRO))
                    {
                        WinFormsUtil.Error("File Missing!", "DllBattle.cro was not found in your RomFS folder!");
                        return;
                    }
                    new TypeChart6().ShowDialog();
                    break;
                case 7:
                    new TypeChart7().ShowDialog();
                    break;
            }
        }

        private void B_Gift_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;
            if (DialogResult.Yes != WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "CRO Editing causes crashes if you do not patch the RO module.", "In order to patch the RO module, your device must be running Custom Firmware (for example, Luma3DS).", "Continue anyway?"))
                return;
            string CRO = Path.Combine(RomFSPath, "DllField.cro");
            if (!File.Exists(CRO))
            {
                WinFormsUtil.Error("File Missing!", "DllField.cro was not found in your RomFS folder!");
                return;
            }
            new GiftEditor6().ShowDialog();
        }

        private void B_Static_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;

            if (Config.Generation == 7)
            {
                new Thread(() =>
                {
                    var esg = Config.GetGARCData("encounterstatic");
                    byte[][] es = esg.Files;

                    Invoke((Action)(() => new StaticEncounterEditor7(es).ShowDialog()));
                    esg.Files = es;
                    esg.Save();
                }).Start();
                return;
            }

            if (DialogResult.Yes != WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "CRO Editing causes crashes if you do not patch the RO module.", "In order to patch the RO module, your device must be running Custom Firmware (for example, Luma3DS).", "Continue anyway?"))
                return;
            string CRO = Path.Combine(RomFSPath, "DllField.cro");
            if (!File.Exists(CRO))
            {
                WinFormsUtil.Error("File Missing!", "DllField.cro was not found in your RomFS folder!");
                return;
            }
            new StaticEncounterEditor6().ShowDialog();
        }

        // CXI Building
        private void B_RebuildTrimmed3DS_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;

            SaveFileDialog sfd = new SaveFileDialog
            {
                FileName = "newROM.3ds",
                Filter = "Binary File|*.*"
            };
            if (sfd.ShowDialog() != DialogResult.OK)
                return;
            string path = sfd.FileName;

            new Thread(() =>
            {
                Interlocked.Increment(ref threads);
                Exheader exh = new Exheader(ExHeaderPath);
                CTRUtil.BuildROM(true, "Nintendo", ExeFSPath, RomFSPath, ExHeaderPath, exh.GetSerial(), path,
                    true, pBar1, RTB_Status);
                Interlocked.Decrement(ref threads);
            }).Start();
        }

        // 3DS Building
        private void B_Rebuild3DS_Click(object sender, EventArgs e)
        {
            if (ThreadActive())
                return;

            SaveFileDialog sfd = new SaveFileDialog
            {
                FileName = "newROM.3ds",
                Filter = "Binary File|*.*"
            };
            if (sfd.ShowDialog() != DialogResult.OK)
                return;
            string path = sfd.FileName;

            new Thread(() =>
            {
                Interlocked.Increment(ref threads);
                Exheader exh = new Exheader(ExHeaderPath);
                CTRUtil.BuildROM(true, "Nintendo", ExeFSPath, RomFSPath, ExHeaderPath, exh.GetSerial(), path,
                    false, pBar1, RTB_Status);
                Interlocked.Decrement(ref threads);
            }).Start();
        }

        // Extra Tools
        private void L_SubTools_Click(object sender, EventArgs e)
        {
            new ToolsUI().ShowDialog();
        }

        private void B_Patch_Click(object sender, EventArgs e)
        {
            new Patch().ShowDialog();
        }

        private void Menu_BLZ_Click(object sender, EventArgs e)
        {
            var ofd = new OpenFileDialog();
            if (DialogResult.OK != ofd.ShowDialog())
                return;

            string path = ofd.FileName;
            FileInfo fi = new FileInfo(path);
            if (fi.Length > 15 * 1024 * 1024) // 15MB
            { WinFormsUtil.Error("File too big!", fi.Length + " bytes."); return; }

            if (ModifierKeys != Keys.Control && fi.Length % 0x200 == 0 && WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Detected Decompressed Binary.", "Compress? File will be replaced.") == DialogResult.Yes)
                new Thread(() => { Interlocked.Increment(ref threads); new BLZCoder(new[] { "-en", path }, pBar1); Interlocked.Decrement(ref threads); WinFormsUtil.Alert("Compressed!"); }).Start();
            else if (WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Detected Compressed Binary", "Decompress? File will be replaced.") == DialogResult.Yes)
                new Thread(() => { Interlocked.Increment(ref threads); new BLZCoder(new[] { "-d", path }, pBar1); Interlocked.Decrement(ref threads); WinFormsUtil.Alert("Decompressed!"); }).Start();
        }

        private void Menu_LZ11_Click(object sender, EventArgs e)
        {
            var ofd = new OpenFileDialog();
            if (DialogResult.OK != ofd.ShowDialog())
                return;

            string path = ofd.FileName;
            FileInfo fi = new FileInfo(path);
            if (fi.Length > 15*1024*1024) // 15MB
            { WinFormsUtil.Error("File too big!", fi.Length + " bytes."); return; }

            byte[] data = File.ReadAllBytes(path);
            string predict = data[0] == 0x11 ? "compressed" : "decompressed";
            var dr = WinFormsUtil.Prompt(MessageBoxButtons.YesNoCancel, $"Detected {predict} file. Do what?",
                "Yes = Decompress\nNo = Compress\nCancel = Abort");
            new Thread(() =>
            {
                Interlocked.Increment(ref threads);
                if (dr == DialogResult.Yes)
                {
                    try
                    {
                        LZSS.Decompress(path, Path.Combine(Directory.GetParent(path).FullName, "dec_" + Path.GetFileNameWithoutExtension(path) + ".bin"));
                    } catch (Exception err) { WinFormsUtil.Alert("Tried decompression, may have worked:", err.ToString()); }
                    WinFormsUtil.Alert("File Decompressed!", path);
                }
                if (dr == DialogResult.No)
                {
                    LZSS.Compress(path, Path.Combine(Directory.GetParent(path).FullName, Path.GetFileNameWithoutExtension(path).Replace("_dec", "") + ".lz"));
                    WinFormsUtil.Alert("File Compressed!", path);
                }
                Interlocked.Decrement(ref threads);
            }).Start();
        }

        private void Menu_SMDH_Click(object sender, EventArgs e)
        {
            new Icon().ShowDialog();
        }

        private void Menu_Shuffler_Click(object sender, EventArgs e)
        {
            new Shuffler().ShowDialog();
        }

        // GARC Requests
        internal static string GetGARCFileName(string requestedGARC, int lang)
        {
            var garc = Config.GetGARCReference(requestedGARC);
            if (garc.LanguageVariant)
                garc = garc.GetRelativeGARC(lang);

            return garc.Reference;
        }

        private bool GetGARC(string infile, string outfolder, bool PB, bool bypassExt = false)
        {
            if (skipBoth && Directory.Exists(outfolder))
            {
                UpdateStatus("Skipped - Exists!", false);
                Interlocked.Decrement(ref threads);
                return true;
            }
            try
            {
                bool success = GarcUtil.UnpackGARC(infile, outfolder, bypassExt, PB ? pBar1 : null, L_Status, true);
                UpdateStatus(string.Format(success ? "Success!" : "Failed!"), false);
                Interlocked.Decrement(ref threads);
                return success;
            }
            catch (Exception e) { WinFormsUtil.Error("Could not get the GARC:", e.ToString()); Interlocked.Decrement(ref threads); return false; }
        }

        private bool SetGARC(string outfile, string infolder, int padBytes, bool PB)
        {
            if (skipBoth || (ModifierKeys == Keys.Control && WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Cancel writing data back to GARC?") == DialogResult.Yes))
            { Interlocked.Decrement(ref threads); UpdateStatus("Aborted!", false); return false; }

            try
            {
                bool success = GarcUtil.PackGARC(infolder, outfile, Config.GARCVersion, padBytes, PB ? pBar1 : null, L_Status, true);
                Interlocked.Decrement(ref threads);
                UpdateStatus(string.Format(success ? "Success!" : "Failed!"), false);
                return success;
            }
            catch (Exception e) { WinFormsUtil.Error("Could not set the GARC back:", e.ToString()); Interlocked.Decrement(ref threads); return false; }
        }

        private void ThreadGet(string infile, string outfolder, bool PB = true, bool bypassExt = false)
        {
            Interlocked.Increment(ref threads);
            if (Directory.Exists(outfolder))
            {
                try { Directory.Delete(outfolder, true); }
                catch { }
            }

            new Thread(() => GetGARC(infile, outfolder, PB, bypassExt)).Start();
        }

        private void ThreadSet(string outfile, string infolder, int padBytes, bool PB = true)
        {
            Interlocked.Increment(ref threads);
            new Thread(() => SetGARC(outfile, infolder, padBytes, PB)).Start();
        }

        // Update RichTextBox
        private void UpdateStatus(string status, bool preBreak = true)
        {
            string newtext = (preBreak ? Environment.NewLine : "") + status;
            try
            {
                if (RTB_Status.InvokeRequired)
                {
                    RTB_Status.Invoke((MethodInvoker)delegate
                    {
                        RTB_Status.AppendText(newtext);
                        RTB_Status.SelectionStart = RTB_Status.Text.Length;
                        RTB_Status.ScrollToCaret();
                        L_Status.Text = RTB_Status.Lines.Last().Split(new[] {" @"}, StringSplitOptions.None)[0];
                    });
                }
                else
                {
                    RTB_Status.AppendText(newtext);
                    RTB_Status.SelectionStart = RTB_Status.Text.Length;
                    RTB_Status.ScrollToCaret();
                    L_Status.Text = RTB_Status.Lines.Last().Split(new[] { " @" }, StringSplitOptions.None)[0];
                }
            }
            catch { }
        }

        private void ResetStatus()
        {
            try
            {
                if (L_Status.InvokeRequired)
                {
                    L_Status.Invoke((MethodInvoker)(() => L_Status.Text = ""));
                }
                else
                {
                    L_Status.Text = "";
                }
            }
            catch { }
        }

        private void SetInt32SeedToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (DialogResult.Yes != WinFormsUtil.Prompt(MessageBoxButtons.YesNo, "Reseed RNG?", "If yes, copy the 32 bit (not hex) integer seed to the clipboard before hitting Yes."))
                return;

            string val = string.Empty;
            try { val = Clipboard.GetText(); }
            catch { }
            if (int.TryParse(val, out int seed))
            {
                Util.ReseedRand(seed);
                WinFormsUtil.Alert($"Reseeded RNG to seed: {seed}");
                return;
            }
            WinFormsUtil.Alert("Unable to set seed.");
        }
    }
}