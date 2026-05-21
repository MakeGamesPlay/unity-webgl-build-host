using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using Debug = UnityEngine.Debug;

namespace MakeGamesPlay.WebBuildHost.Editor
{
    /// <summary>
    /// Editor window + menu item that hosts a Unity WebGL build via the bundled
    /// dependency-free <c>web-host</c> server (a small self-contained native
    /// binary, one per editor platform - no Python/Node/runtime required).
    ///
    /// The server serves the build with the Content-Encoding / Content-Type
    /// fixups Unity WebGL needs (.br / .gz / .wasm) plus COOP/COEP, exposes an
    /// optional self-signed HTTPS listener for same-network phone testing
    /// (camera / WebXR / SharedArrayBuffer secure context), and an optional
    /// Cloudflare quick-tunnel for off-network HTTPS. It also relays each
    /// connected device's console back to the editor over a loopback control
    /// endpoint (surfaced by the device-console UI in a later phase).
    ///
    /// UI: built with UI Toolkit (CreateGUI + a periodic UpdateUI that syncs
    /// element state to the status-file-backed model). The server is launched
    /// detached and tracked purely through the status file, so the window holds
    /// no child handle and re-discovers a running server across domain reloads
    /// and editor restarts. Closing the window does NOT stop the server; only
    /// Stop (or Ctrl-C in a terminal) does.
    /// </summary>
    public partial class WebBuildHostWindow : EditorWindow
    {
        // ─── Constants ─────────────────────────────────────────────
        const string LastFolderPrefKey = "WebBuildHost.LastFolder";
        const string LastPortPrefKey   = "WebBuildHost.LastPort";
        const string LastTunnelPrefKey = "WebBuildHost.LastTunnel";
        const string LastLanPrefKey    = "WebBuildHost.LastLan";
        const int    DefaultPort       = 8000;

        // ─── Branding / CTA links (TODO: set these to the real published URLs) ───
        const string GitHubUrl     = "https://github.com/MakeGamesPlay/web-build-host";
        const string AssetStoreUrl = "https://assetstore.unity.com/publishers/MakeGamesPlay";
        const string WebARAssetUrl = "https://assetstore.unity.com/packages/tools/integration/webar-image-tracker";
        const int    MaxOutputLines    = 200;
        const double StatusPollIntervalSec = 1.0;
        const int    HealthProbeTimeoutMs  = 250;

        /// <summary>
        /// Mirror of the JSON the server writes to the status file. Field names
        /// must match the Go struct json tags EXACTLY - JsonUtility is case-
        /// and name-sensitive and silently leaves mismatches at their defaults.
        /// </summary>
        [Serializable]
        class HostStatus
        {
            public int pid;
            public int cloudflaredPid;
            public int httpPort;
            public int httpsPort;
            public int controlPort;
            public string localUrl;
            public string localHttpsUrl;
            public string lanUrl;
            public string lanHttpsUrl;
            public string tunnelUrl;
        }

        // ─── Config (mirrors EditorPrefs) ──────────────────────────
        string buildFolder;
        int port = DefaultPort;
        bool useTunnel = true;
        bool useLan = true;

        // ─── Tracked server (sourced from the status file) ─────────
        int serverPid;
        int cloudflaredPid;
        int boundPort;
        int httpsPort;
        int controlPort;
        string localUrl;
        string localHttpsUrl;
        string lanUrl;
        string lanHttpsUrl;
        string publicUrl;
        bool localServerAlive;
        bool? cloudflaredAvailable;

        double lastStatusPollAt;
        readonly List<string> outputLines = new List<string>();
        string serveLogTail = "";
        bool autoScroll = true;
        // QR of the best phone-reachable URL (tunnel, else LAN HTTPS, else LAN
        // HTTP). Regenerated only when that URL changes; destroyed on close.
        Texture2D qrTexture;
        string qrForUrl;

        // ─── UI Toolkit element refs (built in CreateGUI) ──────────
        VisualElement configContainer;
        TextField folderField;
        IntegerField portField;
        Toggle tunnelToggle, lanToggle, autoScrollToggle;
        Button startButton, stopButton;
        Label stateValue;
        HelpBox portRebindHelp, notRespondingHelp;
        VisualElement cloudflaredHelp;
        VisualElement shareRow, qrCard, qrInner, detailsCol;
        UrlRow localRow, lanRow, publicRow;
        Label scanTitle, scanHint;

        /// <summary>A label + read-only (selectable) field + Copy button.</summary>
        class UrlRow
        {
            public VisualElement root;
            public Label label;
            public TextField field;
            public Button copy;
            public Button open;

            public void Set(string lbl, string url, string placeholder = "(not available)")
            {
                label.text = lbl;
                bool has = !string.IsNullOrEmpty(url);
                field.SetValueWithoutNotify(has ? url : placeholder);
                copy.SetEnabled(has);
                copy.userData = has ? url : null;
                open.SetEnabled(has);
                open.userData = has ? url : null;
            }

            public void Show(bool v) => root.style.display = v ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ─── Menu entry ────────────────────────────────────────────

        [MenuItem("Tools/WebGL Build Host", priority = 103)]
        static void ShowMenuItem()
        {
            var win = GetWindow<WebBuildHostWindow>(utility: false, title: "WebGL Build Host", focus: true);
            win.minSize = new Vector2(540, 380);
            win.Show();
            if (!win.IsRunning && string.IsNullOrEmpty(win.buildFolder))
            {
                win.PromptForFolder();
            }
        }

        // ─── Lifecycle ─────────────────────────────────────────────

        void OnEnable()
        {
            buildFolder = EditorPrefs.GetString(LastFolderPrefKey, "");
            port        = EditorPrefs.GetInt(LastPortPrefKey, DefaultPort);
            useTunnel   = EditorPrefs.GetBool(LastTunnelPrefKey, true);
            useLan      = EditorPrefs.GetBool(LastLanPrefKey, true);
            EditorApplication.update += OnEditorUpdate;
            RefreshFromStatusFile(announce: true);
            ReadServeLog();
        }

        void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            if (qrTexture != null) { DestroyImmediate(qrTexture); qrTexture = null; qrForUrl = null; }
        }

        // ─── Editor pulse ──────────────────────────────────────────

        void OnEditorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - lastStatusPollAt < StatusPollIntervalSec) return;
            lastStatusPollAt = now;

            RefreshFromStatusFile(announce: false);
            ReadServeLog();
            PollDevices();
            UpdateUI();
        }

        void ReadServeLog()
        {
            try
            {
                var path = LogFilePath();
                if (!File.Exists(path)) return;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs))
                {
                    var all = sr.ReadToEnd();
                    var lines = all.Split('\n');
                    int keep = Mathf.Min(lines.Length, MaxOutputLines);
                    int start = lines.Length - keep;
                    serveLogTail = string.Join("\n", lines, start, keep).TrimEnd();
                }
            }
            catch
            {
                // The server may be mid-write, or the file just got cleared;
                // leave the previous tail and retry on the next poll.
            }
        }

        // ─── UI construction ───────────────────────────────────────

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 8;
            root.style.paddingRight = 8;
            root.style.paddingTop = 6;
            root.style.paddingBottom = 6;

            root.Add(Bold("Host WebGL Build"));
            var desc = Wrapped(
                "Serves a Unity Web build with the right Content-Encoding / wasm headers over HTTP and self-signed " +
                "HTTPS, optionally exposing it via a Cloudflare quick-tunnel for phone testing. The dependency-free " +
                "server keeps running across recompiles, builds, and editor restarts until you Stop it.");
            desc.style.marginBottom = 8;
            root.Add(desc);

            // ── Config ──
            var configCard = Card();
            configContainer = new VisualElement();

            var folderRow = Row();
            folderRow.Add(Fixed("Build folder", 90));
            folderField = new TextField { isReadOnly = true };
            folderField.style.flexGrow = 1;
            folderField.style.flexShrink = 1;
            folderField.style.minWidth = 0; // allow shrink so Browse stays visible on narrow windows
            folderField.style.marginRight = 4;
            var browse = new Button(() => { PromptForFolder(); UpdateUI(); }) { text = "Browse…" };
            browse.style.width = 80;
            browse.style.flexShrink = 0;
            folderRow.Add(folderField);
            folderRow.Add(browse);
            configContainer.Add(folderRow);

            var portRow = Row();
            portRow.Add(Fixed("Port", 90));
            portField = new IntegerField { value = port };
            portField.style.width = 80;
            portField.RegisterValueChangedCallback(e => port = Mathf.Clamp(e.newValue, 1, 65535));
            portRow.Add(portField);
            var portSpacer = new VisualElement();
            portSpacer.style.flexGrow = 1;
            portRow.Add(portSpacer);
            tunnelToggle = new Toggle("Expose via Cloudflare tunnel") { value = useTunnel };
            tunnelToggle.RegisterValueChangedCallback(e => { useTunnel = e.newValue; UpdateUI(); });
            portRow.Add(tunnelToggle);
            configContainer.Add(portRow);

            var lanRowCfg = Row();
            var lanSpacer = new VisualElement();
            lanSpacer.style.width = 94;
            lanRowCfg.Add(lanSpacer);
            lanToggle = new Toggle("Serve on local network (HTTPS)") { value = useLan };
            lanToggle.tooltip = "Bind 0.0.0.0 so phones on the same Wi-Fi can reach a self-signed HTTPS URL " +
                                "(secure context for camera / WebXR). May trigger a one-time OS firewall prompt.";
            lanToggle.RegisterValueChangedCallback(e => useLan = e.newValue);
            lanRowCfg.Add(lanToggle);
            configContainer.Add(lanRowCfg);

            configCard.Add(configContainer);

            // Inline help: if the tunnel is enabled but cloudflared is missing, explain
            // what it's for and offer the install command + download link, so the user
            // doesn't have to start the server to find out it's needed.
            cloudflaredHelp = new VisualElement();
            cloudflaredHelp.style.display = DisplayStyle.None;
            cloudflaredHelp.style.marginTop = 2;
            cloudflaredHelp.Add(new HelpBox(
                "The public link needs cloudflared, which isn't installed. The build still " +
                "works on localhost and your local network without it.", HelpBoxMessageType.Info));
            var cfBtns = Row();
            cfBtns.style.marginTop = 2;
            cfBtns.Add(new Button(() =>
            {
                var cmd = CloudflaredInstallCommand();
                EditorGUIUtility.systemCopyBuffer = cmd;
                AppendOutput("[host] Copied to clipboard: " + cmd);
            }) { text = "Copy install command" });
            cfBtns.Add(new Button(() => Application.OpenURL("https://github.com/cloudflare/cloudflared/releases"))
                { text = "Download page" });
            cfBtns.Add(new Button(() => { cloudflaredAvailable = ProbeCloudflared(); UpdateUI(); })
                { text = "Re-check" });
            cloudflaredHelp.Add(cfBtns);
            configCard.Add(cloudflaredHelp);

            var btnRow = Row();
            btnRow.style.marginTop = 4;
            startButton = new Button(() => { StartServer(); UpdateUI(); }) { text = "Start" };
            startButton.style.flexGrow = 1;
            startButton.style.height = 28;
            stopButton = new Button(() => { StopServer(); UpdateUI(); }) { text = "Stop" };
            stopButton.style.flexGrow = 1;
            stopButton.style.height = 28;
            btnRow.Add(startButton);
            btnRow.Add(stopButton);
            configCard.Add(btnRow);
            root.Add(configCard);

            // ── Status ──
            var statusTitle = Bold("Status");
            statusTitle.style.marginTop = 8;
            statusTitle.style.marginBottom = 2;
            root.Add(statusTitle);
            var statusCard = Card();

            var stateRow = Row();
            stateRow.Add(Fixed("State", 90));
            stateValue = new Label("Idle");
            stateRow.Add(stateValue);
            statusCard.Add(stateRow);

            portRebindHelp = new HelpBox("", HelpBoxMessageType.Info);
            portRebindHelp.style.display = DisplayStyle.None;
            statusCard.Add(portRebindHelp);
            notRespondingHelp = new HelpBox("", HelpBoxMessageType.Warning);
            notRespondingHelp.style.display = DisplayStyle.None;
            statusCard.Add(notRespondingHelp);

            // ── Share row: QR card + URL details ──
            shareRow = new VisualElement();
            shareRow.style.flexDirection = FlexDirection.Row;
            shareRow.style.marginTop = 4;

            qrCard = new VisualElement();
            qrCard.style.width = 184;
            qrCard.style.height = 184;
            qrCard.style.backgroundColor = Color.white;
            qrCard.style.marginRight = 12;
            SetPadding(qrCard, 10);
            SetRadius(qrCard, 14);
            qrInner = new VisualElement();
            qrInner.style.flexGrow = 1;
            qrCard.Add(qrInner);
            shareRow.Add(qrCard);

            detailsCol = new VisualElement();
            detailsCol.style.flexGrow = 1;
            detailsCol.style.justifyContent = Justify.Center;
            localRow = MakeUrlRow(54);
            lanRow = MakeUrlRow(54);
            publicRow = MakeUrlRow(54);
            detailsCol.Add(localRow.root);
            detailsCol.Add(lanRow.root);
            detailsCol.Add(publicRow.root);
            scanTitle = Bold("Scan with your phone");
            scanTitle.style.fontSize = 11;
            scanTitle.style.marginTop = 8;
            detailsCol.Add(scanTitle);
            scanHint = Wrapped("Point your camera at the QR code, or tap Copy and paste the URL into your mobile browser.");
            detailsCol.Add(scanHint);
            shareRow.Add(detailsCol);
            statusCard.Add(shareRow);
            root.Add(statusCard);

            // ── Log ──
            var logHeader = Row();
            logHeader.style.marginTop = 8;
            var logTitle = Bold("Log");
            logTitle.style.width = 60;
            logHeader.Add(logTitle);
            var logSpacer = new VisualElement();
            logSpacer.style.flexGrow = 1;
            logHeader.Add(logSpacer);
            var reloadAllBtn = new Button(ReloadAll) { text = "Reload all" };
            reloadAllBtn.tooltip = "Reload every connected device (after a rebuild)";
            logHeader.Add(reloadAllBtn);
            autoScrollToggle = new Toggle("Auto-scroll") { value = autoScroll };
            autoScrollToggle.RegisterValueChangedCallback(e => autoScroll = e.newValue);
            logHeader.Add(autoScrollToggle);
            var copyLog = new Button(CopySelectedRows) { text = "Copy" };
            copyLog.style.width = 60;
            logHeader.Add(copyLog);
            var clearLog = new Button(ClearSelectedTab) { text = "Clear" };
            clearLog.style.width = 60;
            logHeader.Add(clearLog);
            root.Add(logHeader);

            var logHint = Wrapped("[host] = window actions; server output is tailed below (startup errors, request logs, tunnel handshake).");
            logHint.style.opacity = 0.7f;
            root.Add(logHint);

            // Tab strip: pinned "Server" tab + one tab per connected device.
            root.Add(BuildTabStrip());

            // Device info + actions (shown only when a device tab is selected).
            deviceInfoRow = new VisualElement();
            deviceInfoRow.style.flexDirection = FlexDirection.Row;
            deviceInfoRow.style.alignItems = Align.Center;
            deviceInfoRow.style.marginBottom = 2;
            deviceInfoRow.style.display = DisplayStyle.None;
            deviceInfoLabel = new Label("");
            deviceInfoLabel.style.fontSize = 11;
            deviceInfoLabel.style.color = LogMuted;
            deviceInfoLabel.style.flexShrink = 1;
            deviceInfoLabel.style.minWidth = 0;
            deviceInfoLabel.style.overflow = Overflow.Hidden;
            deviceInfoLabel.style.whiteSpace = WhiteSpace.NoWrap;
            deviceInfoRow.Add(deviceInfoLabel);
            var diSpacer = new VisualElement(); diSpacer.style.flexGrow = 1; deviceInfoRow.Add(diSpacer);
            var identifyBtn = new Button(IdentifySelected) { text = "Identify" };
            identifyBtn.style.flexShrink = 0;
            identifyBtn.tooltip = "Flash an overlay (and vibrate) on this device so you can spot which phone it is";
            deviceInfoRow.Add(identifyBtn);
            var reportBtn = new Button(CopyDeviceReport) { text = "Copy report" };
            reportBtn.style.flexShrink = 0;
            reportBtn.tooltip = "Copy this device's info + logs as a Markdown bug report";
            deviceInfoRow.Add(reportBtn);
            root.Add(deviceInfoRow);

            // Bordered "log box" that visually groups the search field + the
            // list as one unit belonging to the selected tab.
            var logBox = new VisualElement();
            logBox.style.flexGrow = 1;
            logBox.style.marginTop = 2;
            SetBorder(logBox, new Color(0f, 0f, 0f, 0.2f), 1);

            var search = new ToolbarSearchField();
            search.style.width = Length.Percent(100); // span the full width of the log box
            search.style.marginTop = 3; search.style.marginBottom = 3;
            search.style.marginLeft = 0; search.style.marginRight = 0;
            search.RegisterValueChangedCallback(e => { searchText = e.newValue ?? ""; logSig = null; UpdateLogView(); });
            logBox.Add(search);

            // Level filters + collapse (device tabs only; hidden for the Server tab).
            filterRow = new VisualElement();
            filterRow.style.flexDirection = FlexDirection.Row;
            filterRow.style.alignItems = Align.Center;
            filterRow.style.marginBottom = 3;
            filterRow.style.marginLeft = 1; filterRow.style.marginRight = 1;
            logChip = MakeChip("Log", () => { showLog = !showLog; logSig = null; UpdateLogView(); });
            warnChip = MakeChip("Warn", () => { showWarn = !showWarn; logSig = null; UpdateLogView(); });
            errChip = MakeChip("Error", () => { showError = !showError; logSig = null; UpdateLogView(); });
            collapseChip = MakeChip("Collapse", () => { collapse = !collapse; logSig = null; UpdateLogView(); });
            filterRow.Add(logChip); filterRow.Add(warnChip); filterRow.Add(errChip);
            var filterSpacer = new VisualElement(); filterSpacer.style.flexGrow = 1; filterRow.Add(filterSpacer);
            filterRow.Add(collapseChip);
            logBox.Add(filterRow);

            // Log = a VIRTUALIZED ListView (only visible rows render) of plain,
            // per-row-colored Labels. We deliberately avoid one big rich-text
            // Label: Unity 6's text generator NullRefs + lags on large / tag-
            // heavy strings (RichTextTagParser.CreateTextSpan).
            logList = new ListView();
            logList.style.flexGrow = 1;
            // Dynamic height so each row sizes to its (wrapped) content. Rows
            // are 3 columns: [#] [time] [message]. Only the message wraps - the
            // # and time stay in fixed top-aligned gutters.
            logList.virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight;
            logList.fixedItemHeight = 18; // initial estimate only
            logList.selectionType = SelectionType.Multiple; // shift/ctrl-select rows
            logList.showAlternatingRowBackgrounds = AlternatingRowBackground.ContentOnly;
            logList.itemsSource = logRows;
            logList.makeItem = () =>
            {
                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.FlexStart; // # / time align to first line
                row.style.paddingTop = 1; row.style.paddingBottom = 1;
                row.style.paddingLeft = 4; row.style.paddingRight = 6;

                var seq = new Label();
                seq.style.width = 50; seq.style.flexShrink = 0;
                seq.style.unityTextAlign = TextAnchor.UpperRight;
                seq.style.paddingRight = 8;
                seq.style.color = LogMuted; seq.style.fontSize = 11;
                row.Add(seq);

                var time = new Label();
                time.style.width = 58; time.style.flexShrink = 0;
                time.style.unityTextAlign = TextAnchor.UpperLeft;
                time.style.color = LogMuted; time.style.fontSize = 11;
                row.Add(time);

                var msg = new Label();
                msg.style.flexGrow = 1; msg.style.flexShrink = 1;
                msg.style.whiteSpace = WhiteSpace.Normal; // wrap within the message column only
                msg.style.unityTextAlign = TextAnchor.UpperLeft;
                msg.style.fontSize = 11;
                msg.enableRichText = false; // never touch the rich-text parser
                row.Add(msg);

                row.userData = new[] { seq, time, msg };
                return row;
            };
            logList.bindItem = (el, i) =>
            {
                var labels = (Label[])el.userData;
                var r = logRows[i];
                labels[0].text = r.frame;
                labels[0].style.display = string.IsNullOrEmpty(r.frame) ? DisplayStyle.None : DisplayStyle.Flex;
                labels[1].text = r.time;
                labels[1].style.display = string.IsNullOrEmpty(r.time) ? DisplayStyle.None : DisplayStyle.Flex;
                labels[2].text = r.text;
                labels[2].style.color = r.color;
            };
            // Ctrl/Cmd+C copies the selected rows (or all rows if none selected).
            logList.RegisterCallback<KeyDownEvent>(evt =>
            {
                if ((evt.ctrlKey || evt.commandKey) && evt.keyCode == KeyCode.C)
                {
                    CopySelectedRows();
                    evt.StopPropagation();
                }
            });
            // Scrolling up pauses auto-scroll so you can read / select without
            // the view snapping back to the bottom. Re-check Auto-scroll to resume.
            logList.RegisterCallback<WheelEvent>(evt =>
            {
                if (evt.delta.y < 0f && autoScroll)
                {
                    autoScroll = false;
                    autoScrollToggle.SetValueWithoutNotify(false);
                }
            });
            logBox.Add(logList);
            root.Add(logBox);

            // ── Footer: branding + CTAs (own GitHub/Asset Store + cross-promo) ──
            var footer = new VisualElement();
            footer.style.flexDirection = FlexDirection.Row;
            footer.style.alignItems = Align.Center;
            footer.style.flexShrink = 0;
            footer.style.flexWrap = Wrap.Wrap;
            footer.style.marginTop = 6;
            footer.style.paddingTop = 5;
            footer.style.borderTopWidth = 1;
            footer.style.borderTopColor = new Color(1f, 1f, 1f, 0.1f);
            var brand = new Label("WebGL Build Host");
            brand.style.fontSize = 11; brand.style.color = LogMuted; brand.style.marginRight = 8;
            footer.Add(brand);
            footer.Add(Link("GitHub", GitHubUrl));
            footer.Add(FooterSep());
            footer.Add(Link("Asset Store", AssetStoreUrl));
            var footSpacer = new VisualElement(); footSpacer.style.flexGrow = 1; footSpacer.style.minWidth = 8; footer.Add(footSpacer);
            var promo = new Label("Doing AR on the Web? ");
            promo.style.fontSize = 11; promo.style.color = LogMuted;
            footer.Add(promo);
            footer.Add(Link("Try WebAR Image Tracker →", WebARAssetUrl));
            root.Add(footer);

            UpdateUI();
        }

        // ─── UI sync ───────────────────────────────────────────────

        void UpdateUI()
        {
            // Guard: not built yet, or window destroyed.
            if (this == null || stateValue == null) return;

            bool running = IsRunning;

            configContainer.SetEnabled(!running);
            startButton.SetEnabled(!running && !string.IsNullOrEmpty(buildFolder));
            startButton.text = running ? "Running…" : "Start";
            stopButton.SetEnabled(running);

            folderField.SetValueWithoutNotify(string.IsNullOrEmpty(buildFolder) ? "(not selected)" : buildFolder);
            if (portField.value != port) portField.SetValueWithoutNotify(port);
            if (tunnelToggle.value != useTunnel) tunnelToggle.SetValueWithoutNotify(useTunnel);
            if (lanToggle.value != useLan) lanToggle.SetValueWithoutNotify(useLan);
            if (autoScrollToggle.value != autoScroll) autoScrollToggle.SetValueWithoutNotify(autoScroll);

            // Probe cloudflared once (lazily) when the tunnel is on, then show install
            // help while it's missing. The Re-check button re-probes after installing.
            if (useTunnel && cloudflaredAvailable == null) cloudflaredAvailable = ProbeCloudflared();
            cloudflaredHelp.style.display =
                (useTunnel && cloudflaredAvailable == false) ? DisplayStyle.Flex : DisplayStyle.None;

            stateValue.text = StateText();

            bool showRebind = running && boundPort > 0 && boundPort != port;
            portRebindHelp.style.display = showRebind ? DisplayStyle.Flex : DisplayStyle.None;
            if (showRebind)
                portRebindHelp.text = "Port " + port + " was in use; the server is bound to port " + boundPort + " instead.";

            bool showNR = running && !localServerAlive;
            notRespondingHelp.style.display = showNR ? DisplayStyle.Flex : DisplayStyle.None;
            if (showNR)
                notRespondingHelp.text = "localhost:" + boundPort + " isn't accepting connections yet. The server may " +
                                         "still be starting, or its listener crashed. Give it a second; if it persists, Stop and Start again.";

            if (!running)
            {
                EnsureQrTexture(); // releases the texture (no target)
                shareRow.style.display = DisplayStyle.None;
            }
            else
            {
                shareRow.style.display = DisplayStyle.Flex;
                bool haveQr = EnsureQrTexture();
                qrCard.style.display = haveQr ? DisplayStyle.Flex : DisplayStyle.None;
                if (haveQr) qrInner.style.backgroundImage = new StyleBackground(qrTexture);
                scanTitle.style.display = haveQr ? DisplayStyle.Flex : DisplayStyle.None;
                scanHint.style.display = haveQr ? DisplayStyle.Flex : DisplayStyle.None;

                localRow.Set("Local", localUrl);
                localRow.Show(true);

                string lan = !string.IsNullOrEmpty(lanHttpsUrl) ? lanHttpsUrl : lanUrl;
                if (!string.IsNullOrEmpty(lan)) { lanRow.Set("LAN", lan); lanRow.Show(true); }
                else lanRow.Show(false);

                bool tunnelPending = useTunnel && cloudflaredAvailable == true && string.IsNullOrEmpty(publicUrl);
                if (!string.IsNullOrEmpty(publicUrl)) { publicRow.Set("Public", publicUrl); publicRow.Show(true); }
                else if (tunnelPending) { publicRow.Set("Public", null, "(resolving…)"); publicRow.Show(true); }
                else publicRow.Show(false);
            }

            UpdateTabs();
            UpdateDeviceInfo();
            UpdateLogView();
        }

        string StateText()
        {
            if (IsRunning && !localServerAlive)
                return "Process alive but server not responding - device reload will fail";
            if (IsRunning && !string.IsNullOrEmpty(publicUrl))
                return "Running (tunnel ready)";
            if (IsRunning && useTunnel && cloudflaredAvailable == false)
                return "Running (cloudflared not installed - localhost/LAN only)";
            if (IsRunning && useTunnel)
                return "Running - waiting for tunnel…";
            if (IsRunning)
                return useLan ? "Running (LAN + localhost)" : "Running (localhost only)";
            return "Idle";
        }

        // ─── UI helpers ────────────────────────────────────────────

        UrlRow MakeUrlRow(float labelWidth)
        {
            var row = Row();
            var lbl = new Label("");
            lbl.style.width = labelWidth;
            var field = new TextField { isReadOnly = true };
            field.style.flexGrow = 1;
            field.style.flexShrink = 1;
            field.style.minWidth = 0; // shrink so Copy stays visible on narrow windows
            field.style.marginRight = 4;
            var open = new Button { text = "Open" };
            open.style.width = 52;
            open.style.flexShrink = 0;
            open.clicked += () =>
            {
                if (open.userData is string s && !string.IsNullOrEmpty(s)) Application.OpenURL(s);
            };
            var copy = new Button { text = "Copy" };
            copy.style.width = 60;
            copy.style.flexShrink = 0;
            copy.clicked += () =>
            {
                if (copy.userData is string s && !string.IsNullOrEmpty(s))
                    EditorGUIUtility.systemCopyBuffer = s;
            };
            row.Add(lbl);
            row.Add(field);
            row.Add(open);
            row.Add(copy);
            return new UrlRow { root = row, label = lbl, field = field, copy = copy, open = open };
        }

        static VisualElement Row()
        {
            var v = new VisualElement();
            v.style.flexDirection = FlexDirection.Row;
            v.style.alignItems = Align.Center;
            v.style.marginBottom = 2;
            return v;
        }

        static Label Fixed(string text, float width)
        {
            var l = new Label(text);
            l.style.width = width;
            return l;
        }

        static Label Bold(string text)
        {
            var l = new Label(text);
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            return l;
        }

        static Label Wrapped(string text)
        {
            var l = new Label(text);
            l.style.whiteSpace = WhiteSpace.Normal;
            l.style.fontSize = 11;
            l.style.opacity = 0.85f;
            return l;
        }

        static void SetPadding(VisualElement e, float p)
        {
            e.style.paddingLeft = p; e.style.paddingRight = p;
            e.style.paddingTop = p; e.style.paddingBottom = p;
        }

        static void SetRadius(VisualElement e, float r)
        {
            e.style.borderTopLeftRadius = r; e.style.borderTopRightRadius = r;
            e.style.borderBottomLeftRadius = r; e.style.borderBottomRightRadius = r;
        }

        static void SetBorder(VisualElement e, Color c, float w)
        {
            e.style.borderTopWidth = w; e.style.borderBottomWidth = w;
            e.style.borderLeftWidth = w; e.style.borderRightWidth = w;
            e.style.borderTopColor = c; e.style.borderBottomColor = c;
            e.style.borderLeftColor = c; e.style.borderRightColor = c;
        }

        // A subtle rounded panel used to visually group a section's controls.
        static VisualElement Card()
        {
            var c = new VisualElement();
            c.style.backgroundColor = new Color(1f, 1f, 1f, 0.03f);
            c.style.borderTopLeftRadius = 6; c.style.borderTopRightRadius = 6;
            c.style.borderBottomLeftRadius = 6; c.style.borderBottomRightRadius = 6;
            c.style.paddingLeft = 8; c.style.paddingRight = 8;
            c.style.paddingTop = 6; c.style.paddingBottom = 6;
            c.style.marginBottom = 8;
            return c;
        }

        // A small clickable, toggleable chip (used for the level filters + collapse).
        Label MakeChip(string text, Action onClick)
        {
            var c = new Label(text);
            c.style.paddingLeft = 8; c.style.paddingRight = 8;
            c.style.paddingTop = 2; c.style.paddingBottom = 2;
            c.style.marginRight = 4;
            c.style.borderTopLeftRadius = 4; c.style.borderTopRightRadius = 4;
            c.style.borderBottomLeftRadius = 4; c.style.borderBottomRightRadius = 4;
            c.style.fontSize = 11;
            c.RegisterCallback<ClickEvent>(_ => onClick());
            return c;
        }

        static void ChipStyle(Label chip, bool active, Color activeColor)
        {
            chip.style.backgroundColor = active ? activeColor : new Color(1f, 1f, 1f, 0.05f);
            chip.style.color = active ? Color.white : LogMuted;
        }

        // A clickable text link that opens a URL in the browser (bold on hover).
        Label Link(string text, string url)
        {
            var l = new Label(text);
            l.style.color = new Color(0.36f, 0.62f, 1f);
            l.style.fontSize = 11;
            l.tooltip = url;
            l.RegisterCallback<ClickEvent>(_ => { if (!string.IsNullOrEmpty(url)) Application.OpenURL(url); });
            l.RegisterCallback<MouseEnterEvent>(_ => l.style.unityFontStyleAndWeight = FontStyle.Bold);
            l.RegisterCallback<MouseLeaveEvent>(_ => l.style.unityFontStyleAndWeight = FontStyle.Normal);
            return l;
        }

        static Label FooterSep()
        {
            var s = new Label(" · ");
            s.style.color = LogMuted; s.style.fontSize = 11;
            return s;
        }

        // ─── QR ────────────────────────────────────────────────────

        /// <summary>
        /// The URL the QR should encode: tunnel first (works anywhere), then
        /// LAN HTTPS (secure context for camera/AR on the same Wi-Fi), then LAN
        /// HTTP. Localhost is never phone-reachable, so it's never chosen.
        /// </summary>
        string QrTarget()
        {
            if (!string.IsNullOrEmpty(publicUrl)) return publicUrl;
            if (!string.IsNullOrEmpty(lanHttpsUrl)) return lanHttpsUrl;
            if (!string.IsNullOrEmpty(lanUrl)) return lanUrl;
            return null;
        }

        bool EnsureQrTexture()
        {
            var target = QrTarget();
            if (string.IsNullOrEmpty(target))
            {
                if (qrTexture != null) { DestroyImmediate(qrTexture); qrTexture = null; qrForUrl = null; }
                return false;
            }
            if (qrTexture == null || qrForUrl != target)
            {
                if (qrTexture != null) DestroyImmediate(qrTexture);
                qrTexture = WebQrCode.Generate(target);
                qrForUrl = target;
                if (qrTexture != null) qrTexture.filterMode = FilterMode.Point;
            }
            return qrTexture != null;
        }

        // ─── Folder picker ─────────────────────────────────────────

        void PromptForFolder()
        {
            string initial = buildFolder;
            if (string.IsNullOrEmpty(initial) || !Directory.Exists(initial))
            {
                initial = Path.GetDirectoryName(Application.dataPath);
            }
            var picked = EditorUtility.OpenFolderPanel("Select Unity WebGL build folder", initial, "");
            if (string.IsNullOrEmpty(picked)) return;
            if (!ValidateBuildFolder(picked, out string warning))
            {
                if (!EditorUtility.DisplayDialog("Folder may not be a WebGL build",
                        warning + "\n\nUse this folder anyway?", "Use it", "Cancel"))
                {
                    return;
                }
            }
            buildFolder = picked;
            EditorPrefs.SetString(LastFolderPrefKey, buildFolder);
        }

        static bool ValidateBuildFolder(string folder, out string warning)
        {
            warning = null;
            if (!Directory.Exists(folder))
            {
                warning = "Folder does not exist:\n" + folder;
                return false;
            }
            var hasIndex = File.Exists(Path.Combine(folder, "index.html"));
            var hasBuildSubfolder = Directory.Exists(Path.Combine(folder, "Build"));
            if (!hasIndex && !hasBuildSubfolder)
            {
                warning = "No index.html or Build/ subfolder found. This usually means the folder isn't a Unity WebGL build root.";
                return false;
            }
            if (!hasIndex)
                warning = "index.html not found - the page won't load when opened from a browser.";
            else if (!hasBuildSubfolder)
                warning = "Build/ subfolder not found - Unity loader probably won't resolve.";
            return string.IsNullOrEmpty(warning);
        }

        // ─── Server state (status-file backed) ─────────────────────

        bool IsRunning => serverPid != 0;

        static string StatusFilePath()
        {
            uint hash = (uint)(Application.dataPath ?? "web").GetHashCode();
            return Path.Combine(Path.GetTempPath(), "web-host-" + hash.ToString("x8") + ".json");
        }

        static string LogFilePath()
        {
            uint hash = (uint)(Application.dataPath ?? "web").GetHashCode();
            return Path.Combine(Path.GetTempPath(), "web-host-" + hash.ToString("x8") + ".log");
        }

        static bool TryReadStatus(out HostStatus status)
        {
            status = null;
            try
            {
                var path = StatusFilePath();
                if (!File.Exists(path)) return false;
                var json = File.ReadAllText(path);
                if (string.IsNullOrEmpty(json)) return false;
                status = JsonUtility.FromJson<HostStatus>(json);
                return status != null && status.pid > 0;
            }
            catch
            {
                return false;
            }
        }

        static bool PidAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                var p = Process.GetProcessById(pid);
                bool alive = p != null && !p.HasExited;
                p?.Dispose();
                return alive;
            }
            catch
            {
                return false;
            }
        }

        void RefreshFromStatusFile(bool announce)
        {
            if (TryReadStatus(out var s) && PidAlive(s.pid))
            {
                bool firstSeen = serverPid == 0;
                serverPid      = s.pid;
                cloudflaredPid = s.cloudflaredPid;
                boundPort      = s.httpPort;
                httpsPort      = s.httpsPort;
                controlPort    = s.controlPort;
                localUrl       = string.IsNullOrEmpty(s.localUrl) ? ("http://localhost:" + s.httpPort) : s.localUrl;
                localHttpsUrl  = s.localHttpsUrl;
                lanUrl         = s.lanUrl;
                lanHttpsUrl    = s.lanHttpsUrl;
                publicUrl      = string.IsNullOrEmpty(s.tunnelUrl) ? null : s.tunnelUrl;
                localServerAlive = boundPort > 0 && ProbeLocalServer(boundPort);
                if (announce && firstSeen)
                {
                    AppendOutput("[host] Re-discovered running server (PID " + serverPid +
                                 ") on " + localUrl + ". It survived the reload/restart - nothing to re-host.");
                }
            }
            else
            {
                if (serverPid != 0)
                {
                    AppendOutput("[host] Tracked server (PID " + serverPid + ") is no longer running.");
                }
                serverPid = 0;
                cloudflaredPid = 0;
                boundPort = 0;
                httpsPort = 0;
                controlPort = 0;
                localUrl = null;
                localHttpsUrl = null;
                lanUrl = null;
                lanHttpsUrl = null;
                publicUrl = null;
                localServerAlive = false;
            }
        }

        // ─── Start / Stop ──────────────────────────────────────────

        void StartServer()
        {
            if (IsRunning)
            {
                AppendOutput("[host] Server already tracked (PID " + serverPid + ") - ignoring Start.");
                return;
            }
            if (string.IsNullOrEmpty(buildFolder) || !Directory.Exists(buildFolder))
            {
                EditorUtility.DisplayDialog("No build folder", "Pick a Unity WebGL build folder first.", "OK");
                return;
            }
            var binPath = ResolveServerBinaryPath(out string expected);
            if (binPath == null)
            {
                EditorUtility.DisplayDialog("Server binary missing",
                    "Couldn't find the web-host server binary for this platform inside the plugin.\n\nExpected:\n  " +
                    expected + "\n\nBuild it with Editor/HostBuild/Server~/build.ps1 (or build.sh).",
                    "OK");
                return;
            }

            boundPort = port;

            bool effectiveTunnel = useTunnel;
            if (effectiveTunnel)
            {
                cloudflaredAvailable = ProbeCloudflared();
                if (cloudflaredAvailable == false)
                {
                    AppendOutput("[host] cloudflared not found - tunnel skipped (LAN + localhost still work). " +
                                 "Install: `winget install Cloudflare.cloudflared` (Windows) / " +
                                 "`brew install cloudflared` (macOS).");
                    effectiveTunnel = false;
                }
            }

            EditorPrefs.SetInt(LastPortPrefKey, port);
            EditorPrefs.SetBool(LastTunnelPrefKey, useTunnel);
            EditorPrefs.SetBool(LastLanPrefKey, useLan);

            var statusPath = StatusFilePath();
            var logPath = LogFilePath();
            try { if (File.Exists(statusPath)) File.Delete(statusPath); } catch { }
            try { if (File.Exists(logPath)) File.Delete(logPath); } catch { }
            serveLogTail = "";

            EnsureExecutable(binPath);

            var args = "--root \"" + buildFolder + "\"" +
                       " --port " + boundPort +
                       (useLan ? " --lan" : "") +
                       (effectiveTunnel ? "" : " --no-tunnel") +
                       " --status-file \"" + statusPath + "\"" +
                       " --log-file \"" + logPath + "\"";
            AppendOutput("[host] launching: " + binPath + " " + args);

            var psi = new ProcessStartInfo
            {
                FileName = binPath,
                Arguments = args,
                WorkingDirectory = buildFolder,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            try
            {
                var p = Process.Start(psi);
                try { serverPid = (p != null) ? p.Id : 0; } catch { serverPid = 0; }
                p?.Dispose();
            }
            catch (Exception ex)
            {
                AppendOutput("[host] launch failed: " + ex.Message);
                boundPort = 0;
                serverPid = 0;
                return;
            }

            localUrl = "http://localhost:" + boundPort;
            localHttpsUrl = null;
            lanUrl = null;
            lanHttpsUrl = null;
            publicUrl = null;
            localServerAlive = false;
            AppendOutput("[host] web-host launched as an independent process. It keeps running across " +
                         "recompiles, builds, and editor restarts. Click Stop (or Ctrl-C in a terminal) to end it.");
            AppendOutput("[host] waiting for status file - URLs appear within ~1-2 s…");
            lastStatusPollAt = 0; // poll immediately next tick
        }

        void StopServer()
        {
            int srvPid = serverPid;
            int cfPid = cloudflaredPid;
            if ((srvPid == 0 || cfPid == 0) && TryReadStatus(out var s))
            {
                if (srvPid == 0) srvPid = s.pid;
                if (cfPid == 0) cfPid = s.cloudflaredPid;
            }

            KillPid(cfPid, "cloudflared");
            KillPid(srvPid, "server");

            try { var path = StatusFilePath(); if (File.Exists(path)) File.Delete(path); } catch { }
            try { var lp = LogFilePath(); if (File.Exists(lp)) File.Delete(lp); } catch { }

            serverPid = 0;
            cloudflaredPid = 0;
            boundPort = 0;
            httpsPort = 0;
            controlPort = 0;
            localUrl = null;
            localHttpsUrl = null;
            lanUrl = null;
            lanHttpsUrl = null;
            publicUrl = null;
            localServerAlive = false;
            serveLogTail = "";
            AppendOutput("[host] Stopped.");
        }

        static void KillPid(int pid, string label)
        {
            if (pid <= 0) return;
            try
            {
                var p = Process.GetProcessById(pid);
                if (p != null && !p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(2000);
                }
                p?.Dispose();
            }
            catch
            {
                // Already gone, or a PID we can't touch - nothing to do.
            }
        }

        // ─── TCP liveness probe ────────────────────────────────────

        static bool ProbeLocalServer(int port, int timeoutMs = HealthProbeTimeoutMs)
        {
            try
            {
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    var ar = socket.BeginConnect(IPAddress.Loopback, port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                    try { socket.EndConnect(ar); return true; }
                    catch { return false; }
                }
            }
            catch
            {
                return false;
            }
        }

        void AppendOutput(string line)
        {
            lock (outputLines)
            {
                outputLines.Add(line);
                if (outputLines.Count > MaxOutputLines)
                {
                    outputLines.RemoveRange(0, outputLines.Count - MaxOutputLines);
                }
            }
            EditorApplication.delayCall += UpdateUI;
        }

        // ─── Server binary discovery ───────────────────────────────

        string ResolveServerBinaryPath(out string expectedPath)
        {
            string file = ServerBinaryFileName();
            string editorDir = PackageEditorDir();
            string full = string.IsNullOrEmpty(editorDir) ? null : Path.Combine(editorDir, "HostBuild", "Bin~", file);
            expectedPath = full ?? ("<package>/Editor/HostBuild/Bin~/" + file);
            if (full != null && File.Exists(full)) return Path.GetFullPath(full);

            // Fallback: search the project (handles a relocated package).
            try
            {
                var hits = Directory.GetFiles(Application.dataPath, file, SearchOption.AllDirectories);
                if (hits.Length > 0) return Path.GetFullPath(hits[0]);
            }
            catch { }
            return null;
        }

        // Locate this package's Editor folder via the window's own script asset,
        // so the binary is found with no dependency on any other plugin. Returns an
        // absolute path that resolves whether the package lives under Assets/, as an
        // embedded package, or in the UPM cache (Library/PackageCache for git/registry
        // installs). File.Exists on a virtual "Packages/<name>/…" path would otherwise
        // fail for cached packages, so map the asset path onto the real on-disk root.
        string PackageEditorDir()
        {
            try
            {
                var ms = MonoScript.FromScriptableObject(this);
                var assetPath = AssetDatabase.GetAssetPath(ms);
                if (string.IsNullOrEmpty(assetPath)) return null;

                var pkg = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                if (pkg != null && !string.IsNullOrEmpty(pkg.resolvedPath))
                {
                    // assetPath = "Packages/<name>/Editor/WebBuildHostWindow.cs";
                    // graft the part after "Packages/<name>" onto the real package root.
                    var rel = assetPath.Substring(("Packages/" + pkg.name).Length).TrimStart('/', '\\');
                    return Path.GetDirectoryName(Path.GetFullPath(Path.Combine(pkg.resolvedPath, rel)));
                }

                // Under Assets/ (or relocated): resolve relative to the project root.
                return Path.GetDirectoryName(Path.GetFullPath(assetPath));
            }
            catch { }
            return null;
        }

        static string ServerBinaryFileName()
        {
            string os, arch, ext = "";
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: os = "windows"; arch = "amd64"; ext = ".exe"; break;
                case RuntimePlatform.OSXEditor:     os = "darwin";  arch = IsArm64() ? "arm64" : "amd64"; break;
                case RuntimePlatform.LinuxEditor:   os = "linux";   arch = "amd64"; break;
                default:                            os = "windows"; arch = "amd64"; ext = ".exe"; break;
            }
            return "web-host-" + os + "-" + arch + ext;
        }

        static bool IsArm64()
        {
            try { return RuntimeInformation.OSArchitecture == Architecture.Arm64; }
            catch { return false; }
        }

        static void EnsureExecutable(string path)
        {
#if UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/bin/chmod",
                    Arguments = "+x \"" + path + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                var p = Process.Start(psi);
                p?.WaitForExit(2000);
                p?.Dispose();
            }
            catch { }
#endif
        }

        static bool ProbeExecutable(string fileName, string args)
        {
            try
            {
                using (var p = new Process())
                {
                    p.StartInfo = new ProcessStartInfo
                    {
                        FileName = fileName,
                        Arguments = args,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    };
                    if (!p.Start()) return false;
                    p.WaitForExit(2000);
                    return p.HasExited && p.ExitCode == 0;
                }
            }
            catch
            {
                return false;
            }
        }

        // ─── Cloudflared discovery ─────────────────────────────────

        static bool ProbeCloudflared()
        {
            string[] candidates = {
                @"C:\Program Files (x86)\cloudflared\cloudflared.exe",
                @"C:\Program Files\cloudflared\cloudflared.exe",
                "cloudflared",
            };
            foreach (var c in candidates)
            {
                if (ProbeExecutable(c, "--version")) return true;
            }
            return false;
        }

        // Platform-appropriate one-liner to install cloudflared, copied to the clipboard
        // from the inline help. The Download page button covers every other case.
        static string CloudflaredInstallCommand()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor: return "winget install --id Cloudflare.cloudflared";
                case RuntimePlatform.OSXEditor:     return "brew install cloudflared";
                default:                            return "sudo apt-get install cloudflared";
            }
        }
    }
}
