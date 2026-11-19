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
            Check("속도 M220", Parser.Apply("FR:120%", s) == Parser.Speed && s.Feed == 120);
            Check("유량 M221", Parser.Apply("echo:E0 Flow: 95%", s) == Parser.Speed && s.Flow == 95);
            Check("상관없는 줄", Parser.Apply("Cap:EEPROM:1", s) == Parser.None && Parser.Apply("ok", s) == Parser.None);
            Check("M115 줄은 온도로 안 읽음", Parser.Apply("FIRMWARE_NAME:Marlin 2.0.8.1 (Jun  9 2022 10:11:27) SOURCE_CODE_URL:github.com/MarlinFirmware/Marlin PROTOCOL_VERSION:1.0 MACHINE_TYPE:Cremaker EXTRUDER_COUNT:1", s) == Parser.None);
            Check("자동 보고 구분", Parser.IsReport(" T:22 /0 B:22 /0") && Parser.IsReport("X:1 Y:2 Z:3 E:0 Count") && !Parser.IsReport("echo:Unknown command: \"M78\""));
            Check("명령 종류", Parser.KindOf("M104 S200") == "hotend" && Parser.KindOf("m140 s60") == "bed" && Parser.KindOf("G28 X") == "move"
                && Parser.KindOf("M220") == "" && Parser.KindOf("M220 S90") == "feed" && Parser.KindOf("M115") == "");
            Check("주석 지우기", Link.StripComment("G28 ; 홈") == "G28" && Link.StripComment("; 전부 주석") == "");

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

            // 로그 줄
            var ls = new PrinterState();
            Parser.Apply(" T:200.12 /200.00 B:60.39 /60.00 @:61 B@:127", ls);
            string row = LogWriter.StateRow(new DateTime(2026, 11, 19, 21, 0, 0), ls);
            Check("상태 줄 (위치 없을 때 빈칸)", row == "2026-11-19 21:00:00.000,200.12,200,60.39,60,61,127,,,,,0,,,");
            Check("사건 줄 쉼표 감싸기", LogWriter.EventRow(new Ev(new DateTime(2026, 11, 19, 21, 0, 0), "PC", "보냄: a, b")) == "2026-11-19 21:00:00.000,PC,\"보냄: a, b\"");
            string tmp = Path.Combine(Path.GetTempPath(), "cremaker_selftest_" + Guid.NewGuid().ToString("N"));
            using (var lw = new LogWriter(tmp, new DateTime(2026, 11, 19, 21, 5, 7)))
            {
                lw.State(DateTime.Now, ls);
                lw.Event(new Ev(DateTime.Now, "프린터", "노즐 목표 0 → 200 °C"));
                lw.Serial(DateTime.Now, "<", "ok");
            }
            string dir = Path.Combine(tmp, "2026-11-19");
            Check("로그 파일 3개", File.Exists(Path.Combine(dir, "210507_state.csv")) && File.Exists(Path.Combine(dir, "210507_events.csv")) && File.Exists(Path.Combine(dir, "210507_serial.txt")));
            Check("CSV 는 BOM + 머리줄", File.ReadAllBytes(Path.Combine(dir, "210507_events.csv")).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })
                && File.ReadAllLines(Path.Combine(dir, "210507_events.csv"), Encoding.UTF8)[1].Contains("노즐 목표"));
            Directory.Delete(tmp, true);

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
