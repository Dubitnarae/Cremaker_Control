// 프린터 상태 해석·조작 감지·로그 저장 — 화면과 통신 스레드에 묶이지 않은 부분 (--selftest 로 시험)
// 근거: 원본 펌웨어\[소스파일] 메인보드 펌웨어 V1.1 (Marlin 2.0.8.1)
//  - SERIAL_PORT 1 = TFT 화면, SERIAL_PORT_2 -1 = USB → 화면에서 보낸 명령·응답은 USB로 오지 않는다.
//  - AUTO_REPORT_TEMPERATURES / AUTO_REPORT_POSITION / AUTO_REPORT_SD_STATUS 는 report_port_mask = All
//    (libs/autoreport.h) 이라 화면 쪽 결과도 USB로 같이 온다. 그래서 '값이 바뀐 것'으로 화면 조작을 알아낸다.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CremakerControl
{
    // Configuration.h 에서 옮긴 안전 한계
    static class Machine
    {
        public const double XMin = -8, XMax = 220, YMin = -6, YMax = 220, ZMin = 0, ZMax = 260;
        public const int HotendMax = 275 - 15;   // HEATER_0_MAXTEMP 275 - Marlin 이 막는 여유(HOTEND_OVERSHOOT 15)
        public const int BedMax = 100 - 10;      // BED_MAXTEMP 100 - BED_OVERSHOOT 10
        public const int ExtrudeMin = 170;       // EXTRUDE_MINTEMP
        public const int XyFeed = 3000, ZFeed = 600, EFeed = 300;   // mm/min (최대 200·200·12·120 mm/s 안쪽)
    }

    class PrinterState
    {
        public double T = double.NaN, Tt, B = double.NaN, Bt;
        public int TPow, BPow;
        public double X = double.NaN, Y, Z, E;
        public long CX, CY, CZ;   // 모터 스텝 카운트 (M114 "Count") — 홈 동작처럼 좌표가 그대로일 때도 바뀐다
        public bool HasCount;
        public bool SdPrinting;
        public long SdPos, SdSize;
        public int Feed = -1, Flow = -1;

        public bool HasTemp { get { return !double.IsNaN(T); } }
        public bool HasPos { get { return !double.IsNaN(X); } }
        public double SdPercent { get { return SdSize > 0 ? 100.0 * SdPos / SdSize : 0; } }

        public PrinterState Clone() { return (PrinterState)MemberwiseClone(); }
    }

    // Marlin 응답 한 줄 해석. 상태를 고쳤으면 무엇을 고쳤는지 돌려준다.
    static class Parser
    {
        public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Regex RxT = new Regex(@"(?:^|\s)T:(-?[\d.]+)\s*/(-?[\d.]+)");
        static readonly Regex RxB = new Regex(@"(?:^|\s)B:(-?[\d.]+)\s*/(-?[\d.]+)");
        static readonly Regex RxTPow = new Regex(@"(?:^|\s)@:(\d+)");
        static readonly Regex RxBPow = new Regex(@"B@:(\d+)");
        static readonly Regex RxPos = new Regex(@"^X:(-?[\d.]+) Y:(-?[\d.]+) Z:(-?[\d.]+) E:(-?[\d.]+)(?: Count X:(-?\d+) Y:(-?\d+) Z:(-?\d+))?");
        static readonly Regex RxSd = new Regex(@"SD printing byte (\d+)/(\d+)");
        static readonly Regex RxFeed = new Regex(@"FR:(\d+)%");
        static readonly Regex RxFlow = new Regex(@"E\d+ Flow: ?(\d+)%");

        static double D(string s) { return double.Parse(s, Inv); }

        public const int None = 0, Temp = 1, Pos = 2, Sd = 4, Speed = 8, Busy = 16;

        public static int Apply(string line, PrinterState s)
        {
            Match m;
            // G28·G29·M109 처럼 오래 걸리는 명령 중 2초마다 온다 (화면에서 보낸 명령이어도 USB로 온다)
            if (line.StartsWith("echo:busy")) return Busy;
            if ((m = RxT.Match(line)).Success && (line.StartsWith("T:") || line.StartsWith("ok T:") || line.StartsWith(" T:")))
            {
                s.T = D(m.Groups[1].Value); s.Tt = D(m.Groups[2].Value);
                if ((m = RxB.Match(line)).Success) { s.B = D(m.Groups[1].Value); s.Bt = D(m.Groups[2].Value); }
                if ((m = RxTPow.Match(line)).Success) s.TPow = int.Parse(m.Groups[1].Value, Inv);
                if ((m = RxBPow.Match(line)).Success) s.BPow = int.Parse(m.Groups[1].Value, Inv);
                return Temp;
            }
            if ((m = RxPos.Match(line)).Success)
            {
                s.X = D(m.Groups[1].Value); s.Y = D(m.Groups[2].Value); s.Z = D(m.Groups[3].Value); s.E = D(m.Groups[4].Value);
                if (m.Groups[5].Success)
                {
                    s.CX = long.Parse(m.Groups[5].Value, Inv); s.CY = long.Parse(m.Groups[6].Value, Inv); s.CZ = long.Parse(m.Groups[7].Value, Inv);
                    s.HasCount = true;
                }
                return Pos;
            }
            if ((m = RxSd.Match(line)).Success)
            {
                s.SdPrinting = true; s.SdPos = long.Parse(m.Groups[1].Value, Inv); s.SdSize = long.Parse(m.Groups[2].Value, Inv);
                return Sd;
            }
            if (line.Contains("Not SD printing") || line.Contains("Done printing file"))
            {
                s.SdPrinting = false;
                return Sd;
            }
            if ((m = RxFeed.Match(line)).Success) { s.Feed = int.Parse(m.Groups[1].Value, Inv); return Speed; }
            if ((m = RxFlow.Match(line)).Success) { s.Flow = int.Parse(m.Groups[1].Value, Inv); return Speed; }
            return None;
        }

        // M115 → 메인보드 펌웨어 판정. 원본 펌웨어 폴더의 bin 문자열로 확인한 빌드 날짜:
        // v1.1 = "Mar  7 2022" (MACHINE_TYPE:Cremaker), v1.2 = "Jul 12 2022" (MACHINE_TYPE:Cremaker_V1.2).
        // v1.1 → v1.2 차이는 기종 이름·조명 상태 문구·레벨링 문구뿐이라 이 프로그램 동작은 같다.
        // 화면(TFT) v1.3 → v1.4 도 버전 표시만 바뀌었고, 화면은 USB 와 따로 통신하므로 영향 없음.
        public const string LatestBoard = "v1.2";
        static readonly Regex RxFw = new Regex(@"FIRMWARE_NAME:Marlin (\S+) \((\w{3} +\d+ \d{4})[^)]*\).*MACHINE_TYPE:(\S+)");

        public static string BoardVersion(string m115, out bool latest, out string detail)
        {
            latest = false;
            detail = "";
            var m = RxFw.Match(m115);
            if (!m.Success) return "";
            string date = Regex.Replace(m.Groups[2].Value, " +", " ");
            DateTime d;
            string iso = DateTime.TryParseExact(date, "MMM d yyyy", Inv, DateTimeStyles.None, out d) ? d.ToString("yyyy-MM-dd", Inv) : date;
            detail = "Marlin " + m.Groups[1].Value + ", " + iso + " 빌드, 기종 " + m.Groups[3].Value;
            if (date == "Jul 12 2022" || m.Groups[3].Value == "Cremaker_V1.2") { latest = true; return "v1.2"; }
            if (date == "Mar 7 2022") return "v1.1";
            return "확인 안 된 빌드";
        }

        // 프린터를 멈추게 하는 오류 줄. 이 펌웨어는 한국어라 kill 메시지가 한글로 온다
        // (language_ko_KR.h: "원점 이동 실패, 재부팅 필요", "최대 온도 초과" 등). 이 줄 뒤로는 재부팅 전까지 응답이 없다.
        public static bool IsError(string line)
        {
            return line.StartsWith("Error:") || line.Contains("Printer halted") || line.Contains("Thermal Runaway") || line.Contains("MAXTEMP") || line.Contains("MINTEMP")
                || line.Contains("Homing Failed") || line.Contains("실패") || line.Contains("재부팅") || line.Contains("초과") || line.Contains("미달") || line.Contains("폭주");
        }

        // 자동 보고처럼 계속 오는 줄 (콘솔에서 숨기기용)
        public static bool IsReport(string line)
        {
            return line.StartsWith("T:") || line.StartsWith(" T:") || line.StartsWith("X:") || line.Contains("SD printing") || line == "ok"
                || line.StartsWith("FR:") || line.Contains(" Flow: ") || line.StartsWith("echo:busy");
        }

        // PC에서 보낸 명령이 무엇을 바꾸는지 (출처 판단용)
        public static string KindOf(string cmd)
        {
            string c = cmd.Trim().ToUpperInvariant();
            int sp = c.IndexOf(' ');
            string w = sp < 0 ? c : c.Substring(0, sp);
            switch (w)
            {
                case "M104": case "M109": return "hotend";
                case "M140": case "M190": return "bed";
                case "G0": case "G1": case "G2": case "G3": case "G28": case "G29": case "G92": case "M84": case "M18": return "move";
                case "M220": return c.Contains("S") ? "feed" : "";
                case "M221": return c.Contains("S") ? "flow" : "";
                case "M23": case "M24": case "M25": case "M524": return "sd";
            }
            return "";
        }
    }

    class Ev
    {
        public DateTime Time;
        public string Source, Text;   // Source: PC / 프린터 / 상태 / 오류
        public Ev(DateTime t, string src, string text) { Time = t; Source = src; Text = text; }
    }

    // 상태 변화 → 사건. PC가 최근에 같은 종류 명령을 보냈으면 출처 PC, 아니면 프린터(화면·SD 출력).
    class Detector
    {
        public double Window = 5;      // 초: PC 명령 뒤 이 시간 안의 변화는 PC가 한 것으로 본다
        public double StopAfter = 2.5; // 초: 위치가 이만큼 그대로면 이동 멈춤
        readonly Dictionary<string, double> lastPc = new Dictionary<string, double>();
        PrinterState prev;
        bool moving, tReached = true, bReached = true;
        double lastMoveTs = -1e9, lastETs = -1e9, moveStartE;
        string moveSrc = "";
        bool moveLogical, busy;
        double busyStart, lastBusy;
        long startCX, startCY, startCZ;
        int lastSdTenth = -1;

        public void PcSent(string kind, double ts) { if (kind != "") lastPc[kind] = ts; }

        string Src(string kind, double ts)
        {
            double t;
            return lastPc.TryGetValue(kind, out t) && ts - t <= Window ? "PC" : "프린터";
        }

        // 종류와 상관없이 PC가 최근에 명령을 보냈는지 (처리 중 판단용)
        string SrcAny(double ts)
        {
            foreach (var t in lastPc.Values) if (ts - t <= Window) return "PC";
            return "프린터";
        }

        static string F(double v) { return v.ToString("0.##", Parser.Inv); }

        // 출력 중으로 보이면 true (SD 출력, 또는 화면이 G-code를 흘려 보내 압출이 계속되는 중)
        public bool LooksPrinting(PrinterState s, double ts)
        {
            return s.SdPrinting || (ts - lastETs < 10 && s.Tt > 0);
        }

        public List<Ev> Update(PrinterState s, int what, double ts, DateTime now)
        {
            var evs = new List<Ev>();
            if (prev == null) { prev = s.Clone(); if (s.HasPos) moveStartE = s.E; return evs; }
            if ((what & Parser.Temp) != 0 && prev.HasTemp)
            {
                if (s.Tt != prev.Tt)
                {
                    evs.Add(new Ev(now, Src("hotend", ts), s.Tt == 0 ? "노즐 히터 끔" : "노즐 목표 " + F(prev.Tt) + " → " + F(s.Tt) + " °C"));
                    tReached = s.Tt == 0;
                }
                if (s.Bt != prev.Bt)
                {
                    evs.Add(new Ev(now, Src("bed", ts), s.Bt == 0 ? "베드 히터 끔" : "베드 목표 " + F(prev.Bt) + " → " + F(s.Bt) + " °C"));
                    bReached = s.Bt == 0;
                }
                if (!tReached && Math.Abs(s.T - s.Tt) <= 2) { tReached = true; evs.Add(new Ev(now, "상태", "노즐 목표 온도 도달 (" + F(s.T) + " °C)")); }
                if (!bReached && Math.Abs(s.B - s.Bt) <= 1.5) { bReached = true; evs.Add(new Ev(now, "상태", "베드 목표 온도 도달 (" + F(s.B) + " °C)")); }
            }
            if ((what & Parser.Pos) != 0 && prev.HasPos)
            {
                bool logical = s.X != prev.X || s.Y != prev.Y || s.Z != prev.Z || s.E != prev.E;
                bool counts = s.HasCount && prev.HasCount && (s.CX != prev.CX || s.CY != prev.CY || s.CZ != prev.CZ);
                if (s.E != prev.E) lastETs = ts;
                if (logical || counts)
                {
                    lastMoveTs = ts;
                    if (!moving)
                    {
                        moving = true; moveSrc = Src("move", ts); moveStartE = prev.E; moveLogical = false;
                        startCX = prev.CX; startCY = prev.CY; startCZ = prev.CZ;
                        evs.Add(new Ev(now, moveSrc, logical
                            ? "이동 시작 (X " + F(prev.X) + " Y " + F(prev.Y) + " Z " + F(prev.Z) + ")"
                            : "모터 움직임 시작 — 좌표는 그대로 (홈 동작 등)"));
                    }
                    moveLogical |= logical;
                }
            }
            if ((what & Parser.Busy) != 0)
            {
                if (!busy) { busy = true; busyStart = ts; evs.Add(new Ev(now, SrcAny(ts), "프린터 처리 중 (홈·레벨링·가열 대기 같은 오래 걸리는 명령)")); }
                lastBusy = ts;
            }
            if (busy && ts - lastBusy > 3.5)   // busy 는 2초마다 오므로 3.5초 안 오면 끝난 것
            {
                busy = false;
                evs.Add(new Ev(now, "상태", "처리 끝 (약 " + (lastBusy - busyStart + 2).ToString("0", Parser.Inv) + "초 걸림)"));
            }
            if (moving && !busy && ts - lastMoveTs >= StopAfter)   // 처리 중(홈 등)에는 잠깐 멈춰도 같은 동작으로 본다
            {
                moving = false;
                double de = s.E - moveStartE;
                string cnt = s.HasCount ? "모터 카운트 X " + startCX + "→" + s.CX + " Y " + startCY + "→" + s.CY + " Z " + startCZ + "→" + s.CZ : "";
                evs.Add(new Ev(now, moveSrc, moveLogical
                    ? "이동 멈춤 (X " + F(s.X) + " Y " + F(s.Y) + " Z " + F(s.Z) + (Math.Abs(de) > 0.001 ? ", E " + (de > 0 ? "+" : "") + F(de) + " mm" : "") + ")"
                    : "모터 움직임 멈춤 — 좌표 그대로 X " + F(s.X) + " Y " + F(s.Y) + " Z " + F(s.Z) + " (" + cnt + ")"));
            }
            if ((what & Parser.Sd) != 0)
            {
                if (s.SdPrinting && !prev.SdPrinting) { evs.Add(new Ev(now, Src("sd", ts), "SD 출력 시작")); lastSdTenth = -1; }
                if (!s.SdPrinting && prev.SdPrinting) evs.Add(new Ev(now, Src("sd", ts), prev.SdPercent >= 99.5 ? "SD 출력 끝" : "SD 출력 멈춤 (" + prev.SdPercent.ToString("0", Parser.Inv) + "%)"));
                if (s.SdPrinting)
                {
                    int tenth = (int)(s.SdPercent / 10);
                    if (tenth != lastSdTenth && lastSdTenth >= 0) evs.Add(new Ev(now, "상태", "SD 출력 " + tenth * 10 + "%"));
                    lastSdTenth = tenth;
                }
            }
            if ((what & Parser.Speed) != 0)
            {
                if (prev.Feed >= 0 && s.Feed != prev.Feed) evs.Add(new Ev(now, Src("feed", ts), "속도 " + prev.Feed + " → " + s.Feed + " %"));
                if (prev.Flow >= 0 && s.Flow != prev.Flow) evs.Add(new Ev(now, Src("flow", ts), "유량 " + prev.Flow + " → " + s.Flow + " %"));
            }
            prev = s.Clone();
            return evs;
        }

        public bool Moving { get { return moving; } }
        public bool Busy { get { return busy; } }
    }

    // logs\날짜\시각_state.csv (1초마다 상태), _events.csv (사건), _serial.txt (통신 원문)
    class LogWriter : IDisposable
    {
        StreamWriter state, events, serial;
        public string Dir { get; private set; }
        public string Prefix { get; private set; }

        public LogWriter(string root, DateTime start)
        {
            Dir = Path.Combine(root, start.ToString("yyyy-MM-dd", Parser.Inv));
            Directory.CreateDirectory(Dir);
            Prefix = start.ToString("HHmmss", Parser.Inv);
            var bom = new UTF8Encoding(true);   // 엑셀에서 한글이 깨지지 않게
            state = new StreamWriter(Path.Combine(Dir, Prefix + "_state.csv"), true, bom) { AutoFlush = true };
            events = new StreamWriter(Path.Combine(Dir, Prefix + "_events.csv"), true, bom) { AutoFlush = true };
            serial = new StreamWriter(Path.Combine(Dir, Prefix + "_serial.txt"), true, new UTF8Encoding(false)) { AutoFlush = true };
            state.WriteLine("시각,노즐(°C),노즐 목표,베드(°C),베드 목표,노즐 출력(0-127),베드 출력(0-127),X,Y,Z,E,SD 출력 중,SD 진행(%),속도(%),유량(%),모터 X 카운트,모터 Y 카운트,모터 Z 카운트");
            events.WriteLine("시각,출처,내용");
        }

        static string Ts(DateTime t) { return t.ToString("yyyy-MM-dd HH:mm:ss.fff", Parser.Inv); }
        static string N(double v) { return double.IsNaN(v) ? "" : v.ToString("0.###", Parser.Inv); }
        static string Q(string s) { return s.Contains(",") || s.Contains("\"") ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }

        public static string StateRow(DateTime t, PrinterState s)
        {
            return string.Join(",", new[] {
                Ts(t), N(s.T), N(s.Tt), N(s.B), N(s.Bt), s.HasTemp ? s.TPow.ToString(Parser.Inv) : "", s.HasTemp ? s.BPow.ToString(Parser.Inv) : "",
                N(s.X), s.HasPos ? N(s.Y) : "", s.HasPos ? N(s.Z) : "", s.HasPos ? N(s.E) : "",
                s.SdPrinting ? "1" : "0", s.SdPrinting ? s.SdPercent.ToString("0.0", Parser.Inv) : "",
                s.Feed >= 0 ? s.Feed.ToString(Parser.Inv) : "", s.Flow >= 0 ? s.Flow.ToString(Parser.Inv) : "",
                s.HasCount ? s.CX.ToString(Parser.Inv) : "", s.HasCount ? s.CY.ToString(Parser.Inv) : "", s.HasCount ? s.CZ.ToString(Parser.Inv) : "" });
        }

        public static string EventRow(Ev e) { return Ts(e.Time) + "," + Q(e.Source) + "," + Q(e.Text); }

        public void State(DateTime t, PrinterState s) { state.WriteLine(StateRow(t, s)); }
        public void Event(Ev e) { events.WriteLine(EventRow(e)); }
        public void Serial(DateTime t, string dir, string line) { serial.WriteLine(t.ToString("HH:mm:ss.fff", Parser.Inv) + " " + dir + " " + line); }

        public void Dispose()
        {
            foreach (var w in new[] { state, events, serial }) if (w != null) w.Dispose();
            state = events = serial = null;
        }
    }
}
