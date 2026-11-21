// --selftest : 해석·조작 감지·로그 줄·업데이트 비교를 검사한다. 줄들은 2026-11-19 실제 프린터에서 받은 것.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CremakerControl
{
    static class SelfTest
    {
        static int pass, fail;
        static TextWriter o;

        static void Check(string name, bool ok)
        {
            if (ok) pass++; else fail++;
            o.WriteLine((ok ? "ok   " : "FAIL ") + name);
        }

        public static int Run()
        {
            o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            var s = new PrinterState();

            // 해석
            Check("온도 자동 보고", Parser.Apply(" T:102.62 /200.00 B:22.54 /0.00 P:22.60 /0.00 @:127 B@:0", s) == Parser.Temp
                && s.T == 102.62 && s.Tt == 200 && s.B == 22.54 && s.Bt == 0 && s.TPow == 127 && s.BPow == 0);
            Check("ok 뒤 온도 (M105)", Parser.Apply("ok T:200.12 /200.00 B:60.39 /60.00 P:26.72 /0.00 @:61 B@:127", s) == Parser.Temp
                && s.Bt == 60 && s.TPow == 61 && s.BPow == 127);
            Check("위치 자동 보고", Parser.Apply("X:-8.0000 Y:-6.0000 Z:0.0000 E:0.0000 Count X:-640 Y:-480 Z:0", s) == Parser.Pos
                && s.X == -8 && s.Y == -6 && s.Z == 0 && s.E == 0);
            Check("SD 진행", Parser.Apply("SD printing byte 500/1000", s) == Parser.Sd && s.SdPrinting && Math.Abs(s.SdPercent - 50) < 1e-9);
            Check("SD 안 함", Parser.Apply("Not SD printing", s) == Parser.Sd && !s.SdPrinting);
            // 화면 펌웨어 v1.5 의 M118 P2 진행 보고 (Printing.c loopReportProgress 형식)
            Check("화면 진행 보고", Parser.Apply("TFT_PROGRESS P25 T600 C250000 S1000000 H0", s) == Parser.Tft
                && s.TftOn && !s.TftPaused && s.TftPct == 25 && s.TftSec == 600 && Math.Abs(s.TftPercent - 25) < 1e-9);
            Check("화면 진행 남은 시간", Math.Abs(PrintTracker.TftRemain(s) - 1800) < 1e-9);
            Check("화면 진행 일시정지", Parser.Apply("TFT_PROGRESS P25 T630 C250000 S1000000 H1", s) == Parser.Tft && s.TftPaused);
            Check("화면 진행 숨김 대상", Parser.IsReport("TFT_PROGRESS P25 T630 C250000 S1000000 H1") && !Parser.IsReport("TFT_PROGRESS END"));
            Check("화면 진행 끝", Parser.Apply("TFT_PROGRESS END", s) == Parser.Tft && !s.TftOn && !s.TftPaused);
            var s0 = new PrinterState { TftOn = true, TftPos = 5, TftSize = 1000, TftSec = 10 };
            Check("화면 진행 1% 전엔 남은 시간 모름", double.IsNaN(PrintTracker.TftRemain(s0)));
            Check("속도 M220", Parser.Apply("FR:120%", s) == Parser.Speed && s.Feed == 120);
            Check("유량 M221", Parser.Apply("echo:E0 Flow: 95%", s) == Parser.Speed && s.Flow == 95);
            Check("상관없는 줄", Parser.Apply("Cap:EEPROM:1", s) == Parser.None && Parser.Apply("ok", s) == Parser.None);
            Check("M115 줄은 온도로 안 읽음", Parser.Apply("FIRMWARE_NAME:Marlin 2.0.8.1 (Jun  9 2022 10:11:27) SOURCE_CODE_URL:github.com/MarlinFirmware/Marlin PROTOCOL_VERSION:1.0 MACHINE_TYPE:Cremaker EXTRUDER_COUNT:1", s) == Parser.None);
            Check("자동 보고 구분", Parser.IsReport(" T:22 /0 B:22 /0") && Parser.IsReport("X:1 Y:2 Z:3 E:0 Count") && !Parser.IsReport("echo:Unknown command: \"M78\""));
            Check("명령 종류", Parser.KindOf("M104 S200") == "hotend" && Parser.KindOf("m140 s60") == "bed" && Parser.KindOf("G28 X") == "move"
                && Parser.KindOf("M220") == "" && Parser.KindOf("M220 S90") == "feed" && Parser.KindOf("M115") == "");
            Check("오류 줄 (한글 kill 메시지)", Parser.IsError("echo:원점 이동 실패, 재부팅 필요") && Parser.IsError("Error:Printer halted. kill() called!") && !Parser.IsError("echo:busy: processing") && !Parser.IsError("echo:E0 Flow: 100%"));
            Check("주석 지우기", Link.StripComment("G28 ; 홈") == "G28" && Link.StripComment("; 전부 주석") == "");

            // 설정 백업 비교 (줄은 2026-11-19 손으로 만든 백업 파일 모양 그대로)
            var bak = new[] { "===== M115", "FIRMWARE_NAME:Marlin 2.0.8.1", "Cap:EEPROM:1", "===== M503", "echo:  G21    ; Units in mm (mm)",
                "echo:; Steps per unit:", "echo: M92 X80.0000 Y80.0000 Z800.0000 E95.0000", "", "echo:  M914 X2 Y2", "ok", "===== M420 V", "echo:  M420 S1" };
            var set1 = SettingsBackup.Settings(bak);
            Check("백업 설정 줄만 읽기", set1.SequenceEqual(new[] { "G21", "M92 X80.0000 Y80.0000 Z800.0000 E95.0000", "M914 X2 Y2" }));
            var set2 = SettingsBackup.Settings(new[] { "===== M503", "echo:  G21 ; Units", "echo: M92 X80.0000 Y80.0000 Z800.0000 E95.0000", "echo:  M914 X2 Y70" });
            Check("백업 비교", SettingsBackup.Diff(set1, set2).SequenceEqual(new[] { "- M914 X2 Y2", "+ M914 X2 Y70" }) && SettingsBackup.Diff(set1, set1).Count == 0);

            // Cura G코드 진행률 (Cura 표준 주석 모양: ;TIME: ;LAYER: ;TIME_ELAPSED:, Z 홉 이동 포함)
            var gc = GcodeInfo.Parse(new[] {
                ";FLAVOR:Marlin", ";TIME:1000", ";LAYER_COUNT:3", "M82", "G92 E0", "G1 Z15 F3000",
                ";LAYER:0", "G0 X10 Y10 Z0.3", "G1 X20 Y10 E1.5", "G1 E0.5 ; 되감기", "G0 Z1.3", "G0 X30 Y30", "G0 Z0.3", "G1 X40 Y30 E2", ";TIME_ELAPSED:200",
                ";LAYER:1", "G0 X10 Y10 Z0.5", "G1 X20 Y10 E3", "G92 E0", "G1 X30 Y10 E1", ";TIME_ELAPSED:600",
                ";LAYER:2", "G0 X10 Y10 Z0.7", "M83", "G1 X20 Y10 E0.4", ";TIME_ELAPSED:1000" }, "t.gcode");
            Check("G코드 층·Z·시간", gc.Layers == 3 && gc.LayerZ.SequenceEqual(new[] { 0.3, 0.5, 0.7 }) && gc.LayerStart.SequenceEqual(new[] { 0.0, 200, 600 }) && gc.TotalTime == 1000);
            Check("Z 로 층 찾기", gc.LayerAt(0.3) == 0 && gc.LayerAt(0.5) == 1 && gc.LayerAt(0.2) == -1 && gc.LayerAt(9) == 2);
            // 3D 보기용 압출 선: 거의 일직선이면 합침, 종류(;TYPE:), 바이트 위치
            var g3 = GcodeInfo.Parse(new[] { ";LAYER:0", "M83", ";TYPE:WALL-OUTER", "G0 X0 Y0 Z0.2", "G1 X10 Y0 E1", "G1 X20 Y0.001 E1", "G1 X20 Y10 E1",
                ";TYPE:FILL", "G1 X10 Y10 E1", "G1 E-1", "G0 X0 Y0", ";LAYER:1", ";TYPE:WALL-OUTER", "G0 X0 Y0 Z0.4", "G1 X10 Y0 E1" }, "s.gcode");
            Check("압출 선 합치기·종류", g3.Segs.Count == 4 && g3.Segs[0].X1 == 20 && g3.Segs[0].Kind == 0 && g3.Segs[2].Kind == 3 && g3.Segs[3].Layer == 1
                && g3.MaxX == 20 && g3.MaxY == 10 && Math.Abs(g3.MaxZ - 0.4) < 1e-6);
            var g5 = GcodeInfo.Parse(new[] { ";LAYER:0", "M83", "G0 X0 Y0 Z0.2", ";TYPE:SUPPORT", "G1 X10 Y0 E1", ";TYPE:SKIRT", "G1 X10 Y10 E1" }, "s.gcode");
            Check("서포트 종류", g5.Segs.Count == 2 && g5.Segs[0].Kind == 5 && g5.Segs[1].Kind == 4);
            Check("바이트 위치로 선 수", g3.CountAtOffset(g3.Segs[0].End) == 1 && g3.CountAtOffset(g3.Segs[0].End - 1) == 0 && g3.CountAtOffset(long.MaxValue) == 4);
            Check("노즐 위치로 선 찾기", g3.Locate(0, 4, 20, 5, 0.2, 4) == 2 && g3.Locate(0, 4, 50, 50, 0.2, 4) == -1 && g3.Locate(0, 4, 5, 0, 0.4, 4) == 4
                && g3.FirstOfLayer(1) == 3);
            string gtmp = Path.Combine(Path.GetTempPath(), "cremaker_selftest_" + Guid.NewGuid().ToString("N") + ".gcode");
            File.WriteAllText(gtmp, ";LAYER:0\r\nG0 X0 Y0 Z0.2\r\nG1 X10 Y0 E1\r\n", new UTF8Encoding(false));
            var gl = GcodeInfo.Load(gtmp);
            Check("파일 바이트 위치 (CRLF)", gl.Size == new FileInfo(gtmp).Length && gl.Segs.Count == 1 && gl.Segs[0].End == gl.Size);
            File.Delete(gtmp);
            var tr = new PrintTracker();
            var ps = new PrinterState { T = 200, Tt = 200, X = 10, Y = 10, Z = 0.5, E = 0 };
            var t0 = new DateTime(2026, 11, 20, 10, 0, 0);
            Check("출력 시작 감지", tr.Update(ps, true, t0).Count == 1 && tr.On);
            ps = ps.Clone(); ps.X = 20; ps.E = 2;
            tr.Update(ps, true, t0.AddSeconds(400));
            var pr = tr.Progress(gc, t0.AddSeconds(400));
            Check("진행률 (2층 시작 = 200/1000)", pr != null && pr[0] == 20 && pr[1] == 2 && tr.PrintZ == 0.5 && tr.Extruded == 2);
            ps = ps.Clone(); ps.Z = 1.5; ps.X = 25;   // Z 홉 이동 (압출 없음) 은 출력 높이로 안 봄
            tr.Update(ps, true, t0.AddSeconds(401));
            Check("Z 홉 무시", tr.PrintZ == 0.5);
            tr.Update(ps, false, t0.AddSeconds(430));
            Check("잠깐 멈춤은 끝 아님", tr.On);
            Check("60초 멈추면 출력 끝", tr.Update(ps, false, t0.AddSeconds(470)).Count == 1 && !tr.On);

            // 메인보드 펌웨어 판정 (M115 줄: v1.1·v1.2 는 bin 안 문자열, 06-09 는 이 PC 프린터에서 받은 것)
            bool latest;
            string detail;
            Check("v1.2 판정", Parser.BoardVersion("FIRMWARE_NAME:Marlin 2.0.8.1 (Jul 12 2022 10:42:43) SOURCE_CODE_URL:github.com/MarlinFirmware/Marlin PROTOCOL_VERSION:1.0 MACHINE_TYPE:Cremaker_V1.2 EXTRUDER_COUNT:1", out latest, out detail) == "v1.2"
                && latest && detail == "Marlin 2.0.8.1, 2022-07-12 빌드, 기종 Cremaker_V1.2");
            Check("v1.1 판정", Parser.BoardVersion("FIRMWARE_NAME:Marlin 2.0.8.1 (Mar  7 2022 10:38:07) SOURCE_CODE_URL:github.com/MarlinFirmware/Marlin PROTOCOL_VERSION:1.0 MACHINE_TYPE:Cremaker EXTRUDER_COUNT:1", out latest, out detail) == "v1.1" && !latest);
            Check("06-09 빌드 = 확인 안 됨", Parser.BoardVersion("FIRMWARE_NAME:Marlin 2.0.8.1 (Jun  9 2022 10:11:27) SOURCE_CODE_URL:github.com/MarlinFirmware/Marlin PROTOCOL_VERSION:1.0 MACHINE_TYPE:Cremaker EXTRUDER_COUNT:1", out latest, out detail) == "확인 안 된 빌드"
                && !latest && detail.Contains("2022-06-09"));
            Check("M115 아님", Parser.BoardVersion("ok", out latest, out detail) == "");

            // 조작 감지 — 화면에서 예열(PC 명령 없음) → 출처 '프린터'
            var d = new Detector();
            var p = new PrinterState();
            var now = DateTime.Now;
            Func<string, double, List<Ev>> Feed = (line, t) => d.Update(p, Parser.Apply(line, p), t, now);
            Feed(" T:22.6 /0.00 B:22.0 /0.00 @:0 B@:0", 0);
            Feed("X:-8.0000 Y:-6.0000 Z:0.0000 E:0.0000 Count X:-640 Y:-480 Z:0", 0.1);
            var e1 = Feed(" T:23.0 /200.00 B:22.0 /0.00 @:127 B@:0", 2);
            Check("화면 예열 감지 → 프린터", e1.Count == 1 && e1[0].Source == "프린터" && e1[0].Text.Contains("0 → 200"));
            var e2 = Feed(" T:199.0 /200.00 B:22.0 /0.00 @:60 B@:0", 60);
            Check("목표 온도 도달", e2.Count == 1 && e2[0].Source == "상태" && e2[0].Text.Contains("도달"));
            Check("도달은 한 번만", Feed(" T:200.5 /200.00 B:22.0 /0.00 @:60 B@:0", 62).Count == 0);
            // PC가 베드 설정 → 출처 PC
            d.PcSent("bed", 70);
            var e3 = Feed(" T:200.0 /200.00 B:22.0 /60.00 @:60 B@:127", 71);
            Check("PC 베드 설정 → PC", e3.Count == 1 && e3[0].Source == "PC");
            // 창(5초)이 지난 뒤 변화는 프린터
            var e4 = Feed(" T:200.0 /200.00 B:22.0 /0.00 @:60 B@:0", 80);
            Check("5초 지난 뒤 변화 → 프린터", e4.Count == 1 && e4[0].Source == "프린터" && e4[0].Text == "베드 히터 끔");
            // 화면에서 축 이동 → 시작·멈춤
            var e5 = Feed("X:10.0000 Y:-6.0000 Z:0.0000 E:0.0000 Count X:0 Y:0 Z:0", 90);
            Check("이동 시작 → 프린터", e5.Count == 1 && e5[0].Text.StartsWith("이동 시작") && e5[0].Source == "프린터");
            Check("계속 움직이면 사건 없음", Feed("X:20.0000 Y:-6.0000 Z:0.0000 E:0.0000 Count X:0 Y:0 Z:0", 91).Count == 0);
            Check("1초 멈춤은 아직 이동 중", d.Update(p, Parser.None, 92, now).Count == 0 && d.Moving);
            var e6 = d.Update(p, Parser.None, 93.6, now);
            Check("2.5초 멈추면 이동 멈춤", e6.Count == 1 && e6[0].Text.StartsWith("이동 멈춤 (X 20") && !d.Moving);
            // 압출이 계속되면 출력 중으로 본다 (화면 TFT 가 자기 SD 로 출력할 때 메인보드 SD 는 '출력 안 함')
            Feed("X:21.0000 Y:-6.0000 Z:0.2000 E:1.5000 Count X:0 Y:0 Z:0", 100);
            Check("압출 + 가열 → 출력 중", d.LooksPrinting(p, 101));
            Check("10초 압출 없으면 출력 아님", !d.LooksPrinting(p, 111));
            var e7 = d.Update(p, Parser.None, 104, now);
            Check("멈춤에 E 변화 표시", e7.Count == 1 && e7[0].Text.Contains("E +1.5 mm"));
            // SD 출력
            Feed("SD printing byte 10/1000", 120);
            var e8 = Feed("SD printing byte 120/1000", 122);
            Check("SD 10% 사건", e8.Count == 1 && e8[0].Text == "SD 출력 10%");
            var e9 = Feed("Not SD printing", 124);
            Check("SD 멈춤 사건", e9.Count == 1 && e9[0].Text.StartsWith("SD 출력 멈춤 (12%"));
            Feed("FR:100%", 130);
            var e10 = Feed("FR:150%", 135);
            Check("화면 속도 변경 → 프린터", e10.Count == 1 && e10[0].Source == "프린터" && e10[0].Text == "속도 100 → 150 %");

            // 홈 동작 — 좌표 그대로, 모터 카운트·busy 만 바뀜 (2026-11-19 22:27 실제 로그)
            var dh = new Detector();
            var ph = new PrinterState();
            Func<string, double, List<Ev>> H = (line, t) => dh.Update(ph, Parser.Apply(line, ph), t, now);
            H("X:-8.0000 Y:-6.0000 Z:0.0000 E:0.0000 Count X:-640 Y:-480 Z:0", 0);
            Check("카운트 읽기", ph.HasCount && ph.CX == -640 && ph.CY == -480 && ph.CZ == 0);
            var h1 = H("X:-8.0000 Y:-6.0000 Z:0.0000 E:0.0000 Count X:-27360 Y:-480 Z:0", 1);
            Check("홈: 모터 움직임 시작 → 프린터", h1.Count == 1 && h1[0].Source == "프린터" && h1[0].Text.StartsWith("모터 움직임 시작"));
            Check("busy 줄 종류", Parser.Apply("echo:busy: processing", new PrinterState()) == Parser.Busy);
            var h2 = H("echo:busy: processing", 2.9);
            Check("처리 중 시작", h2.Count == 1 && h2[0].Text.StartsWith("프린터 처리 중") && dh.Busy);
            H("X:-8.0000 Y:-6.0000 Z:0.0000 E:0.0000 Count X:130 Y:-27120 Z:0", 3);
            H("echo:busy: processing", 4.9);
            H("X:-8.0000 Y:-6.0000 Z:0.0000 E:0.0000 Count X:130 Y:18400 Z:0", 8);
            for (double bt = 6.9; bt <= 22.95; bt += 2) H("echo:busy: processing", bt);
            var h3 = dh.Update(ph, Parser.None, 26.6, now);
            Check("처리 끝 + 걸린 시간", h3.Any(e => e.Text == "처리 끝 (약 22초 걸림)") && !dh.Busy);
            Check("모터 움직임 멈춤 + 카운트", h3.Any(e => e.Text.StartsWith("모터 움직임 멈춤") && e.Text.Contains("X -640→130 Y -480→18400")) && !dh.Moving);
            // 출력 중(압출 계속) busy 는 버퍼 가득 대기 → 사건 없음 (2026-11-21 첫 출력 로그에서 약 60번)
            var dp = new Detector();
            var pp = new PrinterState();
            Func<string, double, List<Ev>> P = (line, t) => dp.Update(pp, Parser.Apply(line, pp), t, now);
            P(" T:205.0 /205.00 B:60.0 /60.00 @:90 B@:40", 0);
            P("X:10.0000 Y:10.0000 Z:0.2000 E:1.0000 Count X:0 Y:0 Z:0", 0);
            P("X:11.0000 Y:10.0000 Z:0.2000 E:2.0000 Count X:0 Y:0 Z:0", 1);
            var pb = P("echo:busy: processing", 2);
            var pe = dp.Update(pp, Parser.None, 6, now);
            Check("출력 중 busy 는 사건 없음", !pb.Any(e => e.Text.Contains("처리")) && !pe.Any(e => e.Text.Contains("처리")) && !dp.Busy);

            var ch = new PrinterState();

            Parser.Apply("X:1.0000 Y:2.0000 Z:3.0000 E:0.0000 Count X:80 Y:160 Z:2400", ch);
            Check("상태 줄 카운트 열", LogWriter.StateRow(new DateTime(2026, 11, 19), ch).EndsWith(",80,160,2400"));

            // 로그 줄
            var ls = new PrinterState();
            Parser.Apply(" T:200.12 /200.00 B:60.39 /60.00 @:61 B@:127", ls);
            string row = LogWriter.StateRow(new DateTime(2026, 11, 19, 21, 0, 0), ls);
            Check("상태 줄 (위치 없을 때 빈칸)", row == "2026-11-19 21:00:00.000,200.12,200,60.39,60,61,127,,,,,0,,,,,,");
            Check("사건 줄 쉼표 감싸기", LogWriter.EventRow(new Ev(new DateTime(2026, 11, 19, 21, 0, 0), "PC", "보냄: a, b")) == "2026-11-19 21:00:00.000,PC,\"보냄: a, b\"");
            string tmp = Path.Combine(Path.GetTempPath(), "cremaker_selftest_" + Guid.NewGuid().ToString("N"));
            using (var lw = new LogWriter(tmp, new DateTime(2026, 11, 19, 21, 5, 7)))
            {
                lw.State(DateTime.Now, ls);
                lw.Event(new Ev(DateTime.Now, "프린터", "노즐 목표 0 → 200 °C"));
                lw.Serial(DateTime.Now, "<", "ok");
            }
            using (var lw = new LogWriter(tmp, new DateTime(2026, 11, 19, 21, 5, 8)))
            {
                var tl = DateTime.Now;
                foreach (var x in new[] { ">|M220", "<|FR:100%", "<|ok", ">|M220", "<|FR:100%", "<|ok", ">|M220", "<|FR:120%", "<|ok",
                    ">|G28", "<|ok", "<|Not SD printing", "<|Not SD printing", "<|X:1.0000 Y:2.0000 Z:0.0000 E:0.0000 Count X:80 Y:160 Z:0",
                    "<|X:1.0000 Y:2.0000 Z:0.0000 E:0.0000 Count X:80 Y:160 Z:0", "<|X:2.0000 Y:2.0000 Z:0.0000 E:0.0000 Count X:160 Y:160 Z:0",
                    "<|echo:busy: processing", "<|echo:busy: processing" })
                    lw.Serial(tl, x.Substring(0, 1), x.Substring(2));
                lw.Serial(tl.AddSeconds(10), "<", "X:3.0000 Y:2.0000 Z:0.0000 E:0.0000 Count X:240 Y:160 Z:0");
            }
            var sl = File.ReadAllLines(Path.Combine(tmp, "2026-11-19", "210508_serial.txt")).Select(x => x.Substring(13)).ToArray();
            Check("serial 로그 줄이기 (폴링·반복 보고 빼기, 위치 10초에 한 번)", string.Join("|", sl) == "< FR:100%|< FR:120%|> G28|< ok|< Not SD printing|< X:1.0000 Y:2.0000 Z:0.0000 E:0.0000 Count X:80 Y:160 Z:0|< echo:busy: processing|< echo:busy: processing|< X:3.0000 Y:2.0000 Z:0.0000 E:0.0000 Count X:240 Y:160 Z:0");
            string dir = Path.Combine(tmp, "2026-11-19");
            Check("로그 파일 3개", File.Exists(Path.Combine(dir, "210507_state.csv")) && File.Exists(Path.Combine(dir, "210507_events.csv")) && File.Exists(Path.Combine(dir, "210507_serial.txt")));
            Check("CSV 는 BOM + 머리줄", File.ReadAllBytes(Path.Combine(dir, "210507_events.csv")).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })
                && File.ReadAllLines(Path.Combine(dir, "210507_events.csv"), Encoding.UTF8)[1].Contains("노즐 목표"));
            Directory.Delete(tmp, true);

            // v0.2.0 온도 이상 감시: 목표 도달 전엔 조용, 도달 뒤 노즐 -8 이 10초 이어지면 경고 한 번, 돌아오면 회복
            var tw = new TempWatch();
            var tw0 = new DateTime(2026, 11, 21, 1, 0, 0);
            var ts1 = new PrinterState { T = 150, Tt = 205, B = 60, Bt = 60 };
            Check("온도 감시: 가열 중엔 경고 없음", tw.Update(ts1, tw0).Count == 0);
            ts1.T = 204.5; tw.Update(ts1, tw0.AddSeconds(60));
            ts1.T = 197; var w1 = tw.Update(ts1, tw0.AddSeconds(61)); var w2 = tw.Update(ts1, tw0.AddSeconds(70)); var w3 = tw.Update(ts1, tw0.AddSeconds(72));
            Check("온도 감시: 10초 넘게 처지면 경고 한 번", w1.Count == 0 && w2.Count == 0 && w3.Count == 1 && w3[0].Source == "경고" && tw.Update(ts1, tw0.AddSeconds(80)).Count == 0);
            ts1.T = 204; var w4 = tw.Update(ts1, tw0.AddSeconds(90));
            Check("온도 감시: 돌아오면 회복 기록", w4.Count == 1 && w4[0].Source == "상태");
            ts1.Tt = 215; ts1.T = 204;
            Check("온도 감시: 목표 바꾸면 다시 도달부터", tw.Update(ts1, tw0.AddSeconds(100)).Count == 0 && tw.Update(ts1, tw0.AddSeconds(200)).Count == 0);

            // 출력 기록장
            Check("필라멘트 무게 (1.75 mm PLA 1 m ≈ 2.98 g)", Math.Abs(JobRecord.Grams(1, 1.75, 1.24) - 2.982) < 0.01);
            var jr = new JobRecord { Start = tw0, End = tw0.AddMinutes(190), File = "a,b.gcode", Expected = 189 * 60, ExtrudedM = 2.5, Result = "완료", Memo = "말 \"따옴표\"" };
            var cells = jr.Row(1.75, 1.24, 20000);
            Check("출력 기록 줄 값", cells[2] == "3:10" && cells[4] == "3:09" && cells[5] == "1.01" && cells[6] == "2.50" && cells[7] == "7.5" && cells[8] == "149");
            Check("CSV 쉼표·따옴표 왕복", JobLog.Split(JobLog.Line(cells)).SequenceEqual(cells));
            string jp = Path.Combine(Path.GetTempPath(), "cc_joblog_test.csv");
            if (File.Exists(jp)) File.Delete(jp);
            JobLog.Append(jp, cells); JobLog.Append(jp, cells);
            var jrows = JobLog.Read(jp); jrows[1][10] = "고침"; JobLog.Write(jp, jrows);
            var jrows2 = JobLog.Read(jp);
            Check("출력 기록 파일 쓰기·고치기", jrows2.Count == 2 && jrows2[1][10] == "고침" && jrows2[0][3] == "a,b.gcode" && File.ReadAllBytes(jp)[0] == 0xEF);
            File.Delete(jp);
            var ng = new NotifyGate();
            Check("알림 중복 막기", ng.Allow("end", tw0, 600) && !ng.Allow("end", tw0.AddSeconds(100), 600) && ng.Allow("pause", tw0, 60) && ng.Allow("end", tw0.AddSeconds(601), 600));

            // PID (Marlin 2.0.8 M301 응답·M303 끝 문구)
            var pv = Pid.FromM301("echo: p:22.20 i:1.08 d:114.00");
            Check("PID M301 읽기", pv != null && pv[0] == 22.2 && pv[1] == 1.08 && pv[2] == 114);
            Check("PID 다른 줄은 안 읽음", Pid.FromM301(" Kp: 20.56 Ki: 1.10 Kd: 96.08") == null && Pid.FromM301("echo:  M301 P22.20 I1.08 D114.00") == null && Pid.FromM301(" bias: 92 d: 92 min: 196.56 max: 203.75") == null);
            Check("PID 끝·실패 문구", Pid.Finished("PID Autotune finished! Put the last Kp, Ki and Kd constants from below into Configuration.h") && Pid.Failed("PID Autotune failed! timeout"));

            // 드라이버 점검: 움직이는 축 열만, 전부 0 이면 고장 의심, Z 200 미만 주의
            var dc = new DriverCheck();
            foreach (var l in new[] { ">M122", "<sg_result		0	0	0	0", "<Testing X connection... OK", "<Testing Y connection... OK", "<Testing Z connection... OK", "<Testing E connection... OK",
                ">G91", ">G1 X60 F3000", "<sg_result		62	0	0	0", "<sg_result		216	0	0	0", ">G90",
                ">G1 Y60 F3000", "<sg_result		0	0	0	0", "<sg_result		0	0	0	0", ">G90",
                ">G1 Z10 F240", "<sg_result		0	0	180	0", "<sg_result		0	0	0	0", ">G90", "<Testing X connection... OK" })
                if (l[0] == '>') dc.Sent(l.Substring(1)); else dc.Got(l.Substring(1));
            bool dbad;
            var drep = dc.Report(out dbad);
            Check("드라이버 점검 판정", dbad && drep.Count == 7 && drep[4].Contains("62 ~ 216") && drep[4].EndsWith("정상") && drep[5].Contains("고장 의심") && drep[6].Contains("180 ~ 180") && drep[6].Contains("주의"));

            // 업데이트
            Check("버전 비교", Updater.IsNewer("0.1.1", "0.1.0") && !Updater.IsNewer("0.1.0", "0.1.0") && Updater.IsNewer("v0.10.0", "0.9.9"));
            Check("SHA256 검사", Updater.Verified(Encoding.ASCII.GetBytes("abc"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  x.exe")
                && !Updater.Verified(Encoding.ASCII.GetBytes("abd"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
            Check("안전 한계 (펌웨어 값)", Machine.HotendMax == 260 && Machine.BedMax == 90 && Machine.ExtrudeMin == 170);

            o.WriteLine(fail == 0 ? "SELFTEST OK (" + pass + ")" : "SELFTEST FAIL " + fail + " / " + (pass + fail));
            return fail == 0 ? 0 : 1;
        }
    }
}
