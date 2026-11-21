// Cremaker Control — Cremaker Metal V2 (SKR2 + Marlin 2.0.8.1) 를 USB로 실시간 조작하고,
// 프린터 화면에서 한 조작까지 감지해 로그로 모으는 프로그램.
// 실행: Cremaker_Control.exe              (보통)
//       Cremaker_Control.exe --selftest   (자체 시험, 빌드 때 실행)

using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Cremaker Control")]
[assembly: System.Reflection.AssemblyProduct("Cremaker Control")]
[assembly: System.Reflection.AssemblyCompany("KIM YEONGDO")]
[assembly: System.Reflection.AssemblyCopyright("Copyright (c) 2026 KIM YEONGDO")]
[assembly: System.Reflection.AssemblyVersion("0.2.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("0.2.0.0")]

namespace CremakerControl
{
    static class App
    {
        public const string Name = "Cremaker Control";
        public const string Author = "KIM YEONGDO";
        public const string Repo = "Dubitnarae/Cremaker_Control";   // 자동 업데이트가 릴리스를 확인하는 저장소(공개여야 함)

        public static string Version
        {
            get { var v = typeof(App).Assembly.GetName().Version; return v.Major + "." + v.Minor + "." + v.Build; }
        }

        public static string ExeDir { get { return Path.GetDirectoryName(Application.ExecutablePath); } }
        public static string LogRoot { get { return Path.Combine(ExeDir, "logs"); } }

        public static void Restart()
        {
            Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = false, WorkingDirectory = ExeDir });
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (Array.IndexOf(args, "--selftest") >= 0) return SelfTest.Run();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Updater.CleanupOld();
            var win = new MainForm();
            Application.Run(win);
            if (win.RestartAfterUpdate) App.Restart();
            return 0;
        }
    }
}
