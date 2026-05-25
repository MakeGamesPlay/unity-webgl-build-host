using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MakeGamesPlay.WebBuildHost.Editor
{
    /// <summary>
    /// Device-console half of the Host Build window: a tab strip (a pinned
    /// "Server" tab + one tab per connected device, each with a live/stale dot
    /// and an error badge) backed by the server's loopback control endpoint
    /// (/__webhost/devices + /__webhost/log). Per-device logs are colorized by
    /// level. The editor only POLLS the endpoint (no socket to lose), so this
    /// re-syncs across domain reloads automatically.
    /// </summary>
    public partial class WebBuildHostWindow
    {
        const string ServerTabId = "__server__";
        const int DeviceLogMaxLines = 5000;
        const int ControlTimeoutMs = 400;

        // ── Control-endpoint JSON (subset; matches the Go control handler) ──
        [Serializable] class CtrlDevices { public CtrlDevice[] devices; }

        [Serializable]
        class CtrlDevice
        {
            public string id;
            public string label;
            public string ip;
            public bool live;
            public int count;
            public int errors;
            public int warns;
            public long maxSeq;
            public string ua;
            public int w;
            public int h;
            public float dpr;
        }

        [Serializable] class CtrlLogResp { public string id; public CtrlLine[] lines; public long oldest; }

        [Serializable]
        class CtrlLine
        {
            public long seq;
            public string level;
            public string msg;
            public long ts;
            public long frame;
        }

        // ── Editor-side per-device model ──
        class DeviceTab
        {
            public string id;
            public string label;
            public string ip;
            public bool live;
            public int errors;
            public int warns;
            public string ua;
            public int w, h;
            public float dpr;
            public readonly List<CtrlLine> lines = new List<CtrlLine>();
            public long cursor;              // highest seq pulled into lines
            public bool everFetched;         // pulled at least once
            public VisualElement tab;
            public VisualElement dot;
            public Label nameLabel;
            public Label badge;
        }

        VisualElement tabStrip;
        VisualElement serverTab;
        readonly Dictionary<string, DeviceTab> deviceTabs = new Dictionary<string, DeviceTab>();
        readonly HashSet<string> closedTabs = new HashSet<string>();
        string selectedTabId = ServerTabId;

        static readonly Color DotLive  = new Color(0.30f, 0.80f, 0.42f);
        static readonly Color DotStale = new Color(0.50f, 0.50f, 0.50f);
        static readonly Color TabSelectedBg = new Color(0.24f, 0.37f, 0.59f, 1f);
        static readonly Color TabIdleBg     = new Color(1f, 1f, 1f, 0.06f);
        static readonly Color BadgeColor    = new Color(1f, 0.42f, 0.42f);

        // Log view (virtualized ListView - never feeds the rich-text parser).
        ListView logList;
        readonly List<LogRow> logRows = new List<LogRow>();
        string logSig;
        string searchText = "";
        bool showLog = true, showWarn = true, showError = true, collapse = false;
        VisualElement filterRow, deviceInfoRow;
        Label logChip, warnChip, errChip, collapseChip, deviceInfoLabel;
        struct LogRow { public string frame; public string time; public string text; public Color color; }
        static readonly Color LogDefault = new Color(0.80f, 0.80f, 0.80f);
        static readonly Color LogMuted   = new Color(0.55f, 0.55f, 0.55f);
        static readonly Color LogWarn    = new Color(1f, 0.82f, 0.40f);
        static readonly Color LogError   = new Color(1f, 0.42f, 0.42f);

        // ── Tab strip construction (called from CreateGUI) ──

        VisualElement BuildTabStrip()
        {
            tabStrip = new VisualElement();
            tabStrip.style.flexDirection = FlexDirection.Row;
            tabStrip.style.flexWrap = Wrap.Wrap;
            tabStrip.style.marginTop = 2;
            tabStrip.style.marginBottom = 2;
            serverTab = MakeTabElement(ServerTabId, "Server", false, out _, out _, out _);
            StyleTab(serverTab, true);
            tabStrip.Add(serverTab);
            return tabStrip;
        }

        VisualElement MakeTabElement(string id, string label, bool withDot,
                                     out VisualElement dot, out Label nameLabel, out Label badge)
        {
            var tab = new VisualElement();
            tab.style.flexDirection = FlexDirection.Row;
            tab.style.alignItems = Align.Center;
            tab.style.paddingLeft = 8; tab.style.paddingRight = 8;
            tab.style.paddingTop = 3; tab.style.paddingBottom = 3;
            tab.style.marginRight = 4; tab.style.marginBottom = 4;
            tab.style.borderTopLeftRadius = 4; tab.style.borderTopRightRadius = 4;
            tab.style.borderBottomLeftRadius = 4; tab.style.borderBottomRightRadius = 4;

            dot = new VisualElement();
            dot.style.width = 8; dot.style.height = 8;
            dot.style.borderTopLeftRadius = 4; dot.style.borderTopRightRadius = 4;
            dot.style.borderBottomLeftRadius = 4; dot.style.borderBottomRightRadius = 4;
            dot.style.marginRight = 5;
            dot.style.display = withDot ? DisplayStyle.Flex : DisplayStyle.None;
            tab.Add(dot);

            nameLabel = new Label(label);
            tab.Add(nameLabel);

            badge = new Label("");
            badge.style.marginLeft = 5;
            badge.style.color = BadgeColor;
            badge.style.unityFontStyleAndWeight = FontStyle.Bold;
            badge.style.display = DisplayStyle.None;
            tab.Add(badge);

            if (withDot) // device tabs are closable; the Server tab is pinned
            {
                var close = new Label("✕");
                close.style.marginLeft = 6;
                close.style.color = LogMuted;
                close.style.fontSize = 10;
                close.RegisterCallback<ClickEvent>(e => { CloseTab(id); e.StopPropagation(); });
                tab.Add(close);
            }

            tab.RegisterCallback<ClickEvent>(_ => SelectTab(id));
            return tab;
        }

        void StyleTab(VisualElement tab, bool selected)
        {
            tab.style.backgroundColor = selected ? TabSelectedBg : TabIdleBg;
        }

        void SelectTab(string id)
        {
            selectedTabId = id;
            StyleTab(serverTab, id == ServerTabId);
            foreach (var kv in deviceTabs) StyleTab(kv.Value.tab, kv.Key == id);
            lastStatusPollAt = 0; // pull the newly selected device's log promptly
            UpdateDeviceInfo();
            UpdateLogView();
        }

        // Close (remove) a device tab. It stays closed until the window is
        // rebuilt (domain reload) - the device keeps logging on the server.
        void CloseTab(string id)
        {
            if (deviceTabs.TryGetValue(id, out var d))
            {
                tabStrip?.Remove(d.tab);
                deviceTabs.Remove(id);
            }
            closedTabs.Add(id);
            SaveClosedTabs(); // persist so it stays closed across domain reloads (compiles)
            if (selectedTabId == id) SelectTab(ServerTabId);
        }

        // closedTabs lives in SessionState (not a plain field) so closing a tab
        // sticks across domain reloads — otherwise every recompile rebuilt the
        // window with an empty set and PollDevices re-added the old (still
        // server-known) sessions. SessionState clears on full editor restart,
        // which is the right scope: a fresh editor session can show them again.
        const string ClosedTabsKey = "MakeGamesPlay.WebBuildHost.ClosedTabs";

        void LoadClosedTabs()
        {
            var saved = SessionState.GetString(ClosedTabsKey, "");
            if (string.IsNullOrEmpty(saved)) return;
            foreach (var id in saved.Split('\n'))
                if (!string.IsNullOrEmpty(id)) closedTabs.Add(id);
        }

        void SaveClosedTabs() => SessionState.SetString(ClosedTabsKey, string.Join("\n", closedTabs));

        // ── Polling (called from OnEditorUpdate, ~1 Hz) ──

        void PollDevices()
        {
            if (!IsRunning || controlPort <= 0) return;

            if (TryHttpGet(ControlBase() + "devices", ControlTimeoutMs, out var body))
            {
                CtrlDevices parsed = null;
                try { parsed = JsonUtility.FromJson<CtrlDevices>(body); } catch { }
                if (parsed != null && parsed.devices != null)
                {
                    foreach (var cd in parsed.devices)
                    {
                        if (cd == null || string.IsNullOrEmpty(cd.id)) continue;
                        if (closedTabs.Contains(cd.id)) continue; // user closed this tab
                        if (!deviceTabs.TryGetValue(cd.id, out var d))
                        {
                            d = new DeviceTab { id = cd.id };
                            d.tab = MakeTabElement(cd.id, cd.id, true, out d.dot, out d.nameLabel, out d.badge);
                            StyleTab(d.tab, false);
                            deviceTabs[cd.id] = d;
                            tabStrip?.Add(d.tab);
                        }
                        d.label = string.IsNullOrEmpty(cd.label) ? cd.id : cd.label;
                        d.ip = cd.ip;
                        d.live = cd.live;
                        d.errors = cd.errors;
                        d.warns = cd.warns;
                        d.ua = cd.ua;
                        d.w = cd.w; d.h = cd.h; d.dpr = cd.dpr;
                        d.nameLabel.text = d.label;
                        d.dot.style.backgroundColor = d.live ? DotLive : DotStale;
                        bool showBadge = d.errors > 0 && d.id != selectedTabId;
                        d.badge.style.display = showBadge ? DisplayStyle.Flex : DisplayStyle.None;
                        if (showBadge) d.badge.text = d.errors.ToString();
                    }
                }
            }

            // Pull new log lines only for the visible device tab (others keep
            // their badge/dot from the device list; their logs load on click).
            if (selectedTabId != ServerTabId && deviceTabs.TryGetValue(selectedTabId, out var sel))
            {
                string url = ControlBase() + "log?id=" + Uri.EscapeDataString(sel.id) + "&since=" + sel.cursor;
                if (TryHttpGet(url, ControlTimeoutMs, out var lbody))
                {
                    CtrlLogResp lr = null;
                    try { lr = JsonUtility.FromJson<CtrlLogResp>(lbody); } catch { }
                    if (lr != null)
                    {
                        sel.everFetched = true;
                        if (lr.lines != null && lr.lines.Length > 0)
                        {
                            foreach (var ln in lr.lines)
                            {
                                sel.lines.Add(ln);
                                if (ln.seq > sel.cursor) sel.cursor = ln.seq;
                            }
                            if (sel.lines.Count > DeviceLogMaxLines)
                                sel.lines.RemoveRange(0, sel.lines.Count - DeviceLogMaxLines);
                            UpdateLogView();
                        }
                    }
                }
            }
        }

        string ControlBase() => "http://127.0.0.1:" + controlPort + "/__webhost/";

        static bool TryHttpGet(string url, int timeoutMs, out string body)
        {
            body = null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Method = "GET";
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream()))
                {
                    body = sr.ReadToEnd();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        // Hook for UpdateUI - per-tab visuals are refreshed in PollDevices, so
        // this is currently a no-op kept for symmetry / future use.
        void UpdateTabs() { }

        // ── Log rendering ──

        void UpdateLogView()
        {
            if (logList == null) return;

            bool isServer = selectedTabId == ServerTabId;
            // Level filters only apply to device logs (server lines have no
            // structured level).
            if (filterRow != null) filterRow.style.display = isServer ? DisplayStyle.None : DisplayStyle.Flex;

            // Cheap signature so we only rebuild rows when something changes.
            string sig;
            if (isServer)
            {
                int c; lock (outputLines) c = outputLines.Count;
                sig = "S:" + c + ":" + (serveLogTail?.Length ?? 0);
            }
            else if (deviceTabs.TryGetValue(selectedTabId, out var ds))
            {
                sig = "D:" + ds.id + ":" + ds.lines.Count + ":" + ds.cursor;
            }
            else sig = "?";
            sig += "|q:" + searchText + "|f:" + (showLog ? 1 : 0) + (showWarn ? 1 : 0) + (showError ? 1 : 0) + (collapse ? 1 : 0);
            if (sig == logSig) return;
            logSig = sig;

            logRows.Clear();
            if (isServer)
            {
                lock (outputLines)
                    foreach (var l in outputLines)
                        if (MatchesSearch(l)) logRows.Add(new LogRow { frame = "", time = "", text = l, color = LogDefault });
                if (!string.IsNullOrEmpty(serveLogTail))
                    foreach (var l in serveLogTail.Split('\n'))
                        if (MatchesSearch(l)) logRows.Add(new LogRow { frame = "", time = "", text = l, color = ServerLineColor(l) });
            }
            else if (deviceTabs.TryGetValue(selectedTabId, out var d))
            {
                int cLog = 0, cWarn = 0, cErr = 0;
                if (!d.everFetched && d.lines.Count == 0)
                {
                    logRows.Add(new LogRow { frame = "", time = "", text = "(waiting for logs…)", color = LogMuted });
                }
                else
                {
                    string prevKey = null; int run = 0;
                    foreach (var ln in d.lines)
                    {
                        if (ln.level == "error") cErr++; else if (ln.level == "warn") cWarn++; else cLog++;
                        if (!ShowLevel(ln.level)) continue;
                        string msg = (ln.msg ?? "").TrimEnd();
                        if (!MatchesSearch(msg)) continue;

                        string key = ln.level + "" + msg;
                        if (collapse && key == prevKey && logRows.Count > 0)
                        {
                            run++;
                            var last = logRows[logRows.Count - 1];
                            last.frame = ln.frame.ToString();
                            last.time = FormatTs(ln.ts);
                            last.text = msg + "  ×" + run;
                            logRows[logRows.Count - 1] = last;
                            continue;
                        }
                        run = 1; prevKey = key;
                        logRows.Add(new LogRow { frame = ln.frame.ToString(), time = FormatTs(ln.ts), text = msg, color = LevelColor(ln.level) });
                    }
                }
                if (logChip != null)
                {
                    logChip.text = "Log " + cLog;
                    warnChip.text = "Warn " + cWarn;
                    errChip.text = "Error " + cErr;
                    ChipStyle(logChip, showLog, new Color(0.40f, 0.40f, 0.46f, 0.7f));
                    ChipStyle(warnChip, showWarn, new Color(0.70f, 0.55f, 0.15f, 0.8f));
                    ChipStyle(errChip, showError, new Color(0.70f, 0.27f, 0.27f, 0.85f));
                    ChipStyle(collapseChip, collapse, new Color(0.24f, 0.37f, 0.59f, 1f));
                }
            }

            logList.RefreshItems();
            if (autoScroll && logRows.Count > 0)
                logList.schedule.Execute(() => { if (logRows.Count > 0) logList.ScrollToItem(logRows.Count - 1); }).StartingIn(0);
        }

        void ClearSelectedTab()
        {
            if (selectedTabId == ServerTabId)
            {
                lock (outputLines) outputLines.Clear();
                serveLogTail = "";
            }
            else if (deviceTabs.TryGetValue(selectedTabId, out var d))
            {
                // Clear the SERVER ring buffer too (not just the editor view),
                // then resync the cursor to 0 so new lines flow from a clean
                // slate. Without the server clear the storage kept growing; and
                // resetting the cursor to match the server's reset seq removes
                // any chance of a stale cursor stalling new lines.
                if (controlPort > 0)
                    TryHttpGet(ControlBase() + "clear?id=" + Uri.EscapeDataString(d.id), ControlTimeoutMs, out _);
                d.lines.Clear();
                d.cursor = 0;
                d.everFetched = false;
            }
            logSig = null;
            UpdateLogView();
        }

        // Copy the selected rows (or all rows if nothing is selected) of the
        // current tab. Wired to both the Copy button and Ctrl/Cmd+C.
        void CopySelectedRows()
        {
            var sb = new StringBuilder();
            var indices = new List<int>(logList.selectedIndices);
            if (indices.Count > 0)
            {
                indices.Sort();
                foreach (var i in indices)
                    if (i >= 0 && i < logRows.Count) sb.AppendLine(RowCopyText(logRows[i]));
            }
            else
            {
                foreach (var r in logRows) sb.AppendLine(RowCopyText(r));
            }
            EditorGUIUtility.systemCopyBuffer = sb.ToString();
        }

        static string RowCopyText(LogRow r) => (string.IsNullOrEmpty(r.time) ? "" : r.time + "  ") + r.text;

        // Save the current tab's full log to a text file (button sits next to
        // Copy). Device tabs save the full buffered log with a metadata header;
        // the Server tab saves the host/server output.
        void SaveSelectedTab()
        {
            string defaultName, content;
            if (selectedTabId == ServerTabId)
            {
                var sb = new StringBuilder();
                lock (outputLines)
                    foreach (var l in outputLines) sb.AppendLine(l);
                if (!string.IsNullOrEmpty(serveLogTail)) sb.AppendLine(serveLogTail);
                defaultName = "webhost-server-log.txt";
                content = sb.ToString();
            }
            else if (deviceTabs.TryGetValue(selectedTabId, out var d))
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Web device log — " + d.label);
                if (!string.IsNullOrEmpty(d.ua)) sb.AppendLine("# UA: " + d.ua);
                sb.AppendLine("# Screen: " + d.w + "×" + d.h + " @" + d.dpr + "x");
                if (!string.IsNullOrEmpty(d.ip)) sb.AppendLine("# IP: " + d.ip);
                sb.AppendLine();
                foreach (var ln in d.lines)
                    sb.AppendLine(FormatTs(ln.ts) + "  " + (ln.level ?? "log").ToUpperInvariant() + "  " + ln.msg);
                defaultName = SanitizeFileName(d.label) + "-log.txt";
                content = sb.ToString();
            }
            else return;

            var path = EditorUtility.SaveFilePanel("Save logs", "", defaultName, "txt");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                File.WriteAllText(path, content);
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception e)
            {
                Debug.LogError("[WebBuildHost] Save logs failed: " + e.Message);
            }
        }

        static string SanitizeFileName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "device";
            foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '-');
            return s.Replace(' ', '-').Replace('·', '-');
        }

        // ── Device actions (3c) ──

        void ReloadAll()
        {
            if (controlPort > 0) TryHttpGet(ControlBase() + "reload", ControlTimeoutMs, out _);
        }

        void IdentifySelected()
        {
            if (selectedTabId == ServerTabId || controlPort <= 0) return;
            TryHttpGet(ControlBase() + "identify?id=" + Uri.EscapeDataString(selectedTabId), ControlTimeoutMs, out _);
        }

        void CopyDeviceReport()
        {
            if (!deviceTabs.TryGetValue(selectedTabId, out var d)) return;
            var sb = new StringBuilder();
            sb.AppendLine("### Web device log — " + d.label);
            sb.AppendLine();
            if (!string.IsNullOrEmpty(d.ua)) sb.AppendLine("- UA: " + d.ua);
            sb.AppendLine("- Screen: " + d.w + "×" + d.h + " @" + d.dpr + "x");
            if (!string.IsNullOrEmpty(d.ip)) sb.AppendLine("- IP: " + d.ip);
            sb.AppendLine();
            sb.AppendLine("```");
            foreach (var ln in d.lines)
                sb.AppendLine(FormatTs(ln.ts) + "  " + (ln.level ?? "log").ToUpperInvariant() + "  " + ln.msg);
            sb.AppendLine("```");
            EditorGUIUtility.systemCopyBuffer = sb.ToString();
        }

        void UpdateDeviceInfo()
        {
            if (deviceInfoRow == null) return;
            bool isDevice = selectedTabId != ServerTabId && deviceTabs.TryGetValue(selectedTabId, out var d);
            deviceInfoRow.style.display = isDevice ? DisplayStyle.Flex : DisplayStyle.None;
            if (isDevice && deviceTabs.TryGetValue(selectedTabId, out var dev))
            {
                string info = dev.w + "×" + dev.h + " @" + dev.dpr + "x";
                if (!string.IsNullOrEmpty(dev.ip)) info += "  ·  " + dev.ip;
                if (!string.IsNullOrEmpty(dev.ua)) info += "  ·  " + (dev.ua.Length > 64 ? dev.ua.Substring(0, 64) + "…" : dev.ua);
                deviceInfoLabel.text = info;
            }
        }

        bool MatchesSearch(string s)
        {
            if (string.IsNullOrEmpty(searchText)) return true;
            return s != null && s.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        bool ShowLevel(string lvl) => lvl == "error" ? showError : lvl == "warn" ? showWarn : showLog;

        static Color LevelColor(string lvl) => lvl == "error" ? LogError : lvl == "warn" ? LogWarn : LogDefault;

        static Color ServerLineColor(string l)
        {
            if (string.IsNullOrEmpty(l)) return LogDefault;
            if (l.Contains(" ERROR ")) return LogError;
            if (l.Contains(" WARN ")) return LogWarn;
            if (l.StartsWith("[host]") || l.Contains("[tunnel]")) return LogMuted;
            return LogDefault;
        }

        static string FormatTs(long unixMs)
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString("HH:mm:ss"); }
            catch { return ""; }
        }
    }
}
