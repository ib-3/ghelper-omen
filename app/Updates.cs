using GHelper.UI;
using System.Diagnostics;
using System.Management;
using System.Net;
using System.Text.Json;

namespace GHelper
{

    public partial class Updates : RForm
    {
        const int DRIVER_NOT_FOUND = 2;
        const int DRIVER_NEWER = 1;

        const string SYMBOL_UPDATED = "•";
        const string SYMBOL_NEW = "⬤";

        //static int rowCount = 0;
        static string bios;
        static string model;

        static int updatesCount = 0;
        private static long lastUpdate;

        private readonly Font _boldUnderlineFont;
        private readonly Font _font;
        private CancellationTokenSource _cts = new();

        private static readonly HttpClient _httpClient = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All
            });
            client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
            client.DefaultRequestHeaders.Add("User-Agent", "C# App");
            return client;
        }

        public struct DriverDownload
        {
            public string categoryName;
            public string title;
            public string version;
            public string downloadUrl;
            public string date;
            public JsonElement hardwares;
        }
        private void LoadUpdates(bool force = false)
        {

            if (!force && (Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastUpdate) < 5000)) return;
            lastUpdate = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            (bios, model) = AppConfig.GetBiosAndModel();

            buttonRefresh.TabStop = false;

            updatesCount = 0;
            labelUpdates.ForeColor = colorEco;
            labelUpdates.Text = Properties.Strings.NoNewUpdates;

            panelBios.AccessibleRole = AccessibleRole.Grouping;
            panelBios.AccessibleName = Properties.Strings.NoNewUpdates;
            panelBios.TabStop = true;

            Text = Properties.Strings.BiosAndDriverUpdates + ": " + model + " " + bios;
            labelBIOS.Text = "BIOS";
            labelDrivers.Text = Properties.Strings.DriverAndSoftware;

            labelLegend.Text = Properties.Strings.Legend;
            labelLegendGray.Text = Properties.Strings.LegendGray;
            labelLegendRed.Text = SYMBOL_NEW + " " + Properties.Strings.LegendRed;
            labelLegendGreen.Text = SYMBOL_UPDATED + " " + Properties.Strings.LegendGreen;

            SuspendLayout();

            tableBios.Visible = false;
            tableDrivers.Visible = false;

            labelLegendGreen.BackColor = colorEco;
            labelLegendRed.BackColor = colorTurbo;

            ClearTable(tableBios);
            ClearTable(tableDrivers);

            string rogParam = AppConfig.IsROG() ? "&systemCode=rog" : "";

            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            if (AppConfig.IsOmen())
            {
                _ = Task.Run(() => HPDriversAsync(tableBios, tableDrivers, token), token);
            }
            else
            {
                AddGenericBios(tableBios);
                AddGenericDrivers(tableDrivers);
            }
            _ = Task.Run(LaptopSerialNumber, token);

            textSerial.BackColor = panelBios.BackColor;
            textSerial.ForeColor = panelBios.ForeColor;
        }

        private void AddGenericDrivers(TableLayoutPanel table)
        {
            var nvidia = new DriverDownload { categoryName = "Graphics", title = "NVIDIA GeForce Drivers", version = "Latest", date = "Check Website", downloadUrl = "https://www.nvidia.com/download/index.aspx" };
            var intel = new DriverDownload { categoryName = "Graphics", title = "Intel Arc & Iris Xe Graphics", version = "Latest", date = "Check Website", downloadUrl = "https://www.intel.com/content/www/us/en/download-center/home.html" };
            var amdChipset = new DriverDownload { categoryName = "Chipset", title = "AMD Ryzen Chipset Drivers", version = "Latest", date = "Check Website", downloadUrl = "https://www.amd.com/en/support" };
            var intelChipset = new DriverDownload { categoryName = "Chipset", title = "Intel Chipset Drivers", version = "Latest", date = "Check Website", downloadUrl = "https://www.intel.com/content/www/us/en/download-center/home.html" };

            VisualiseDriver(nvidia, table);
            VisualiseDriver(intel, table);
            VisualiseDriver(amdChipset, table);
            VisualiseDriver(intelChipset, table);
            ShowTable(table);
        }

        private void AddGenericBios(TableLayoutPanel table)
        {
            var hp = new DriverDownload { categoryName = "System", title = "HP OMEN Official Drivers & BIOS", version = "Latest", date = "Check Website", downloadUrl = "https://support.hp.com/us-en/drivers/laptops" };
            VisualiseDriver(hp, table);
            ShowTable(table);
        }

        private void ClearTable(TableLayoutPanel tableLayoutPanel)
        {
            while (tableLayoutPanel.Controls.Count > 0)
            {
                tableLayoutPanel.Controls[0].Dispose();
            }

            tableLayoutPanel.RowCount = 0;
            tableLayoutPanel.RowStyles.Clear();
        }

        public Updates()
        {
            InitializeComponent();
            InitTheme(true);

            _boldUnderlineFont = new Font(Font, FontStyle.Bold | FontStyle.Underline);
            _font = new Font(Font, FontStyle.Underline);

            //buttonRefresh.Visible = false;
            buttonRefresh.Click += ButtonRefresh_Click;
            Shown += Updates_Shown;
            Resize += (s, e) => AlignLabelUpdates();

            FormClosed += (s, e) =>
            {
                _cts.Cancel();
                _cts.Dispose();
                // Dispose fonts when form closes
                _boldUnderlineFont.Dispose();
                _font.Dispose();
            };
        }

        private void ButtonRefresh_Click(object? sender, EventArgs e)
        {
            LoadUpdates();
        }

        private void AlignLabelUpdates()
        {
            int dateColumnLeft = panelBios.Padding.Left + (int)(0.63 * (tableBios.Width - 44)) + 10;
            labelUpdates.Left = dateColumnLeft;
        }

        private void Updates_Shown(object? sender, EventArgs e)
        {
            Height = Program.settingsForm.Height;
            Top = Program.settingsForm.Top;
            Left = Program.settingsForm.Left - Width - 5;
            AlignLabelUpdates();
            LoadUpdates(true);
        }

        public void LaptopSerialNumber()
        {
            try
            {
                string serial = string.Empty;
                using var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BIOS");
                using var collection = searcher.Get();
                foreach (ManagementObject obj in collection)
                {
                    using (obj)
                    {
                        serial = obj["SerialNumber"]?.ToString()?.Trim() ?? string.Empty;
                    }
                    break;
                }
                if (!IsDisposed) Invoke(() => textSerial.Text = serial);
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.ToString());
            }
        }

        private Dictionary<string, string> GetDeviceVersions()
        {
            using (ManagementObjectSearcher objSearcher = new ManagementObjectSearcher("Select * from Win32_PnPSignedDriver"))
            {
                using (ManagementObjectCollection objCollection = objSearcher.Get())
                {
                    Dictionary<string, string> list = new();

                    foreach (ManagementObject obj in objCollection) using (obj) if (obj["DriverVersion"] is not null)
                            {
                                if (obj["DeviceID"] is not null)
                                {
                                    list[obj["DeviceID"].ToString()] = obj["DriverVersion"].ToString();
                                }
                                if (obj["DeviceName"] is not null)
                                {
                                    var deviceName = obj["DeviceName"].ToString();
                                    if (deviceName.Contains("DolbyAPO SWC")) list["Dolby"] = obj["DriverVersion"].ToString();
                                    if (deviceName.Contains("Fortemedia Audio")) list["Fortemedia"] = obj["DriverVersion"].ToString();
                                }
                            }
                    return list;
                }
            }
        }


        private void _VisualiseDriver(DriverDownload driver, TableLayoutPanel table)
        {
            string versionText = driver.version.Replace("latest version at the ", "");
            LinkLabel versionLabel = new LinkLabel { Text = versionText, Anchor = AnchorStyles.Left, AutoSize = true };

            versionLabel.AccessibleName = driver.title;
            versionLabel.TabStop = true;
            versionLabel.TabIndex = table.RowCount + 1;

            versionLabel.Cursor = Cursors.Hand;
            versionLabel.Font = _font;
            versionLabel.LinkColor = colorEco;
            versionLabel.Padding = new Padding(0, 5, 5, 5);
            versionLabel.LinkClicked += delegate
            {
                Process.Start(new ProcessStartInfo(driver.downloadUrl) { UseShellExecute = true });
            };

            var symbolLabel = new Label
            {
                Text = "",
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                Padding = new Padding(0, 5, 4, 5),
            };

            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = driver.categoryName, Anchor = AnchorStyles.Left, Dock = DockStyle.Fill, Padding = new Padding(5, 5, 5, 5) }, 0, table.RowCount);
            table.Controls.Add(new Label { Text = driver.title, Anchor = AnchorStyles.Left, Dock = DockStyle.Fill, Padding = new Padding(5, 5, 5, 5) }, 1, table.RowCount);
            table.Controls.Add(new Label { Text = driver.date, Anchor = AnchorStyles.Left, Dock = DockStyle.Fill, Padding = new Padding(5, 5, 5, 5) }, 2, table.RowCount);
            table.Controls.Add(symbolLabel, 3, table.RowCount);
            table.Controls.Add(versionLabel, 4, table.RowCount);
            table.RowCount++;
        }

        public void VisualiseDriver(DriverDownload driver, TableLayoutPanel table)
        {
            if (InvokeRequired)
            {
                Invoke(delegate
                {
                    _VisualiseDriver(driver, table);
                });
            }
            else
            {
                _VisualiseDriver(driver, table);
            }
        }

        public void ShowTable(TableLayoutPanel table)
        {
            Invoke(delegate
            {
                table.Visible = true;
                ResumeLayout(false);
                PerformLayout();
            });
        }

        private void _VisualiseNewDriver(int position, int newer, string tip, TableLayoutPanel table)
        {
            var symbolLabel = table.GetControlFromPosition(3, position) as Label;
            var label = table.GetControlFromPosition(4, position) as LinkLabel;
            if (label == null) return;

            toolTip.SetToolTip(label, tip);

            if (newer == DRIVER_NEWER)
            {
                label.AccessibleName = label.AccessibleName + Properties.Strings.NewUpdates;
                label.Font = _boldUnderlineFont;
                label.LinkColor = colorTurbo;
                if (symbolLabel != null)
                {
                    symbolLabel.Text = SYMBOL_NEW;
                    symbolLabel.ForeColor = colorTurbo;
                }
            }
            else if (newer == DRIVER_NOT_FOUND)
            {
                label.LinkColor = Color.Gray;
            }
            else if (symbolLabel != null)
            {
                symbolLabel.Text = SYMBOL_UPDATED;
                symbolLabel.ForeColor = colorEco;
            }
        }

        public void VisualiseNewDriver(int position, int newer, string tip, TableLayoutPanel table)
        {
            if (InvokeRequired)
            {
                Invoke(delegate
                {
                    _VisualiseNewDriver(position, newer, tip, table);
                });
            }
            else
            {
                _VisualiseNewDriver(position, newer, tip, table);
            }
        }

        public void VisualiseNewCount(int updatesCount, TableLayoutPanel table)
        {
            if (InvokeRequired)
            {
                Invoke(delegate
                {
                    _VisualiseNewCount(updatesCount, table);
                });
            }
            else
            {
                _VisualiseNewCount(updatesCount, table);
            }
        }

        public void _VisualiseNewCount(int updatesCount, TableLayoutPanel table)
        {
            labelUpdates.Text = $"{Properties.Strings.NewUpdates}: {updatesCount}";
            labelUpdates.ForeColor = colorTurbo;
            labelUpdates.Font = _boldUnderlineFont;
            panelBios.AccessibleName = labelUpdates.Text;
        }

        static string CleanupDeviceId(string input)
        {
            int index = input.IndexOf("&REV_");
            if (index != -1)
            {
                return input.Substring(0, index);
            }
            return input;
        }

        public async Task DriversAsync(string url, int type, TableLayoutPanel table, CancellationToken token = default)
        {
            try
            {
                Logger.WriteLine(url);
                var json = await _httpClient.GetStringAsync(url, token);

                var data = JsonSerializer.Deserialize<JsonElement>(json);
                var result = data.GetProperty("Result");

                // fallback for bugged API
                if (result.ToString() == "" || result.GetProperty("Obj").GetArrayLength() == 0)
                {
                    var urlFallback = url + "&tag=" + new Random().Next(10, 99);
                    Logger.WriteLine(urlFallback);
                    json = await _httpClient.GetStringAsync(urlFallback, token);
                    data = JsonSerializer.Deserialize<JsonElement>(json);
                }

                var groups = data.GetProperty("Result").GetProperty("Obj");


                List<string> skipList = new() { "Armoury Crate & Aura Creator Installer", "MyASUS", "ASUS Smart Display Control", "Aura Wallpaper", "Virtual Pet", "Virtual Pet- Ultimate Edition", "ROG Font V1.5", "Armoury Crate Control Interface", "Virtual Assistant" };
                List<DriverDownload> drivers = new();

                for (int i = 0; i < groups.GetArrayLength(); i++)
                {
                    token.ThrowIfCancellationRequested();

                    var categoryName = groups[i].GetProperty("Name").ToString();
                    var files = groups[i].GetProperty("Files");

                    var oldTitle = "";

                    for (int j = 0; j < files.GetArrayLength(); j++)
                    {

                        var file = files[j];
                        var title = file.GetProperty("Title").ToString();

                        if (oldTitle != title && !skipList.Contains(title))
                        {

                            var driver = new DriverDownload();
                            driver.categoryName = categoryName;
                            driver.title = title;
                            driver.version = file.GetProperty("Version").ToString().Replace("V", "");
                            driver.downloadUrl = file.GetProperty("DownloadUrl").GetProperty("Global").ToString();
                            driver.hardwares = file.GetProperty("HardwareInfoList");
                            driver.date = file.GetProperty("ReleaseDate").ToString();
                            drivers.Add(driver);

                            VisualiseDriver(driver, table);
                        }

                        oldTitle = title;
                    }
                }

                ShowTable(table);


                Dictionary<string, string> devices = new();
                if (type == 0) devices = GetDeviceVersions();

                int count = 0;
                foreach (var driver in drivers)
                {
                    token.ThrowIfCancellationRequested();

                    int newer = DRIVER_NOT_FOUND;
                    string tip = driver.version;

                    if (type == 0 && driver.hardwares.GetArrayLength() > 0)
                        for (int k = 0; k < driver.hardwares.GetArrayLength(); k++)
                        {
                            var deviceID = driver.hardwares[k].GetProperty("hardwareid").ToString();
                            deviceID = CleanupDeviceId(deviceID);
                            var localVersions = devices.Where(p => p.Key.Contains(deviceID, StringComparison.CurrentCultureIgnoreCase)).Select(p => p.Value);
                            foreach (var localVersion in localVersions)
                            {
                                newer = Math.Min(newer, new Version(driver.version).CompareTo(new Version(localVersion)));
                                Logger.WriteLine(driver.title + " " + deviceID + " " + driver.version + " vs " + localVersion + " = " + newer);
                                tip = "Download: " + driver.version + "\n" + "Installed: " + localVersion;
                            }
                        }

                    if (type == 1 && !driver.title.Contains("Firmware"))
                    {
                        newer = Int32.Parse(driver.version) > Int32.Parse(bios) ? 1 : -1;
                        tip = "Download: " + driver.version + "\n" + "Installed: " + bios;
                    }

                    VisualiseNewDriver(count, newer, tip, table);

                    if (newer == DRIVER_NEWER)
                    {
                        updatesCount++;
                        VisualiseNewCount(updatesCount, table);
                    }

                    count++;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.ToString());
            }
        }

        #region HP OMEN Drivers Integration

        public class HPTypeaheadResponse
        {
            public List<HPTypeaheadMatch>? matches { get; set; }
        }

        public class HPTypeaheadMatch
        {
            public long productId { get; set; }
            public string? productname { get; set; }
        }

        public class HPOSVersionResponse
        {
            public HPOSVersionData? data { get; set; }
        }

        public class HPOSVersionData
        {
            public List<HPOSVersionGroup>? osversions { get; set; }
        }

        public class HPOSVersionGroup
        {
            public string? name { get; set; }
            public List<HPOSVersionItem>? osVersionList { get; set; }
        }

        public class HPOSVersionItem
        {
            public string? id { get; set; }
            public string? name { get; set; }
        }

        public class HPDriverDetailsResponse
        {
            public HPDriverDetailsData? data { get; set; }
        }

        public class HPDriverDetailsData
        {
            public List<HPSoftwareType>? softwareTypes { get; set; }
        }

        public class HPSoftwareType
        {
            public string? accordionNameEn { get; set; }
            public List<HPSoftwareDriver>? softwareDriversList { get; set; }
        }

        public class HPSoftwareDriver
        {
            public HPLatestVersionDriver? latestVersionDriver { get; set; }
        }

        public class HPLatestVersionDriver
        {
            public string? title { get; set; }
            public string? version { get; set; }
            public string? fileUrl { get; set; }
            public string? releaseDateString { get; set; }
            public string? fileSize { get; set; }
        }

        private static string GetSKUNumber()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT SystemSKUNumber FROM Win32_ComputerSystem");
                using var collection = searcher.Get();
                foreach (ManagementObject obj in collection)
                {
                    using (obj)
                    {
                        string? sku = obj["SystemSKUNumber"]?.ToString();
                        if (!string.IsNullOrEmpty(sku))
                        {
                            int hashIndex = sku.IndexOf('#');
                            if (hashIndex != -1)
                            {
                                sku = sku.Substring(0, hashIndex);
                            }
                            return sku.Trim();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.ToString());
            }
            return string.Empty;
        }

        private Dictionary<string, string> GetDeviceNamesAndVersions()
        {
            using (ManagementObjectSearcher objSearcher = new ManagementObjectSearcher("Select DeviceName, DriverVersion from Win32_PnPSignedDriver"))
            {
                using (ManagementObjectCollection objCollection = objSearcher.Get())
                {
                    Dictionary<string, string> list = new(StringComparer.OrdinalIgnoreCase);
                    foreach (ManagementObject obj in objCollection) using (obj)
                    {
                        if (obj["DeviceName"] is not null && obj["DriverVersion"] is not null)
                        {
                            list[obj["DeviceName"].ToString()] = obj["DriverVersion"].ToString();
                        }
                    }
                    return list;
                }
            }
        }

        private static string CleanVersion(string version)
        {
            if (string.IsNullOrEmpty(version)) return "0.0";

            int revIndex = version.IndexOf("Rev", StringComparison.OrdinalIgnoreCase);
            if (revIndex != -1)
            {
                version = version.Substring(0, revIndex).Trim();
            }

            var biosMatch = System.Text.RegularExpressions.Regex.Match(version, @"^[A-Za-z]+\.(\d+)$");
            if (biosMatch.Success)
            {
                return biosMatch.Groups[1].Value + ".0";
            }

            var cleanMatch = System.Text.RegularExpressions.Regex.Match(version, @"^\d+(\.\d+)*");
            if (cleanMatch.Success)
            {
                string val = cleanMatch.Value;
                if (!val.Contains('.'))
                {
                    val += ".0";
                }
                return val;
            }

            return "0.0";
        }

        private static string? FindLocalVersion(string title, Dictionary<string, string> localDevices)
        {
            string t = title.ToLowerInvariant();
            foreach (var pair in localDevices)
            {
                string deviceName = pair.Key.ToLowerInvariant();
                string version = pair.Value;

                if (t.Contains("nvidia") && t.Contains("graphics") && deviceName.Contains("nvidia") && (deviceName.Contains("geforce") || deviceName.Contains("quadro") || deviceName.Contains("rtx") || deviceName.Contains("gtx")))
                {
                    return version;
                }
                if (t.Contains("intel") && t.Contains("graphics") && deviceName.Contains("intel") && (deviceName.Contains("arc") || deviceName.Contains("iris") || deviceName.Contains("hd graphics") || deviceName.Contains("uhd")))
                {
                    return version;
                }
                if (t.Contains("realtek") && t.Contains("audio") && deviceName.Contains("realtek") && deviceName.Contains("audio") && !deviceName.Contains("effects") && !deviceName.Contains("universal"))
                {
                    return version;
                }
                if (t.Contains("intel") && t.Contains("bluetooth") && deviceName.Contains("intel") && deviceName.Contains("bluetooth"))
                {
                    return version;
                }
                if (t.Contains("intel") && (t.Contains("wireless lan") || t.Contains("wi-fi") || t.Contains("wifi")) && deviceName.Contains("intel") && (deviceName.Contains("wi-fi") || deviceName.Contains("wireless") || deviceName.Contains("dual band")))
                {
                    return version;
                }
                if (t.Contains("realtek") && (t.Contains("local area network") || t.Contains("lan")) && deviceName.Contains("realtek") && (deviceName.Contains("gbe") || deviceName.Contains("pcie") || deviceName.Contains("ethernet")))
                {
                    return version;
                }
                if (t.Contains("intel") && (t.Contains("gna") || t.Contains("gaussian")) && deviceName.Contains("intel") && (deviceName.Contains("gna") || deviceName.Contains("gaussian")))
                {
                    return version;
                }
                if (t.Contains("synaptics") && t.Contains("touchpad") && deviceName.Contains("synaptics") && deviceName.Contains("touchpad"))
                {
                    return version;
                }
                if (t.Contains("elan") && t.Contains("touchpad") && deviceName.Contains("elan") && deviceName.Contains("touchpad"))
                {
                    return version;
                }
                if (t.Contains("intel") && t.Contains("serial io") && deviceName.Contains("intel") && deviceName.Contains("serial io"))
                {
                    return version;
                }

                if (t.Contains("intel") && deviceName.Contains("intel"))
                {
                    string key = t.Replace("intel", "").Trim();
                    if (!string.IsNullOrEmpty(key) && deviceName.Contains(key))
                    {
                        return version;
                    }
                }
            }
            return null;
        }

        public async Task HPDriversAsync(TableLayoutPanel tableBios, TableLayoutPanel tableDrivers, CancellationToken token = default)
        {
            try
            {
                string sku = GetSKUNumber();
                if (string.IsNullOrEmpty(sku))
                {
                    Logger.WriteLine("HP Update: SKU number not found in WMI.");
                    return;
                }
                Logger.WriteLine($"HP Update SKU: {sku}");

                string typeaheadUrl = $"https://support.hp.com/typeahead?q={sku}&cc=us&lc=en";
                var typeaheadJson = await _httpClient.GetStringAsync(typeaheadUrl, token);
                var typeaheadData = JsonSerializer.Deserialize<HPTypeaheadResponse>(typeaheadJson);
                if (typeaheadData?.matches == null || typeaheadData.matches.Count == 0)
                {
                    Logger.WriteLine("HP Update: No product matches found in typeahead API.");
                    return;
                }
                long productOid = typeaheadData.matches[0].productId;
                Logger.WriteLine($"HP Update productOid: {productOid}");

                string osUrl = $"https://support.hp.com/wcc-services/swd-v2/osVersionData?cc=us&lc=en&productOid={productOid}";
                var osJson = await _httpClient.GetStringAsync(osUrl, token);
                var osData = JsonSerializer.Deserialize<HPOSVersionResponse>(osJson);
                if (osData?.data?.osversions == null)
                {
                    Logger.WriteLine("HP Update: Failed to retrieve OS list.");
                    return;
                }

                string targetOsName = "Windows 11";
                if (System.Environment.OSVersion.Version.Major == 10 && System.Environment.OSVersion.Version.Build < 22000)
                {
                    targetOsName = "Windows 10";
                }

                string? osTmsId = null;
                string? osNameSelected = null;

                foreach (var group in osData.data.osversions)
                {
                    if (group.name != null && group.name.Contains(targetOsName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (group.osVersionList != null && group.osVersionList.Count > 0)
                        {
                            var firstVer = group.osVersionList[0];
                            osTmsId = firstVer.id;
                            osNameSelected = firstVer.name;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(osTmsId))
                {
                    Logger.WriteLine($"HP Update: Target OS {targetOsName} not found in OS list.");
                    return;
                }
                Logger.WriteLine($"HP Update OS Selected: {osNameSelected} (ID: {osTmsId})");

                var request = new HttpRequestMessage(HttpMethod.Post, "https://support.hp.com/wcc-services/swd-v2/driverDetails");
                request.Headers.Add("User-Agent", "Mozilla/5.0");
                request.Headers.Referrer = new Uri("https://support.hp.com/");

                var payload = new
                {
                    lc = "en",
                    cc = "us",
                    osTMSId = osTmsId,
                    osName = targetOsName,
                    productSeriesOid = productOid
                };
                string jsonPayload = JsonSerializer.Serialize(payload);
                request.Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request, token);
                var jsonResponse = await response.Content.ReadAsStringAsync(token);

                var driverDetails = JsonSerializer.Deserialize<HPDriverDetailsResponse>(jsonResponse);
                if (driverDetails?.data?.softwareTypes == null)
                {
                    Logger.WriteLine("HP Update: Failed to deserialize driver details or no types returned.");
                    return;
                }

                var localDevices = GetDeviceNamesAndVersions();

                List<DriverDownload> biosList = new();
                List<DriverDownload> driverList = new();

                foreach (var type in driverDetails.data.softwareTypes)
                {
                    if (type.softwareDriversList == null) continue;
                    
                    bool isBiosType = type.accordionNameEn != null && type.accordionNameEn.Equals("BIOS", StringComparison.OrdinalIgnoreCase);

                    foreach (var entry in type.softwareDriversList)
                    {
                        if (entry.latestVersionDriver == null) continue;
                        
                        var item = entry.latestVersionDriver;
                        if (string.IsNullOrEmpty(item.title) || string.IsNullOrEmpty(item.version)) continue;

                        var driver = new DriverDownload
                        {
                            categoryName = type.accordionNameEn ?? "Software",
                            title = item.title,
                            version = item.version,
                            downloadUrl = item.fileUrl ?? "https://support.hp.com/us-en/drivers/laptops",
                            date = item.releaseDateString ?? "Check Website"
                        };

                        if (isBiosType)
                        {
                            biosList.Add(driver);
                            VisualiseDriver(driver, tableBios);
                        }
                        else
                        {
                            driverList.Add(driver);
                            VisualiseDriver(driver, tableDrivers);
                        }
                    }
                }

                ShowTable(tableBios);
                ShowTable(tableDrivers);

                int updatesCountLocal = 0;

                int biosCount = 0;
                foreach (var driver in biosList)
                {
                    token.ThrowIfCancellationRequested();
                    int newer = DRIVER_NOT_FOUND;
                    string tip = driver.version;

                    if (!string.IsNullOrEmpty(bios))
                    {
                        try
                        {
                            string localClean = CleanVersion(bios);
                            string remoteClean = CleanVersion(driver.version);
                            int compare = new Version(remoteClean).CompareTo(new Version(localClean));
                            newer = compare > 0 ? DRIVER_NEWER : -1;
                            tip = $"Download: {driver.version}\nInstalled: {bios}";
                        }
                        catch (Exception ex)
                        {
                            Logger.WriteLine($"HP BIOS version comparison error: {ex.Message}");
                        }
                    }

                    VisualiseNewDriver(biosCount, newer, tip, tableBios);
                    if (newer == DRIVER_NEWER)
                    {
                        updatesCountLocal++;
                        VisualiseNewCount(updatesCountLocal, tableBios);
                    }
                    biosCount++;
                }

                int driverCount = 0;
                foreach (var driver in driverList)
                {
                    token.ThrowIfCancellationRequested();
                    int newer = DRIVER_NOT_FOUND;
                    string tip = driver.version;

                    string? localVer = FindLocalVersion(driver.title, localDevices);
                    if (!string.IsNullOrEmpty(localVer))
                    {
                        try
                        {
                            string localClean = CleanVersion(localVer);
                            string remoteClean = CleanVersion(driver.version);
                            int compare = new Version(remoteClean).CompareTo(new Version(localClean));
                            newer = compare > 0 ? DRIVER_NEWER : -1;
                            tip = $"Download: {driver.version}\nInstalled: {localVer}";
                        }
                        catch (Exception ex)
                        {
                            Logger.WriteLine($"HP Driver version comparison error: {ex.Message}");
                        }
                    }

                    VisualiseNewDriver(driverCount, newer, tip, tableDrivers);
                    if (newer == DRIVER_NEWER)
                    {
                        updatesCountLocal++;
                        VisualiseNewCount(updatesCountLocal, tableDrivers);
                    }
                    driverCount++;
                }

                updatesCount = updatesCountLocal;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"HP Update query failed: {ex.ToString()}");
            }
        }

        #endregion
    }
}