// G코드 3D 진행 보기: 출력한 부분은 색, 남은 부분은 옅은 회색 바깥 벽만. 그래픽 카드 없이 GDI+ 로 그린다.
// 마우스: 왼쪽 끌기 = 돌리기, 오른쪽 끌기 = 옮기기, 휠 = 확대, 두 번 클릭 = 처음 보기.
// 끄는 동안에는 바깥 벽만(층이 많으면 몇 층씩 건너뛰어) 그리고, 손을 떼면 전부 다시 그린다.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CremakerControl
{
    class GcodeView : Control
    {
        GcodeInfo g;
        int done = -1;                         // 출력한 선 수 (-1 = 출력 중 아님 → 전부 색으로)
        double nozX = double.NaN, nozY, nozZ;  // 노즐 위치 (모르면 NaN)
        string info = "";
        double yaw = -30, pitch = 35, zoom = 1, panX, panY;
        Point last;
        bool dragging;
        Bitmap cache;
        bool dirty = true;
        public double LastRenderMs;

        static readonly Color[] KindColor = {
            Color.FromArgb(225, 80, 35),    // 바깥 벽
            Color.FromArgb(245, 155, 55),   // 안쪽 벽
            Color.FromArgb(235, 195, 50),   // 윗·아랫면
            Color.FromArgb(205, 150, 95),   // 채움
            Color.FromArgb(60, 160, 170) }; // 기타
        static readonly Color PendingColor = Color.FromArgb(205, 205, 205);

        public GcodeView()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Color.White;
            SetStyle(ControlStyles.Selectable, true);   // 휠을 받으려면 포커스가 필요
        }

        public GcodeInfo File
        {
            get { return g; }
            set { g = value; done = -1; nozX = double.NaN; info = ""; ResetView(); }
        }

        public void SetProgress(int doneCount, double x, double y, double z, string text)
        {
            if (doneCount == done && text == info && (x == nozX || double.IsNaN(x) && double.IsNaN(nozX)) && y == nozY && z == nozZ) return;
            done = doneCount; nozX = x; nozY = y; nozZ = z; info = text;
            dirty = true;
            Invalidate();
        }

        void ResetView() { yaw = -30; pitch = 35; zoom = 1; panX = panY = 0; dirty = true; Invalidate(); }

        // ---------- 3D → 화면 (직교 투영: Z 축 둘레로 yaw, 내려다보는 각 pitch) ----------
        double cosY, sinY, cosP, sinP, scale, ox, oy, mx, my, mz;

        void Setup(Size size)
        {
            double a = yaw * Math.PI / 180, b = pitch * Math.PI / 180;
            cosY = Math.Cos(a); sinY = Math.Sin(a); cosP = Math.Cos(b); sinP = Math.Sin(b);
            mx = (g.MinX + g.MaxX) / 2; my = (g.MinY + g.MaxY) / 2; mz = g.MaxZ / 2;
            double span = Math.Max(Math.Max(g.MaxX - g.MinX, g.MaxY - g.MinY), g.MaxZ) * 1.5 + 1;
            scale = Math.Min(size.Width, size.Height) / span * zoom;
            ox = size.Width / 2.0 + panX; oy = size.Height / 2.0 + panY;
        }

        PointF P(double x, double y, double z)
        {
            double dx = x - mx, dy = y - my, dz = z - mz;
            double sx = cosY * dx - sinY * dy, depth = sinY * dx + cosY * dy;
            return new PointF((float)(ox + scale * sx), (float)(oy - scale * (dz * cosP + depth * sinP)));
        }

        // 선 그리기. fast = 끄는 중 (바깥 벽만, 선이 많으면 층 건너뛰기)
        public void Render(Graphics gr, Size size, bool fast)
        {
            var t0 = DateTime.UtcNow;
            gr.Clear(BackColor);
            if (g == null || g.Segs.Count == 0) return;
            Setup(size);
            gr.SmoothingMode = SmoothingMode.None;
            using (var bed = new Pen(Color.FromArgb(235, 235, 235)))
            {
                for (int v = 0; v <= 220; v += 20)
                {
                    gr.DrawLine(bed, P(v, 0, 0), P(v, 220, 0));
                    gr.DrawLine(bed, P(0, v, 0), P(220, v, 0));
                }
            }
            var segs = g.Segs;
            int n = segs.Count, doneN = done < 0 ? n : Math.Min(done, n);
            int top = doneN > 0 ? segs[doneN - 1].Layer : -1;
            int layerStep = fast ? Math.Max(1, (int)Math.Ceiling(n / 8.0 / 20000)) : 1;   // 바깥 벽을 대략 2만 개 안쪽으로
            var pens = new Pen[KindColor.Length];
            for (int k = 0; k < pens.Length; k++) pens[k] = new Pen(KindColor[k]);
            var pending = new Pen(PendingColor);
            var pts = new List<PointF>();
            Pen cur = null;
            float lx = float.NaN, ly = 0, lz = 0;
            for (int i = 0; i < n; i++)
            {
                var s = segs[i];
                bool isDone = i < doneN;
                if (s.Kind != 0 && (fast || !isDone)) continue;                  // 남은 부분·끄는 중엔 바깥 벽만
                if (fast && s.Layer % layerStep != 0 && s.Layer != top) continue;
                if (isDone && s.Kind == 3 && s.Layer < top - 1) continue;        // 위층에 덮여 안 보이는 안쪽 채움은 생략
                Pen pen = isDone ? pens[s.Kind] : pending;
                if (pen != cur || s.X0 != lx || s.Y0 != ly || s.Z != lz)
                {
                    if (pts.Count >= 2) gr.DrawLines(cur, pts.ToArray());
                    pts.Clear();
                    pts.Add(P(s.X0, s.Y0, s.Z));
                    cur = pen;
                }
                pts.Add(P(s.X1, s.Y1, s.Z));
                lx = s.X1; ly = s.Y1; lz = s.Z;
            }
            if (pts.Count >= 2) gr.DrawLines(cur, pts.ToArray());
            foreach (var p in pens) p.Dispose();
            pending.Dispose();

            // 노즐: 위치 보고가 있으면 그 자리, 없으면 마지막으로 출력한 선 끝
            double nx = nozX, ny = nozY, nz = nozZ;
            if (double.IsNaN(nx) && done > 0) { var s = segs[doneN - 1]; nx = s.X1; ny = s.Y1; nz = s.Z; }
            if (!double.IsNaN(nx) && done >= 0)
            {
                var c = P(nx, ny, nz);
                gr.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Color.Red)) gr.FillEllipse(b, c.X - 5, c.Y - 5, 10, 10);
                using (var w = new Pen(Color.White, 2)) gr.DrawEllipse(w, c.X - 5, c.Y - 5, 10, 10);
            }
            LastRenderMs = (DateTime.UtcNow - t0).TotalMilliseconds;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var gr = e.Graphics;
            if (g == null)
            {
                gr.Clear(BackColor);
                TextRenderer.DrawText(gr, "왼쪽 '출력 파일 맞추기'로 Cura G코드를 열면 여기에 3D로 보입니다.", Font, ClientRectangle, Color.DimGray,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            if (ClientSize.Width < 2 || ClientSize.Height < 2) return;
            if (dragging) Render(gr, ClientSize, true);
            else
            {
                if (cache == null || cache.Size != ClientSize) { if (cache != null) cache.Dispose(); cache = new Bitmap(ClientSize.Width, ClientSize.Height); dirty = true; }
                if (dirty) { using (var cg = Graphics.FromImage(cache)) Render(cg, ClientSize, false); dirty = false; }
                gr.DrawImageUnscaled(cache, 0, 0);
            }
            TextRenderer.DrawText(gr, info, Font, new Point(8, 6), Color.Black);
            TextRenderer.DrawText(gr, "왼쪽 끌기 돌리기 · 오른쪽 끌기 옮기기 · 휠 확대 · 두 번 클릭 처음 보기", Font,
                new Point(8, ClientSize.Height - 20), Color.Gray);
        }

        protected override void OnMouseDown(MouseEventArgs e) { Focus(); last = e.Location; dragging = true; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging || g == null) return;
            int dx = e.X - last.X, dy = e.Y - last.Y;
            last = e.Location;
            if (e.Button == MouseButtons.Left) { yaw += dx * 0.5; pitch = Math.Max(0, Math.Min(90, pitch + dy * 0.5)); }
            else { panX += dx; panY += dy; }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; dirty = true; Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            zoom = Math.Max(0.2, Math.Min(30, zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2)));
            dirty = true;
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e) { ResetView(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && cache != null) { cache.Dispose(); cache = null; }
            base.Dispose(disposing);
        }
    }
}
