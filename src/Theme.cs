// 애플풍 화면 — 밝은 회색 바탕, 흰 둥근 카드, 둥근 버튼, 파랑 강조색.
// 컨트롤 배치는 그대로 두고 색·모양만 바꾼다 (Theme.Apply 를 화면 다 만든 뒤 한 번 부른다).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CremakerControl
{
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(245, 245, 247);
        public static readonly Color Card = Color.White;
        public static readonly Color Text = Color.FromArgb(29, 29, 31);
        public static readonly Color Sub = Color.FromArgb(110, 110, 115);
        public static readonly Color Line = Color.FromArgb(229, 229, 234);
        public static readonly Color Fill = Color.FromArgb(233, 233, 238);   // 보통 버튼
        public static readonly Color Accent = Color.FromArgb(0, 122, 255);
        public static readonly Color Red = Color.FromArgb(255, 59, 48);
        public static readonly Color Orange = Color.FromArgb(255, 149, 0);
        public static readonly Color Yellow = Color.FromArgb(255, 244, 214);
        public static readonly Color Pink = Color.FromArgb(255, 229, 229);

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Color Darker(Color c, int by)
        {
            return Color.FromArgb(c.A, Math.Max(0, c.R - by), Math.Max(0, c.G - by), Math.Max(0, c.B - by));
        }

        // 바탕·글자색을 화면 전체에 입힌다. 이미 따로 정한 색(DimGray 등)은 같은 뜻의 애플 색으로 바꾼다.
        public static void Apply(Control c)
        {
            if (c is Form || c is Panel || c is SplitContainer || c is SplitterPanel || c is TabPage) c.BackColor = Bg;
            if (c is Label || c is CheckBox || c is RadioButton)
            {
                if (c.ForeColor == Color.DimGray || c.ForeColor == Color.Gray) c.ForeColor = Sub;
                else if (c.ForeColor == Color.DarkOrange) c.ForeColor = Orange;
                else if (c.ForeColor == SystemColors.ControlText) c.ForeColor = Text;
            }
            var lv = c as ListView;
            if (lv != null) { lv.BorderStyle = BorderStyle.None; lv.GridLines = false; lv.BackColor = Card; lv.ForeColor = Text; }
            var tb = c as TextBox;
            if (tb != null) { tb.BorderStyle = tb.Multiline ? BorderStyle.None : BorderStyle.FixedSingle; tb.BackColor = Card; tb.ForeColor = Text; }
            var nu = c as NumericUpDown;
            if (nu != null) nu.BorderStyle = BorderStyle.FixedSingle;
            var cb = c as ComboBox;
            if (cb != null) cb.FlatStyle = FlatStyle.Flat;
            foreach (Control k in c.Controls) Apply(k);
            if (c is CardBox) c.BackColor = Card;   // 카드 안 글자·입력칸은 흰 바탕을 물려받는다
        }
    }

    // GroupBox 대신 쓰는 흰 둥근 카드 (제목은 위 왼쪽에 회색 글씨)
    class CardBox : GroupBox
    {
        public CardBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12))
            using (var b = new SolidBrush(Theme.Card))
            using (var p = new Pen(Theme.Line))
            {
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            using (var f = new Font(Font, FontStyle.Bold))
                TextRenderer.DrawText(g, Text, f, new Point(10, 4), Theme.Sub);
        }
    }

    // 둥근 버튼 — BackColor 로 채우고, 마우스 올림·누름은 조금 어둡게, 꺼지면 옅게
    class RoundButton : Button
    {
        bool hover, down;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = Theme.Fill;
            ForeColor = Theme.Text;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = !Enabled ? Color.FromArgb(110, BackColor) : down ? Theme.Darker(BackColor, 30) : hover ? Theme.Darker(BackColor, 14) : BackColor;
            using (var path = Theme.Round(new RectangleF(0, 0, Width - 1, Height - 1), Math.Min(8, Height / 2f)))
            using (var b = new SolidBrush(fill))
                g.FillPath(b, path);
            if (Focused && ShowFocusCues)
                using (var path = Theme.Round(new RectangleF(1, 1, Width - 3, Height - 3), Math.Min(7, Height / 2f)))
                using (var p = new Pen(Color.FromArgb(120, Theme.Accent), 2))
                    g.DrawPath(p, path);
            Color fg = Enabled ? ForeColor : Color.FromArgb(150, ForeColor);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
        }
    }
}
