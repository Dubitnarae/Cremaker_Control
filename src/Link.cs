// USB 직렬 연결 — 읽기 스레드 하나, 보내기 스레드 하나.
// 명령은 한 번에 하나씩 보내고 "ok" 를 기다린다 (Marlin BUFSIZE 4, 화면 쪽 명령과 섞이지 않게 여유를 둔다).
// M112(비상 정지)는 줄을 서지 않고 바로 쓴다 — 펌웨어 EMERGENCY_PARSER 가 즉시 처리한다.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Threading;

namespace CremakerControl
{
    class Link
    {
        public string Port { get; private set; }
        public volatile bool Connected;
        public Action<string, string> OnLine;   // (방향 "<" 받음 / ">" 보냄, 줄)
        public Action<string> OnInfo;           // 연결·끊김 안내

        SerialPort sp;
        Thread reader, writer;
        volatile bool running;
        readonly BlockingCollection<string> queue = new BlockingCollection<string>();
        readonly AutoResetEvent okSignal = new AutoResetEvent(false);
        readonly object writeLock = new object();
        long lastRx;   // 마지막으로 무언가 받은 시각 (Environment.TickCount64 가 없어 DateTime.UtcNow.Ticks)

        public int Pending { get { return queue.Count; } }

        // 연결 직후 보낼 명령: 펌웨어 정보, 자동 보고 켜기 (온도 1초, 위치 1초, SD 2초)
        static readonly string[] Hello = { "M115", "M155 S1", "M154 S1", "M27 S2", "M220", "M221" };

        public Link(string port) { Port = port; }

        public void Start()
        {
            running = true;
            reader = new Thread(ReadLoop) { IsBackground = true, Name = "serial-read" };
            writer = new Thread(WriteLoop) { IsBackground = true, Name = "serial-write" };
            reader.Start();
            writer.Start();
        }

        public void Stop()
        {
            if (Connected) { WriteRaw("M154 S0"); WriteRaw("M27 S0"); Thread.Sleep(150); }   // 켜 둔 위치·SD 자동 보고 끄기 (온도 M155 는 화면도 쓰므로 둔다)
            running = false;
            okSignal.Set();
            queue.Add("");   // 보내기 스레드 깨우기
            CloseSerial();
            if (reader != null) reader.Join(1500);
            if (writer != null) writer.Join(1500);
        }

        public void Send(string cmd)
        {
            foreach (var part in cmd.Split('\n'))
            {
                string c = StripComment(part);
                if (c != "") queue.Add(c);
            }
        }

        public static string StripComment(string s)
        {
            int i = s.IndexOf(';');
            return (i >= 0 ? s.Substring(0, i) : s).Trim();
        }

        public void Emergency()
        {
            WriteRaw("M112");
        }

        void WriteRaw(string c)
        {
            lock (writeLock)
            {
                var s = sp;
                if (s == null || !s.IsOpen) return;
                try { s.Write(c + "\n"); } catch (Exception) { return; }
            }
            if (OnLine != null) OnLine(">", c);
        }

        void Open()
        {
            var s = new SerialPort(Port, 115200) { DtrEnable = true, RtsEnable = true, ReadTimeout = 500, WriteTimeout = 2000, NewLine = "\n", Encoding = System.Text.Encoding.UTF8 };   // 펌웨어 한글 메시지(원점 이동 실패 등)
            s.Open();
            lock (writeLock) sp = s;
            Thread.Sleep(300);
            try { s.DiscardInBuffer(); } catch (Exception) { }
            Connected = true;
            lastRx = DateTime.UtcNow.Ticks;
            if (OnInfo != null) OnInfo(Port + " 연결됨");
            foreach (var h in Hello) queue.Add(h);
        }

        void CloseSerial()
        {
            SerialPort s;
            lock (writeLock) { s = sp; sp = null; }
            bool was = Connected;
            Connected = false;
            if (s != null) { try { s.Close(); } catch (Exception) { } s.Dispose(); }
            if (was && OnInfo != null) OnInfo(Port + " 연결 끊김");
        }

        void ReadLoop()
        {
            while (running)
            {
                SerialPort s;
                lock (writeLock) s = sp;
                if (s == null)
                {
                    try { Open(); }
                    catch (Exception) { Sleep(3000); }   // 프린터 꺼짐·USB 빠짐: 3초마다 다시 시도
                    continue;
                }
                string line;
                try { line = s.ReadLine(); }
                catch (TimeoutException) { continue; }
                catch (Exception) { CloseSerial(); okSignal.Set(); continue; }   // USB 빠짐 등
                line = line.Trim('\r', '\n', '\0', ' ');
                if (line.Length == 0) continue;
                lastRx = DateTime.UtcNow.Ticks;
                if (line.StartsWith("ok")) okSignal.Set();
                if (OnLine != null) OnLine("<", line);
            }
        }

        void WriteLoop()
        {
            while (running)
            {
                string c;
                try { c = queue.Take(); } catch (Exception) { return; }
                if (!running) return;
                if (c == "") continue;
                while (running && !Connected) Thread.Sleep(200);   // 연결될 때까지 기다린다 (끊긴 동안 누른 버튼도 사라지지 않게)
                okSignal.Reset();
                WriteRaw(c);
                // G28·G29·M109·M190 은 오래 걸린다 (Marlin 이 "busy" 를 보내며 기다리게 함). 자동 보고가 계속 와서
                // '조용하면 포기'는 안 통하므로 명령별 최대 시간으로 끊는다. ok 를 놓쳐도 여기서 풀린다.
                string w = c.ToUpperInvariant().Split(' ')[0];
                double limit = w == "G28" || w == "G29" || w == "M109" || w == "M190" || w == "M400" || w == "M303" ? 900 : 20;
                var t0 = DateTime.UtcNow;
                while (running && Connected && !okSignal.WaitOne(500))
                    if ((DateTime.UtcNow - t0).TotalSeconds > limit) break;
            }
        }

        void Sleep(int ms)
        {
            for (int i = 0; i < ms / 100 && running; i++) Thread.Sleep(100);
        }

        // 프린터(STM32 가상 COM, VID 0483 PID 5740)를 앞에 둔 COM 포트 목록
        public static List<KeyValuePair<string, string>> ListPorts()
        {
            var list = new List<KeyValuePair<string, string>>();
            var names = new HashSet<string>(SerialPort.GetPortNames());
            try
            {
                using (var q = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'"))
                    foreach (ManagementObject o in q.Get())
                    {
                        string name = (o["Name"] as string) ?? "", id = (o["PNPDeviceID"] as string) ?? "";
                        int a = name.LastIndexOf("(COM"), b = name.LastIndexOf(')');
                        if (a < 0 || b < a) continue;
                        string port = name.Substring(a + 1, b - a - 1);
                        if (!names.Remove(port)) continue;
                        bool printer = id.ToUpperInvariant().Contains("VID_0483&PID_5740");
                        var kv = new KeyValuePair<string, string>(port, port + (printer ? "  (3D 프린터)" : "  " + name.Substring(0, a).Trim()));
                        if (printer) list.Insert(0, kv); else list.Add(kv);
                    }
            }
            catch (Exception) { }
            foreach (var n in names) list.Add(new KeyValuePair<string, string>(n, n));
            return list;
        }
    }
}
