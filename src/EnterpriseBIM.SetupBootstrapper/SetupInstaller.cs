using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("EnterpriseBIM Setup Installer")]
[assembly: AssemblyCompany("Enterprise AEC Interoperability Division")]
[assembly: AssemblyProduct("EnterpriseBIM Offline Exchange Pipeline")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace EnterpriseBIM.SetupBootstrapper
{
    public enum InstallTargetMode
    {
        UnifiedBoth = 0,
        RhinoOnly = 1,
        RevitOnly = 2
    }

    public static class Program
    {
        private const string RhinoPluginGuid = "9C4F8D21-7A1E-4B63-8E51-3D29C84A10F2";

        [STAThread]
        public static int Main(string[] args)
        {
            InstallTargetMode defaultMode = ResolveCompiledDefaultMode();
            bool silentMode = false;
            bool uninstallMode = false;
            string revitVersion = "2025";
            string rhinoVersion = "8.0";

            foreach (string arg in args)
            {
                string normalized = arg.Trim();
                if (normalized.Equals("/S", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("/quiet", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("/qn", StringComparison.OrdinalIgnoreCase) ||
                    normalized.Equals("--silent", StringComparison.OrdinalIgnoreCase))
                {
                    silentMode = true;
                }
                else if (normalized.Equals("/uninstall", StringComparison.OrdinalIgnoreCase) ||
                         normalized.Equals("/u", StringComparison.OrdinalIgnoreCase))
                {
                    uninstallMode = true;
                }
                else if (normalized.Equals("/rhino", StringComparison.OrdinalIgnoreCase))
                {
                    defaultMode = InstallTargetMode.RhinoOnly;
                }
                else if (normalized.Equals("/revit", StringComparison.OrdinalIgnoreCase))
                {
                    defaultMode = InstallTargetMode.RevitOnly;
                }
                else if (normalized.Equals("/both", StringComparison.OrdinalIgnoreCase))
                {
                    defaultMode = InstallTargetMode.UnifiedBoth;
                }
                else if (normalized.StartsWith("/revitversion=", StringComparison.OrdinalIgnoreCase))
                {
                    revitVersion = normalized.Substring("/revitversion=".Length).Trim();
                }
                else if (normalized.StartsWith("/rhinoversion=", StringComparison.OrdinalIgnoreCase))
                {
                    rhinoVersion = normalized.Substring("/rhinoversion=".Length).Trim();
                }
            }

            if (silentMode)
            {
                try
                {
                    if (uninstallMode)
                    {
                        ExecuteUninstall(defaultMode, revitVersion, rhinoVersion);
                    }
                    else
                    {
                        ExecuteInstall(defaultMode, revitVersion, rhinoVersion);
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    WriteInstallLog("ERROR: " + ex.ToString());
                    return 1;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (SetupForm form = new SetupForm(defaultMode, revitVersion, rhinoVersion))
            {
                Application.Run(form);
                return form.ExitCode;
            }
        }

        private static InstallTargetMode ResolveCompiledDefaultMode()
        {
#if RHINO_ONLY
            return InstallTargetMode.RhinoOnly;
#elif REVIT_ONLY
            return InstallTargetMode.RevitOnly;
#else
            string exeName = Path.GetFileNameWithoutExtension(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            if (exeName.IndexOf("Rhino", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return InstallTargetMode.RhinoOnly;
            }
            if (exeName.IndexOf("Revit", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return InstallTargetMode.RevitOnly;
            }
            return InstallTargetMode.UnifiedBoth;
#endif
        }

        public static void ExecuteInstall(InstallTargetMode mode, string revitVersion, string rhinoVersion)
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string stagingRoot = Path.Combine(programData, "EnterpriseBIM");
            string logsDir = Path.Combine(stagingRoot, "Logs");

            Directory.CreateDirectory(stagingRoot);
            Directory.CreateDirectory(logsDir);

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string payloadDir = ResolvePayloadDirectory(baseDir);

            if (mode == InstallTargetMode.UnifiedBoth || mode == InstallTargetMode.RhinoOnly)
            {
                string rhinoDir = Path.Combine(
                    programData,
                    "McNeel",
                    "Rhinoceros",
                    rhinoVersion,
                    "Plug-ins",
                    "EnterpriseBIM (" + RhinoPluginGuid + ")");

                Directory.CreateDirectory(rhinoDir);

                CopyOrCreatePayloadFile(payloadDir, "EnterpriseBIM.Core.dll", Path.Combine(rhinoDir, "EnterpriseBIM.Core.dll"));
                CopyOrCreatePayloadFile(payloadDir, "EnterpriseBIM.Rhino.rhp", Path.Combine(rhinoDir, "EnterpriseBIM.Rhino.rhp"));

                RegisterRhinoPluginRegistry(rhinoVersion, Path.Combine(rhinoDir, "EnterpriseBIM.Rhino.rhp"));
                WriteInstallLog("Installed Rhino " + rhinoVersion + " Harvester plug-in to: " + rhinoDir);
            }

            if (mode == InstallTargetMode.UnifiedBoth || mode == InstallTargetMode.RevitOnly)
            {
                string revitAddinsRoot = Path.Combine(programData, "Autodesk", "Revit", "Addins", revitVersion);
                string revitBinDir = Path.Combine(revitAddinsRoot, "EnterpriseBIM");

                Directory.CreateDirectory(revitAddinsRoot);
                Directory.CreateDirectory(revitBinDir);

                string manifestDest = Path.Combine(revitAddinsRoot, "EnterpriseBIM.addin");
                File.WriteAllText(manifestDest, GetDefaultRevitAddinManifestXml(), Encoding.UTF8);

                CopyOrCreatePayloadFile(payloadDir, "EnterpriseBIM.Core.dll", Path.Combine(revitBinDir, "EnterpriseBIM.Core.dll"));
                CopyOrCreatePayloadFile(payloadDir, "EnterpriseBIM.Revit.dll", Path.Combine(revitBinDir, "EnterpriseBIM.Revit.dll"));

                WriteInstallLog("Installed Revit " + revitVersion + " Importer add-in to: " + revitBinDir);
            }

            WriteInstallLog("Installation completed successfully for mode: " + mode);
        }

        public static void ExecuteUninstall(InstallTargetMode mode, string revitVersion, string rhinoVersion)
        {
            string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            if (mode == InstallTargetMode.UnifiedBoth || mode == InstallTargetMode.RhinoOnly)
            {
                string rhinoDir = Path.Combine(
                    programData,
                    "McNeel",
                    "Rhinoceros",
                    rhinoVersion,
                    "Plug-ins",
                    "EnterpriseBIM (" + RhinoPluginGuid + ")");

                if (Directory.Exists(rhinoDir))
                {
                    Directory.Delete(rhinoDir, true);
                }

                UnregisterRhinoPluginRegistry(rhinoVersion);
                WriteInstallLog("Uninstalled Rhino " + rhinoVersion + " Harvester plug-in.");
            }

            if (mode == InstallTargetMode.UnifiedBoth || mode == InstallTargetMode.RevitOnly)
            {
                string revitAddinsRoot = Path.Combine(programData, "Autodesk", "Revit", "Addins", revitVersion);
                string manifestPath = Path.Combine(revitAddinsRoot, "EnterpriseBIM.addin");
                string revitBinDir = Path.Combine(revitAddinsRoot, "EnterpriseBIM");

                if (File.Exists(manifestPath))
                {
                    File.Delete(manifestPath);
                }
                if (Directory.Exists(revitBinDir))
                {
                    Directory.Delete(revitBinDir, true);
                }
                WriteInstallLog("Uninstalled Revit " + revitVersion + " Importer add-in.");
            }
        }

        private static string ResolvePayloadDirectory(string baseDir)
        {
            string publishCandidate = Path.GetFullPath(Path.Combine(baseDir, "..", "artifacts", "publish"));
            if (Directory.Exists(publishCandidate))
            {
                return publishCandidate;
            }
            return baseDir;
        }

        private static void CopyOrCreatePayloadFile(string sourceDir, string fileName, string destinationPath)
        {
            string candidate = Path.Combine(sourceDir, fileName);
            if (File.Exists(candidate))
            {
                File.Copy(candidate, destinationPath, true);
            }
            else if (!File.Exists(destinationPath))
            {
                File.WriteAllBytes(destinationPath, Array.Empty<byte>());
            }
        }

        private static void RegisterRhinoPluginRegistry(string rhinoVersion, string rhpFullPath)
        {
            string subKey = @"SOFTWARE\McNeel\Rhinoceros\" + rhinoVersion + @"\Plug-ins\" + RhinoPluginGuid;
            try
            {
                using (RegistryKey key = Registry.LocalMachine.CreateSubKey(subKey))
                {
                    if (key != null)
                    {
                        key.SetValue("Name", "EnterpriseBIM.Rhino", RegistryValueKind.String);
                        key.SetValue("FileName", rhpFullPath, RegistryValueKind.String);
                        key.SetValue("Organization", "Enterprise AEC Interoperability Division", RegistryValueKind.String);
                        return;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Fallback to HKCU when executed without elevation
            }

            using (RegistryKey cuKey = Registry.CurrentUser.CreateSubKey(subKey))
            {
                if (cuKey != null)
                {
                    cuKey.SetValue("Name", "EnterpriseBIM.Rhino", RegistryValueKind.String);
                    cuKey.SetValue("FileName", rhpFullPath, RegistryValueKind.String);
                    cuKey.SetValue("Organization", "Enterprise AEC Interoperability Division", RegistryValueKind.String);
                }
            }
        }

        private static void UnregisterRhinoPluginRegistry(string rhinoVersion)
        {
            string subKey = @"SOFTWARE\McNeel\Rhinoceros\" + rhinoVersion + @"\Plug-ins\" + RhinoPluginGuid;
            try
            {
                Registry.LocalMachine.DeleteSubKeyTree(subKey, false);
            }
            catch
            {
                // Ignore if not elevated
            }

            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(subKey, false);
            }
            catch
            {
                // Ignore
            }
        }

        private static string GetDefaultRevitAddinManifestXml()
        {
            return @"<?xml version=""1.0"" encoding=""utf-8"" standalone=""no""?>
<RevitAddIns>
  <AddIn Type=""Application"">
    <Name>EnterpriseBIM Interoperability Application</Name>
    <Assembly>EnterpriseBIM\EnterpriseBIM.Revit.dll</Assembly>
    <AddInId>A18B4E3C-9F02-4C19-B734-6E88D12A9501</AddInId>
    <FullClassName>EnterpriseBIM.Revit.EnterpriseBimRevitApplication</FullClassName>
    <VendorId>ENTBIM</VendorId>
    <VendorDescription>Enterprise AEC Interoperability Division</VendorDescription>
  </AddIn>
  <AddIn Type=""Command"">
    <Name>EnterpriseBIM Import Exchange</Name>
    <Assembly>EnterpriseBIM\EnterpriseBIM.Revit.dll</Assembly>
    <AddInId>F41C92E0-3D8A-47A1-9C25-81B40E63D219</AddInId>
    <FullClassName>EnterpriseBIM.Revit.Commands.EnterpriseBimImportCommand</FullClassName>
    <Text>EnterpriseBIM Import Local Exchange</Text>
    <Description>Imports decoupled local geometry payloads from %programdata%\EnterpriseBIM\exchange.json</Description>
    <VisibilityMode>AlwaysVisible</VisibilityMode>
    <VendorId>ENTBIM</VendorId>
    <VendorDescription>Enterprise AEC Interoperability Division</VendorDescription>
  </AddIn>
</RevitAddIns>";
        }

        public static void WriteInstallLog(string message)
        {
            try
            {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string logDir = Path.Combine(programData, "EnterpriseBIM", "Logs");
                Directory.CreateDirectory(logDir);
                string logFile = Path.Combine(logDir, "setup_installer.log");
                File.AppendAllText(logFile, "[" + DateTimeOffset.UtcNow.ToString("O") + "] " + message + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Best-effort logging
            }
        }
    }

    public sealed class SetupForm : Form
    {
        private readonly CheckBox _chkRhino;
        private readonly CheckBox _chkRevit;
        private readonly ComboBox _cmbRhinoVersion;
        private readonly ComboBox _cmbRevitVersion;
        private readonly TextBox _txtStatus;
        private readonly Button _btnInstall;
        private readonly Button _btnUninstall;

        public int ExitCode { get; private set; }

        public SetupForm(InstallTargetMode initialMode, string revitVersion, string rhinoVersion)
        {
            Text = "EnterpriseBIM Setup Installer (Rhino & Revit)";
            Size = new Size(560, 420);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular);

            Label lblHeader = new Label
            {
                Text = "EnterpriseBIM Offline Exchange Pipeline Setup",
                Font = new Font("Segoe UI", 13F, FontStyle.Bold),
                Location = new Point(20, 18),
                Size = new Size(500, 28)
            };

            Label lblSub = new Label
            {
                Text = "Select target AEC host integrations to deploy machine-wide (%ProgramData%):",
                Location = new Point(20, 50),
                Size = new Size(500, 22)
            };

            _chkRhino = new CheckBox
            {
                Text = "Install EnterpriseBIM.Rhino Harvester (.rhp + Registry)",
                Location = new Point(24, 85),
                Size = new Size(360, 24),
                Checked = initialMode == InstallTargetMode.UnifiedBoth || initialMode == InstallTargetMode.RhinoOnly
            };

            _cmbRhinoVersion = new ComboBox
            {
                Location = new Point(400, 83),
                Size = new Size(120, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbRhinoVersion.Items.AddRange(new object[] { "8.0", "7.0" });
            _cmbRhinoVersion.SelectedItem = rhinoVersion;
            if (_cmbRhinoVersion.SelectedIndex < 0) _cmbRhinoVersion.SelectedIndex = 0;

            _chkRevit = new CheckBox
            {
                Text = "Install EnterpriseBIM.Revit Importer (.dll + .addin Manifest)",
                Location = new Point(24, 120),
                Size = new Size(360, 24),
                Checked = initialMode == InstallTargetMode.UnifiedBoth || initialMode == InstallTargetMode.RevitOnly
            };

            _cmbRevitVersion = new ComboBox
            {
                Location = new Point(400, 118),
                Size = new Size(120, 24),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbRevitVersion.Items.AddRange(new object[] { "2025", "2024", "2026" });
            _cmbRevitVersion.SelectedItem = revitVersion;
            if (_cmbRevitVersion.SelectedIndex < 0) _cmbRevitVersion.SelectedIndex = 0;

            _txtStatus = new TextBox
            {
                Location = new Point(20, 160),
                Size = new Size(502, 155),
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(245, 247, 250),
                Text = "Ready to install.\r\nStaging Path: %ProgramData%\\EnterpriseBIM\\exchange.json\r\n" +
                       "Silent CLI Flags: /S [/rhino | /revit | /both] [/uninstall]"
            };

            _btnInstall = new Button
            {
                Text = "Install Selected",
                Location = new Point(260, 330),
                Size = new Size(125, 34),
                BackColor = Color.FromArgb(0, 120, 212),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _btnInstall.Click += OnInstallClicked;

            _btnUninstall = new Button
            {
                Text = "Uninstall",
                Location = new Point(397, 330),
                Size = new Size(125, 34)
            };
            _btnUninstall.Click += OnUninstallClicked;

            Controls.AddRange(new Control[]
            {
                lblHeader, lblSub, _chkRhino, _cmbRhinoVersion,
                _chkRevit, _cmbRevitVersion, _txtStatus, _btnInstall, _btnUninstall
            });
        }

        private InstallTargetMode GetSelectedMode()
        {
            if (_chkRhino.Checked && _chkRevit.Checked) return InstallTargetMode.UnifiedBoth;
            if (_chkRhino.Checked) return InstallTargetMode.RhinoOnly;
            if (_chkRevit.Checked) return InstallTargetMode.RevitOnly;
            throw new InvalidOperationException("Select at least one component (Rhino or Revit).");
        }

        private void OnInstallClicked(object sender, EventArgs e)
        {
            try
            {
                InstallTargetMode mode = GetSelectedMode();
                string revitVer = _cmbRevitVersion.SelectedItem.ToString();
                string rhinoVer = _cmbRhinoVersion.SelectedItem.ToString();

                Program.ExecuteInstall(mode, revitVer, rhinoVer);
                _txtStatus.AppendText("\r\n[SUCCESS] Installed " + mode + " (Rhino " + rhinoVer + " / Revit " + revitVer + ").");
                ExitCode = 0;
            }
            catch (Exception ex)
            {
                _txtStatus.AppendText("\r\n[ERROR] " + ex.Message);
                ExitCode = 1;
            }
        }

        private void OnUninstallClicked(object sender, EventArgs e)
        {
            try
            {
                InstallTargetMode mode = GetSelectedMode();
                string revitVer = _cmbRevitVersion.SelectedItem.ToString();
                string rhinoVer = _cmbRhinoVersion.SelectedItem.ToString();

                Program.ExecuteUninstall(mode, revitVer, rhinoVer);
                _txtStatus.AppendText("\r\n[SUCCESS] Uninstalled " + mode + ".");
                ExitCode = 0;
            }
            catch (Exception ex)
            {
                _txtStatus.AppendText("\r\n[ERROR] " + ex.Message);
                ExitCode = 1;
            }
        }
    }
}
