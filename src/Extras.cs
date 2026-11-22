// v0.2.0 추가 기능 — 알림(Windows·텔레그램), 출력 기록장, 온도 이상 감시, PID 자동 조정 결과 읽기, 드라이버 점검 결과 읽기.
// 화면과 상관없는 부분만 여기 두고 --selftest 로 시험한다. 화면 연결은 MainForm.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace CremakerControl
{
    // exe 옆 설정.ini (키=값). 텔레그램 토큰이 들어가므로 저장소에 올리지 않는다(dist 는 .gitignore).
    static class Settings
    {
        public static string File { get { return Path.Combine(App.ExeDir, "설정.ini"); } }
        static Dictionary<string, string> d;

        static Dictionary<string, string> Map()
        {
            if (d != null) return d;
            d = new Dictionary<string, string>();
            try
            {
                if (System.IO.File.Exists(File))
                    foreach (var l in System.IO.File.ReadAllLines(File, Encoding.UTF8))
                    {
                        int i = l.IndexOf('=');
                        if (i > 0) d[l.Substring(0, i).Trim()] = l.Substring(i + 1).Trim();
                    }
            }
            catch (Exception) { }
            return d;
        }

        public static string Get(string k, string def) { string v; return Map().TryGetValue(k, out v) ? v : def; }
        public static double Num(string k, double def) { double v; return double.TryParse(Get(k, ""), System.Globalization.NumberStyles.Float, Parser.Inv, out v) ? v : def; }
        public static void Set(string k, string v) { Map()[k] = v; }
        public static void Save() { System.IO.File.WriteAllLines(File, Map().Select(kv => kv.Key + "=" + kv.Value), new UTF8Encoding(false)); }
    }

    // 텔레그램 봇 메시지. 실패는 예외 (부르는 쪽이 사건 기록에 남긴다)
    static class Telegram
    {
        public static bool Configured { get { return Settings.Get("telegram_token", "") != "" && Settings.Get("telegram_chat", "") != ""; } }

        public static void Send(string text)
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;   // TLS 1.2
            using (var wc = new WebClient { Encoding = Encoding.UTF8 })
            {
                var form = new System.Collections.Specialized.NameValueCollection { { "chat_id", Settings.Get("telegram_chat", "") }, { "text", text } };
                wc.UploadValues("https://api.telegram.org/bot" + Settings.Get("telegram_token", "") + "/sendMessage", form);
            }
        }
    }

    // 같은 종류 알림을 짧은 시간에 여러 번 보내지 않게 (화면 보고 끝 + 출력 끝 감지가 둘 다 오는 경우 등)
    class NotifyGate
    {
        readonly Dictionary<string, DateTime> last = new Dictionary<string, DateTime>();
        public bool Allow(string key, DateTime now, double sec)
        {
            DateTime t;
            if (last.TryGetValue(key, out t) && (now - t).TotalSeconds < sec) return false;
            last[key] = now;
            return true;
        }
    }

    // 온도 이상: 목표에 한 번 닿은 뒤 노즐 ±5 °C 가 10초, 베드 ±3 °C 가 30초 넘게 벗어나면 경고 (한 번만, 돌아오면 회복 기록)
    // 근거: 2026-11-21 첫 출력에서 2층 팬 켜질 때 노즐 205 → 197 처짐. Marlin 열 폭주 보호보다 훨씬 먼저 알리는 용도.
    class TempWatch
    {
        class H { public double Target = -1; public bool Reached, Warned; public DateTime OutSince = DateTime.MinValue; }
        readonly H noz = new H(), bed = new H();
        public double NozLimit = 5, NozHold = 10, BedLimit = 3, BedHold = 30;

        public List<Ev> Update(PrinterState s, DateTime now)
        {
            var evs = new List<Ev>();
            if (!s.HasTemp) return evs;
            One(noz, "노즐", s.T, s.Tt, NozLimit, NozHold, now, evs);
            One(bed, "베드", s.B, s.Bt, BedLimit, BedHold, now, evs);
            return evs;
        }

        static void One(H h, string name, double cur, double target, double limit, double hold, DateTime now, List<Ev> evs)
        {
            if (target != h.Target) { h.Target = target; h.Reached = h.Warned = false; h.OutSince = DateTime.MinValue; }
            if (target <= 0) return;
            double diff = cur - target;
            if (!h.Reached) { if (Math.Abs(diff) <= 2) h.Reached = true; return; }
            if (Math.Abs(diff) <= limit)
            {
                if (h.Warned) evs.Add(new Ev(now, "상태", name + " 온도 정상으로 돌아옴 (" + cur.ToString("0.0", Parser.Inv) + " °C)"));
                h.Warned = false; h.OutSince = DateTime.MinValue;
                return;
            }
            if (h.OutSince == DateTime.MinValue) h.OutSince = now;
            if (!h.Warned && (now - h.OutSince).TotalSeconds >= hold)
            {
                h.Warned = true;
                evs.Add(new Ev(now, "경고", name + " 온도 이상 — 목표 " + target.ToString("0", Parser.Inv) + " °C 인데 " + cur.ToString("0.0", Parser.Inv)
                    + " °C (" + (diff > 0 ? "+" : "") + diff.ToString("0.0", Parser.Inv) + ") 가 " + hold.ToString("0", Parser.Inv) + "초 넘게 이어짐"));
            }
        }
    }

    // 출력 기록장: exe 옆 출력기록.csv (엑셀용 BOM). 한 출력 = 한 줄.
    class JobRecord
    {
        public DateTime Start, End;
        public string File = "", Result = "", Memo = "";
        public double Expected = double.NaN, ExtrudedM;   // 예상 초, 압출 m

        public static readonly string[] Head = { "시작", "끝", "걸린 시간", "파일", "예상 시간", "실제/예상", "압출(m)", "필라멘트(g)", "비용(원)", "결과", "메모" };

        // PLA 1.75 mm 기본: 1 m = π·0.0875²·100 cm³ × 1.24 g/cm³ ≈ 2.98 g
        public static double Grams(double m, double diameter, double density) { return m * Math.PI * Math.Pow(diameter / 20, 2) * 100 * density; }

        public string[] Row(double diameter, double density, double wonPerKg)
        {
            double sec = (End - Start).TotalSeconds, g = Grams(ExtrudedM, diameter, density);
            return new[] {
                Start.ToString("yyyy-MM-dd HH:mm", Parser.Inv), End.ToString("yyyy-MM-dd HH:mm", Parser.Inv), PrintTracker.Hm(sec), File,
                double.IsNaN(Expected) ? "" : PrintTracker.Hm(Expected), double.IsNaN(Expected) || Expected <= 0 ? "" : (sec / Expected).ToString("0.00", Parser.Inv),
                ExtrudedM.ToString("0.00", Parser.Inv), g.ToString("0.0", Parser.Inv), (g / 1000 * wonPerKg).ToString("0", Parser.Inv), Result, Memo };
        }
    }

    static class JobLog
    {
        public static string Path { get { return System.IO.Path.Combine(App.ExeDir, "출력기록.csv"); } }

        public static string Line(string[] cells) { return string.Join(",", cells.Select(c => c.Contains(",") || c.Contains("\"") || c.Contains("\n") ? "\"" + c.Replace("\"", "\"\"") + "\"" : c)); }

        // 따옴표 안의 쉼표·따옴표 두 개를 처리하는 CSV 한 줄 나누기
        public static List<string> Split(string line)
        {
            var res = new List<string>();
            var cur = new StringBuilder();
            bool q = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (q) { if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; } else q = false; } else cur.Append(c); }
                else if (c == '"') q = true;
                else if (c == ',') { res.Add(cur.ToString()); cur.Clear(); }
                else cur.Append(c);
            }
            res.Add(cur.ToString());
            return res;
        }

        public static void Append(string path, string[] cells)
        {
            bool fresh = !File.Exists(path);
            using (var w = new StreamWriter(path, true, new UTF8Encoding(true)))
            {
                if (fresh) w.WriteLine(Line(JobRecord.Head));
                w.WriteLine(Line(cells));
            }
        }

        public static List<List<string>> Read(string path)
        {
            if (!File.Exists(path)) return new List<List<string>>();
            return File.ReadAllLines(path, Encoding.UTF8).Skip(1).Where(l => l.Trim() != "").Select(Split).ToList();
        }

        public static void Write(string path, List<List<string>> rows)
        {
            File.WriteAllLines(path, new[] { Line(JobRecord.Head) }.Concat(rows.Select(r => Line(r.ToArray()))), new UTF8Encoding(true));
        }
    }

    // M301 응답 "echo: p:22.20 i:1.08 d:114.00" 과 M303 끝 "#define DEFAULT_Kp 22.20" 읽기 (Marlin 2.0.8)
    static class Pid
    {
        static readonly Regex RxM301 = new Regex(@"p:\s*(-?[\d.]+)\s+i:\s*(-?[\d.]+)\s+d:\s*(-?[\d.]+)");
        public static double[] FromM301(string line)
        {
            var m = RxM301.Match(line);
            return m.Success ? new[] { double.Parse(m.Groups[1].Value, Parser.Inv), double.Parse(m.Groups[2].Value, Parser.Inv), double.Parse(m.Groups[3].Value, Parser.Inv) } : null;
        }
        public static bool Finished(string line) { return line.Contains("PID Autotune finished"); }
        public static bool Failed(string line) { return line.Contains("PID Autotune failed"); }
        public static string Show(double[] v) { return v == null ? "-" : string.Format(Parser.Inv, "P {0:0.00}  I {1:0.00}  D {2:0.00}", v[0], v[1], v[2]); }
    }

    // 드라이버 점검: 축을 움직이는 동안 M122 의 sg_result(부하 값, 클수록 가벼움) 를 모은다. 열 순서 X Y Z E.
    // 2026-11-19·20 실제 고장 모듈 두 개는 움직여도 항상 0 이었다. Z 는 제조사 충돌 보호가 Z 원점(아래로) 중 200 미만 1초면 멈춘다.
    // 2026-11-22 실기: sg_result 는 멈춘 뒤에도 마지막 값이 남고(0 으로 안 돌아감), 멈추는 순간엔 16~66 처럼 낮게 찍힌다.
    // 그래서 같은 M122 의 tstep 이 "max"(멈춤)인 축 값은 버리고, Z 판정은 아래로 갈 때의 중간값으로 한다.
    class DriverCheck
    {
        static readonly Regex RxSg = new Regex(@"^sg_result\s+(\d+)\s+(\d+)\s+(\d+)\s+(\d+)");
        static readonly Regex RxTstep = new Regex(@"^tstep\s+(\S+)\s+(\S+)\s+(\S+)\s+(\S+)");
        static readonly Regex RxConn = new Regex(@"Testing (\w+) connection\.\.\.\s*(.*)$");
        static readonly string[] Axes = { "X", "Y", "Z", "E" };
        public string Moving = "";   // 지금 움직이는 축 (보낸 G1 줄로 정함)
        bool down;                   // 지금 움직임이 - 방향인지 (Z 는 아래)
        string[] tstep;              // 바로 앞 tstep 줄 (M122 안에서 sg_result 보다 먼저 온다)
        public readonly Dictionary<string, List<int>> Sg = new Dictionary<string, List<int>>();
        public readonly List<int> ZDown = new List<int>();
        public readonly List<string> Conn = new List<string>();

        public void Sent(string cmd)
        {
            var m = Regex.Match(cmd, @"^G1 ([XYZ])(-?)");
            if (m.Success) { Moving = m.Groups[1].Value; down = m.Groups[2].Value == "-"; }
            else if (cmd == "G90") Moving = "";
        }

        public void Got(string line)
        {
            var c = RxConn.Match(line);
            if (c.Success) { string t = c.Groups[1].Value + " 통신 " + c.Groups[2].Value.Trim(); if (!Conn.Contains(t)) Conn.Add(t); return; }
            var ts = RxTstep.Match(line);
            if (ts.Success) { tstep = new[] { ts.Groups[1].Value, ts.Groups[2].Value, ts.Groups[3].Value, ts.Groups[4].Value }; return; }
            var m = RxSg.Match(line);
            if (!m.Success || Moving == "") return;
            int col = Array.IndexOf(Axes, Moving);
            bool moving = tstep == null || tstep[col] != "max";
            tstep = null;
            if (!moving) return;   // 멈춘 뒤 남아 있는 값
            int v = int.Parse(m.Groups[col + 1].Value, Parser.Inv);
            if (!Sg.ContainsKey(Moving)) Sg[Moving] = new List<int>();
            Sg[Moving].Add(v);
            if (Moving == "Z" && down) ZDown.Add(v);
        }

        static int Median(List<int> v) { var s = v.OrderBy(x => x).ToList(); return s[s.Count / 2]; }

        // 결과 문장들과, 문제가 있으면 bad = true
        public List<string> Report(out bool bad)
        {
            bad = false;
            var res = new List<string>();
            foreach (var c in Conn) { res.Add(c); if (!c.EndsWith("OK")) bad = true; }
            foreach (var a in new[] { "X", "Y", "Z" })
            {
                List<int> v;
                if (!Sg.TryGetValue(a, out v) || v.Count == 0) { res.Add(a + ": 움직이는 중 값을 못 받음 (움직이지 않았거나 응답이 늦음)"); bad = true; continue; }
                string range = a + " 움직이는 중 sg_result " + v.Min() + " ~ " + v.Max() + " (" + v.Count + "번 읽음)";
                if (v.Max() == 0) { res.Add(range + " — 고장 의심: 막힘 감지 신호가 없음 (예전 불량 모듈과 같은 증상)"); bad = true; continue; }
                if (a == "Z" && ZDown.Count > 0)
                {
                    int md = Median(ZDown);
                    range += ", 내려갈 때 중간값 " + md;
                    if (md < 200) { res.Add(range + " — 주의: Z 원점 때 200 아래가 1초 넘으면 '원점 이동 실패'로 멈춤"); bad = true; continue; }
                }
                res.Add(range + " — 정상");
            }
            return res;
        }
    }
}
