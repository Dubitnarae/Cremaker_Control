// 화면 — 왼쪽 상태, 가운데 온도 그래프 + 사건 기록/통신 콘솔, 오른쪽 조작.
// 통신 스레드는 받은 줄을 큐에 넣기만 하고, 해석·감지·로그·화면 갱신은 0.2초 타이머에서 한꺼번에 한다
// (줄마다 화면을 부르면 화면 스레드가 막힌다 — RSP_Logger 에서 겪은 일).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

namespace CremakerControl
{
    class MainForm : Form
    {
        public bool RestartAfterUpdate;

        Link link;
        LogWriter log;
        readonly PrinterState st = new PrinterState();
        Detector det = new Detector();
        readonly ConcurrentQueue<Tuple<DateTime, string, string>> rx = new ConcurrentQueue<Tuple<DateTime, string, string>>();
        readonly ConcurrentQueue<string> infos = new ConcurrentQueue<string>();
        readonly Timer tick = new Timer { Interval = 200 };
        DateTime lastStateRow, lastPoll, lastChart;
        string fwName = "";
        bool wasConnected;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Font UiFont = new Font("맑은 고딕", 9f);
        static readonly Font BigFont = new Font("맑은 고딕", 15f, FontStyle.Bold);

        ComboBox cbPort;
        Button btnConnect, btnEstop;
        Label lbConn, lbNozzle, lbBed, lbPos, lbMode, lbSd, lbSpeed, lbFw, lbLock;
        Chart chart;
        ListView lvEvents;
        TextBox tbConsole, tbCmd;
        CheckBox cbHideReports;
        readonly List<Control> motionControls = new List<Control>();
        readonly List<Control> needConn = new List<Control>();
        double step = 10;
        NumericUpDown nuNozzle, nuBed, nuExtrude, nuFan, nuFeed, nuFlow;

        public MainForm()
        {
            Text = App.Name + " v" + App.Version;
            Font = UiFont;
            ClientSize = new Size(1280, 800);
            MinimumSize = new Size(1100, 700);
            StartPosition = FormStartPosition.CenterScreen;
            BuildUi();
            RefreshPorts();
            tick.Tick += (s, e) => OnTick();
            tick.Start();
            Shown += (s, e) => { CheckUpdate(); if (cbPort.Items.Count > 0 && cbPort.Text.Contains("3D 프린터")) Connect(); };
            FormClosing += (s, e) => Disconnect();
            SyncButtons();
        }

        // ---------- 화면 만들기 ----------
        Button Btn(Control parent, string text, int x, int y, int w, int h, Action onClick)
        {
            var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, h), UseVisualStyleBackColor = true };
            b.Click += (s, e) => onClick();
            parent.Controls.Add(b);
            return b;
        }

        NumericUpDown Num(Control parent, int x, int y, int w, decimal min, decimal max, decimal val, int dec)
        {
            var n = new NumericUpDown { Location = new Point(x, y), Width = w, Minimum = min, Maximum = max, Value = val, DecimalPlaces = dec, TextAlign = HorizontalAlignment.Right };
            parent.Controls.Add(n);
            return n;
        }

        Label Lbl(Control parent, string text, int x, int y, int w, int h)
        {
            var l = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), AutoSize = false };
            parent.Controls.Add(l);
            return l;
        }

        void BuildUi()
        {
            // 위 막대
            var top = new Panel { Dock = DockStyle.Top, Height = 40 };
            Lbl(top, "포트", 10, 12, 32, 20);
            cbPort = new ComboBox { Location = new Point(44, 8), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
            top.Controls.Add(cbPort);
            Btn(top, "새로고침", 270, 7, 70, 26, RefreshPorts);
            btnConnect = Btn(top, "연결", 346, 7, 80, 26, () => { if (link == null) Connect(); else Disconnect(); });
            lbConn = Lbl(top, "연결 안 됨", 436, 12, 330, 20);
            Btn(top, "로그 폴더 열기", 770, 7, 110, 26, OpenLogs);
            var about = Lbl(top, "v" + App.Version + "  ·  개발자 " + App.Author, 0, 12, 260, 20);
            about.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            about.TextAlign = ContentAlignment.TopRight;
            about.ForeColor = Color.DimGray;
            top.Resize += (s, e) => about.Left = top.Width - about.Width - 10;

            // 왼쪽 상태
            var left = new Panel { Dock = DockStyle.Left, Width = 250, Padding = new Padding(8) };
            int y = 8;
            Func<string, Label> Status = title =>
            {
                Lbl(left, title, 10, y, 230, 18).ForeColor = Color.DimGray;
                var v = Lbl(left, "-", 10, y + 18, 230, 30);
                v.Font = BigFont;
                y += 54;
                return v;
            };
            lbNozzle = Status("노즐 (현재 / 목표)");
            lbBed = Status("베드 (현재 / 목표)");
            Lbl(left, "위치 (mm)", 10, y, 230, 18).ForeColor = Color.DimGray;
            lbPos = Lbl(left, "-", 10, y + 18, 230, 92);
            lbPos.Font = new Font("Consolas", 13f, FontStyle.Bold);
            y += 114;
            lbMode = Status("상태");
            lbSd = Status("SD 출력");
            lbSpeed = Status("속도 / 유량");
            lbFw = Lbl(left, "", 10, y, 230, 60);
            lbFw.ForeColor = Color.DimGray;

            // 오른쪽 조작
            var right = new Panel { Dock = DockStyle.Right, Width = 350, AutoScroll = true };
            BuildControls(right);

            // 가운데: 그래프 + 탭
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330 };
            chart = new Chart { Dock = DockStyle.Fill, BackColor = Color.White };
            var area = new ChartArea("t");
            area.AxisX.LabelStyle.Format = "HH:mm:ss";
            area.AxisX.MajorGrid.LineColor = Color.Gainsboro;
            area.AxisY.MajorGrid.LineColor = Color.Gainsboro;
            area.AxisY.Title = "°C";
            area.AxisY.Minimum = 0;
            chart.ChartAreas.Add(area);
            chart.Legends.Add(new Legend { Docking = Docking.Top, Alignment = StringAlignment.Center });
            AddSeries("노즐", Color.OrangeRed, 2);
            AddSeries("노즐 목표", Color.FromArgb(255, 170, 150), 1);
            AddSeries("베드", Color.RoyalBlue, 2);
            AddSeries("베드 목표", Color.FromArgb(150, 170, 240), 1);
            split.Panel1.Controls.Add(chart);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            var tpEv = new TabPage("사건 기록 (PC·프린터 조작)");
            lvEvents = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
            lvEvents.Columns.Add("시각", 80);
            lvEvents.Columns.Add("출처", 60);
            lvEvents.Columns.Add("내용", 520);
            tpEv.Controls.Add(lvEvents);
            var tpCon = new TabPage("통신 콘솔");
            tbConsole = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), BackColor = Color.White, WordWrap = false };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 32 };
            tbCmd = new TextBox { Location = new Point(4, 5), Width = 360 };
            tbCmd.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SendConsole(); } };
            bar.Controls.Add(tbCmd);
            needConn.Add(Btn(bar, "보내기", 370, 3, 70, 26, SendConsole));
            cbHideReports = new CheckBox { Text = "자동 보고 줄 숨기기", Location = new Point(450, 7), AutoSize = true, Checked = true };
            bar.Controls.Add(cbHideReports);
            tpCon.Controls.Add(tbConsole);
            tpCon.Controls.Add(bar);
            tabs.TabPages.Add(tpEv);
            tabs.TabPages.Add(tpCon);
            split.Panel2.Controls.Add(tabs);

            Controls.Add(split);
            Controls.Add(right);
            Controls.Add(left);
            Controls.Add(top);
        }

        void AddSeries(string name, Color c, int width)
        {
            var s = new Series(name) { ChartType = SeriesChartType.FastLine, Color = c, BorderWidth = width, XValueType = ChartValueType.DateTime };
            chart.Series.Add(s);
        }

        void BuildControls(Panel p)
        {
            // 이동
            var g = new GroupBox { Text = "이동", Location = new Point(6, 4), Size = new Size(322, 236) };
            p.Controls.Add(g);
            var steps = new[] { 0.1, 1, 10, 50 };
            for (int i = 0; i < steps.Length; i++)
            {
                double v = steps[i];
                var rb = new RadioButton { Text = v.ToString(Inv) + " mm", Location = new Point(12 + i * 76, 20), AutoSize = true, Checked = v == step };
                rb.CheckedChanged += (s, e) => { if (((RadioButton)s).Checked) step = v; };
                g.Controls.Add(rb);
            }
            int bx = 12, by = 48, bw = 64, bh = 40;
            motionControls.Add(Btn(g, "Y+", bx + bw + 4, by, bw, bh, () => Jog("Y", 1)));
            motionControls.Add(Btn(g, "X-", bx, by + bh + 4, bw, bh, () => Jog("X", -1)));
            motionControls.Add(Btn(g, "홈\n전체", bx + bw + 4, by + bh + 4, bw, bh, () => Cmd("G28")));
            motionControls.Add(Btn(g, "X+", bx + 2 * (bw + 4), by + bh + 4, bw, bh, () => Jog("X", 1)));
            motionControls.Add(Btn(g, "Y-", bx + bw + 4, by + 2 * (bh + 4), bw, bh, () => Jog("Y", -1)));
            motionControls.Add(Btn(g, "Z+ (위)", bx + 3 * (bw + 4) + 10, by, bw + 14, bh, () => Jog("Z", 1)));
            motionControls.Add(Btn(g, "Z- (아래)", bx + 3 * (bw + 4) + 10, by + 2 * (bh + 4), bw + 14, bh, () => Jog("Z", -1)));
            int hy = by + 3 * (bh + 4) + 4;
            motionControls.Add(Btn(g, "홈 X", 12, hy, 70, 28, () => Cmd("G28 X")));
            motionControls.Add(Btn(g, "홈 Y", 86, hy, 70, 28, () => Cmd("G28 Y")));
            motionControls.Add(Btn(g, "홈 Z", 160, hy, 70, 28, () => Cmd("G28 Z")));
            motionControls.Add(Btn(g, "모터 끄기", 234, hy, 76, 28, () => Cmd("M84")));

            // 온도
            var t = new GroupBox { Text = "온도 (°C)", Location = new Point(6, 246), Size = new Size(322, 128) };
            p.Controls.Add(t);
            Lbl(t, "노즐", 12, 26, 40, 20);
            nuNozzle = Num(t, 54, 22, 70, 0, Machine.HotendMax, 200, 0);
            needConn.Add(Btn(t, "설정", 130, 20, 60, 26, () => Cmd("M104 S" + nuNozzle.Value.ToString(Inv))));
            needConn.Add(Btn(t, "끄기", 196, 20, 60, 26, () => Cmd("M104 S0")));
            Lbl(t, "베드", 12, 58, 40, 20);
            nuBed = Num(t, 54, 54, 70, 0, Machine.BedMax, 60, 0);
            needConn.Add(Btn(t, "설정", 130, 52, 60, 26, () => Cmd("M140 S" + nuBed.Value.ToString(Inv))));
            needConn.Add(Btn(t, "끄기", 196, 52, 60, 26, () => Cmd("M140 S0")));
            needConn.Add(Btn(t, "PLA 200/60", 12, 88, 92, 28, () => Cmd("M104 S200\nM140 S60")));
            // 펌웨어 예열값 ABS 는 베드 110 이지만 BED_MAXTEMP 100 - 여유 10 = 90 까지만 받는다
            needConn.Add(Btn(t, "ABS 240/90", 108, 88, 92, 28, () => Cmd("M104 S240\nM140 S90")));
            needConn.Add(Btn(t, "모두 냉각", 204, 88, 106, 28, () => Cmd("M104 S0\nM140 S0")));

            // 압출
            var x = new GroupBox { Text = "압출 (노즐 " + Machine.ExtrudeMin + " °C 이상)", Location = new Point(6, 380), Size = new Size(322, 62) };
            p.Controls.Add(x);
            nuExtrude = Num(x, 12, 26, 60, 1, 100, 10, 0);
            Lbl(x, "mm", 76, 29, 30, 20);
            motionControls.Add(Btn(x, "압출", 110, 22, 96, 28, () => Extrude(1)));
            motionControls.Add(Btn(x, "되감기", 212, 22, 96, 28, () => Extrude(-1)));

            // 팬·속도·유량
            var f = new GroupBox { Text = "팬 · 속도 · 유량 (%)", Location = new Point(6, 448), Size = new Size(322, 124) };
            p.Controls.Add(f);
            Lbl(f, "팬", 12, 26, 60, 20);
            nuFan = Num(f, 74, 22, 60, 0, 100, 100, 0);
            needConn.Add(Btn(f, "적용", 140, 20, 60, 26, () => Cmd("M106 S" + ((int)Math.Round((double)nuFan.Value * 2.55)).ToString(Inv))));
            needConn.Add(Btn(f, "끄기", 206, 20, 60, 26, () => Cmd("M107")));
            Lbl(f, "속도", 12, 58, 60, 20);
            nuFeed = Num(f, 74, 54, 60, 10, 300, 100, 0);
            needConn.Add(Btn(f, "적용", 140, 52, 60, 26, () => Cmd("M220 S" + nuFeed.Value.ToString(Inv) + "\nM220")));
            Lbl(f, "유량", 12, 90, 60, 20);
            nuFlow = Num(f, 74, 86, 60, 50, 200, 100, 0);
            needConn.Add(Btn(f, "적용", 140, 84, 60, 26, () => Cmd("M221 S" + nuFlow.Value.ToString(Inv) + "\nM221")));

            lbLock = Lbl(p, "", 10, 578, 318, 36);
            lbLock.ForeColor = Color.DarkOrange;

            btnEstop = new Button { Text = "비상 정지 (M112)", Location = new Point(6, 616), Size = new Size(322, 56), BackColor = Color.FromArgb(220, 40, 40), ForeColor = Color.White, Font = new Font("맑은 고딕", 13f, FontStyle.Bold), FlatStyle = FlatStyle.Flat };
            btnEstop.Click += (s, e) => Estop();
            p.Controls.Add(btnEstop);
            needConn.Add(btnEstop);
            Lbl(p, "비상 정지 후에는 프린터 전원을 껐다 켜거나 리셋해야 합니다.", 10, 676, 318, 34).ForeColor = Color.DimGray;
        }

        // ---------- 연결 ----------
        void RefreshPorts()
        {
            string cur = cbPort.SelectedItem is KeyValuePair<string, string> ? ((KeyValuePair<string, string>)cbPort.SelectedItem).Key : null;
            cbPort.Items.Clear();
            foreach (var kv in Link.ListPorts()) cbPort.Items.Add(kv);
            cbPort.DisplayMember = "Value";
            if (cbPort.Items.Count == 0) return;
            int idx = 0;
            for (int i = 0; i < cbPort.Items.Count; i++)
                if (((KeyValuePair<string, string>)cbPort.Items[i]).Key == cur) idx = i;
            cbPort.SelectedIndex = idx;
        }

        void Connect()
        {
            if (link != null || !(cbPort.SelectedItem is KeyValuePair<string, string>)) return;
            string port = ((KeyValuePair<string, string>)cbPort.SelectedItem).Key;
            try { log = new LogWriter(App.LogRoot, DateTime.Now); }
            catch (Exception e) { MessageBox.Show(this, "로그 폴더를 만들 수 없습니다:\n" + e.Message, App.Name); return; }
            det = new Detector();
            link = new Link(port);
            link.OnLine = (dir, line) => rx.Enqueue(Tuple.Create(DateTime.Now, dir, line));
            link.OnInfo = msg => infos.Enqueue(msg);
            link.Start();
            AddEvent(new Ev(DateTime.Now, "상태", "기록 시작 — " + port + ", 로그 " + log.Dir + "\\" + log.Prefix + "_*"));
            SyncButtons();
        }

        void Disconnect()
        {
            if (link == null) return;
            link.Stop();
            link = null;
            OnTick();   // 남은 줄 처리
            if (log != null) { AddEvent(new Ev(DateTime.Now, "상태", "기록 끝")); log.Dispose(); log = null; }
            wasConnected = false;
            lbConn.Text = "연결 안 됨";
            SyncButtons();
        }

        void OpenLogs()
        {
            System.IO.Directory.CreateDirectory(App.LogRoot);
            Process.Start("explorer.exe", "\"" + (log != null ? log.Dir : App.LogRoot) + "\"");
        }

        // ---------- 명령 ----------
        void Cmd(string cmds)
        {
            if (link == null) return;
            double ts = Ts(DateTime.Now);
            var shown = new List<string>();
            foreach (var c0 in cmds.Split('\n'))
            {
                string c = Link.StripComment(c0);
                if (c == "") continue;
                det.PcSent(Parser.KindOf(c), ts);
                if (!IsPoll(c)) shown.Add(c);
            }
            if (shown.Count > 0) AddEvent(new Ev(DateTime.Now, "PC", "보냄: " + string.Join(" · ", shown)));
            link.Send(cmds);
        }

        void Jog(string axis, int dir)
        {
            int f = axis == "Z" ? Machine.ZFeed : Machine.XyFeed;
            // 상대 이동 후 절대 좌표로 되돌린다 (BTT TFT 화면도 같은 방식). 범위 밖은 펌웨어 소프트 리밋이 막는다.
            Cmd("G91\nG0 " + axis + (dir * step).ToString("0.###", Inv) + " F" + f + "\nG90");
        }

        void Extrude(int dir)
        {
            if (st.HasTemp && st.T < Machine.ExtrudeMin)
            {
                MessageBox.Show(this, "노즐이 " + st.T.ToString("0", Inv) + " °C 입니다. " + Machine.ExtrudeMin + " °C 이상에서만 압출할 수 있습니다.", App.Name);
                return;
            }
            Cmd("M83\nG1 E" + (dir * (int)nuExtrude.Value).ToString(Inv) + " F" + Machine.EFeed + "\nM82");
        }

        void Estop()
        {
            if (link == null) return;
            link.Emergency();
            AddEvent(new Ev(DateTime.Now, "PC", "비상 정지 (M112) 보냄"));
        }

        void SendConsole()
        {
            string c = tbCmd.Text.Trim();
            if (c == "" || link == null) return;
            if (c.ToUpperInvariant().StartsWith("M112")) Estop();
            else Cmd(c);
            tbCmd.Clear();
        }

        // ---------- 주기 처리 ----------
        static double Ts(DateTime t) { return t.Ticks / (double)TimeSpan.TicksPerSecond; }

        void OnTick()
        {
            DateTime now = DateTime.Now;
            string info;
            while (infos.TryDequeue(out info)) AddEvent(new Ev(now, "상태", info));

            var sb = new StringBuilder();
            Tuple<DateTime, string, string> item;
            int n = 0;
            while (n++ < 2000 && rx.TryDequeue(out item))
            {
                DateTime t = item.Item1;
                string dir = item.Item2, line = item.Item3;
                if (log != null) log.Serial(t, dir, line);
                if (dir == "<")
                {
                    int what = Parser.Apply(line, st);
                    foreach (var e in det.Update(st, what, Ts(t), t)) AddEvent(e);
                    if (line.StartsWith("FIRMWARE_NAME:")) { fwName = line; ShowFw(); }
                    if (line.StartsWith("Error:") || line.Contains("Printer halted") || line.Contains("Thermal Runaway") || line.Contains("MAXTEMP") || line.Contains("MINTEMP"))
                        AddEvent(new Ev(t, "오류", line));
                    else if (line.StartsWith("echo:") && !line.StartsWith("echo:busy") && !line.Contains("Flow:"))
                        AddEvent(new Ev(t, "프린터", line.Substring(5).Trim()));
                }
                if (!(cbHideReports.Checked && (dir == "<" ? Parser.IsReport(line) : IsPoll(line))))
                    sb.Append(t.ToString("HH:mm:ss.fff", Inv)).Append(' ').Append(dir).Append(' ').Append(line).Append("\r\n");
            }
            foreach (var e in det.Update(st, Parser.None, Ts(now), now)) AddEvent(e);   // 이동 멈춤 판정
            if (sb.Length > 0) AppendConsole(sb.ToString());

            bool conn = link != null && link.Connected;
            if (conn != wasConnected) { wasConnected = conn; SyncButtons(); }
            if (link != null) lbConn.Text = conn ? link.Port + " 연결됨 · 기록 중" : link.Port + " 연결 기다리는 중 (3초마다 다시 시도)";

            if (conn && (now - lastPoll).TotalSeconds >= 5 && link.Pending == 0)
            {
                lastPoll = now;
                link.Send("M220\nM221");   // 화면에서 바꾼 속도·유량은 자동 보고가 없어 물어본다
            }
            if (log != null && conn && st.HasTemp && (now - lastStateRow).TotalSeconds >= 1)
            {
                lastStateRow = now;
                log.State(now, st);
            }
            if (st.HasTemp && (now - lastChart).TotalSeconds >= 1)
            {
                lastChart = now;
                AddChart(now);
            }
            ShowState(now);
        }

        static bool IsPoll(string c) { return c == "M220" || c == "M221"; }

        void AppendConsole(string text)
        {
            if (tbConsole.TextLength > 300000)
            {
                string keep = tbConsole.Text.Substring(tbConsole.TextLength - 150000);
                tbConsole.Text = keep.Substring(keep.IndexOf('\n') + 1);
            }
            tbConsole.AppendText(text);
        }

        void AddChart(DateTime now)
        {
            double x = now.ToOADate();
            var vals = new[] { st.T, st.Tt, st.B, st.Bt };
            for (int i = 0; i < 4; i++)
            {
                var pts = chart.Series[i].Points;
                pts.AddXY(x, vals[i]);
                while (pts.Count > 600) pts.RemoveAt(0);   // 10분
            }
            var ax = chart.ChartAreas[0].AxisX;
            ax.Minimum = now.AddMinutes(-10).ToOADate();
            ax.Maximum = now.ToOADate();
        }

        void ShowState(DateTime now)
        {
            if (st.HasTemp)
            {
                SetText(lbNozzle, st.T.ToString("0.0", Inv) + " / " + st.Tt.ToString("0", Inv) + " °C");
                SetText(lbBed, st.B.ToString("0.0", Inv) + " / " + st.Bt.ToString("0", Inv) + " °C");
            }
            if (st.HasPos)
                SetText(lbPos, string.Format(Inv, "X {0,8:0.00}\nY {1,8:0.00}\nZ {2,8:0.00}\nE {3,8:0.00}", st.X, st.Y, st.Z, st.E));
            bool printing = det.LooksPrinting(st, Ts(now));
            SetText(lbMode, link == null || !link.Connected ? "-" : printing ? "출력 중" : det.Moving ? "이동 중" : st.Tt > 0 || st.Bt > 0 ? "가열 · 대기" : "대기");
            SetText(lbSd, st.SdPrinting ? st.SdPercent.ToString("0.0", Inv) + " %" : "출력 안 함");
            SetText(lbSpeed, (st.Feed >= 0 ? st.Feed + " %" : "-") + " / " + (st.Flow >= 0 ? st.Flow + " %" : "-"));
            bool conn = link != null && link.Connected;
            bool unlock = conn && !printing;
            foreach (var c in motionControls) if (c.Enabled != unlock) c.Enabled = unlock;
            SetText(lbLock, conn && printing ? "출력 중으로 보여 이동·홈·압출 버튼을 잠갔습니다.\n(온도·팬·속도·비상 정지는 됩니다)" : "");
        }

        static void SetText(Label l, string s) { if (l.Text != s) l.Text = s; }   // 값이 바뀔 때만 다시 그린다

        void ShowFw()
        {
            var m = System.Text.RegularExpressions.Regex.Match(fwName, @"FIRMWARE_NAME:(.*?) SOURCE_CODE_URL.*MACHINE_TYPE:(\S+)");
            lbFw.Text = m.Success ? "펌웨어: " + m.Groups[1].Value + "\n기종: " + m.Groups[2].Value : "";
        }

        void SyncButtons()
        {
            bool conn = link != null && link.Connected;
            btnConnect.Text = link == null ? "연결" : "끊기";
            cbPort.Enabled = link == null;
            foreach (var c in needConn) c.Enabled = conn;
            if (!conn) foreach (var c in motionControls) c.Enabled = false;
        }

        void AddEvent(Ev e)
        {
            if (log != null) log.Event(e);
            var it = new ListViewItem(new[] { e.Time.ToString("HH:mm:ss", Inv), e.Source, e.Text });
            if (e.Source == "프린터") it.ForeColor = Color.RoyalBlue;
            else if (e.Source == "오류") { it.ForeColor = Color.White; it.BackColor = Color.Firebrick; }
            else if (e.Source == "상태") it.ForeColor = Color.DimGray;
            lvEvents.BeginUpdate();
            lvEvents.Items.Add(it);
            while (lvEvents.Items.Count > 3000) lvEvents.Items.RemoveAt(0);
            lvEvents.EndUpdate();
            it.EnsureVisible();
        }

        // ---------- 업데이트 ----------
        void CheckUpdate()
        {
            Task.Run(() =>
            {
                try
                {
                    var found = Updater.Check();
                    if (found != null) BeginInvoke((Action)(() => OnUpdate(found)));
                }
                catch { }   // 인터넷이 없거나 저장소가 아직 없을 때 — 조용히 넘어간다
            });
        }

        void OnUpdate(Updater.Found found)
        {
            if (det.LooksPrinting(st, Ts(DateTime.Now))) return;   // 출력 중에는 묻지 않는다
            string notes = found.Notes.Length > 800 ? found.Notes.Substring(0, 800) : found.Notes;
            if (MessageBox.Show(this, "새 버전 v" + found.Ver + " 이 있습니다. 지금 설치할까요?\n\n" + notes, "업데이트",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Cursor = Cursors.WaitCursor;
            try { Updater.Install(found); }
            catch (Exception e)
            {
                Cursor = Cursors.Default;
                MessageBox.Show(this, e.Message, "업데이트 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            RestartAfterUpdate = true;
            Close();
        }
    }
}
