// SPDX-License-Identifier: GPL-3.0-or-later
// Pebrel v2.2.0 (2fde458): display/ui/theme.rs::nord_skin, display/ui/tokens.rs,
// gpui_shell/widgets.rs and settings_pane/design.rs. See PEBREL-NOTICE.md.
// GPUI is Rust-only; this is the shared WinForms adapter for its visual contract.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PebrelUi
{
    internal static class PebrelTheme
    {
        internal static readonly Color Panel = Color.FromArgb(0x2e, 0x34, 0x40);
        internal static readonly Color Input = Color.FromArgb(0x3b, 0x42, 0x52);
        internal static readonly Color Ink = Color.FromArgb(0xe5, 0xe9, 0xf0);
        internal static readonly Color Muted = Color.FromArgb(0xc0, 0xc7, 0xd3);
        internal static readonly Color Strong = Color.FromArgb(0xec, 0xef, 0xf4);
        internal static readonly Color Faint = Color.FromArgb(0x7b, 0x82, 0x94);
        internal static readonly Color Accent = Color.FromArgb(0x88, 0xc0, 0xd0);
        internal static readonly Color Danger = Color.FromArgb(0xbf, 0x61, 0x6a);
        internal static readonly Color Success = Color.FromArgb(0xa3, 0xbe, 0x8c);
        internal static readonly Color Warning = Color.FromArgb(0xeb, 0xcb, 0x8b);
        internal static readonly Color Border = Blend(Panel, Color.White, 15 / 255F);
        internal static readonly Color Selected = Blend(Panel, Accent, 46 / 255F);
        internal const int Radius = 6;
        internal const int ControlHeight = 32;
        internal const int NavigationWidth = 176;
        [DllImport("gdi32.dll")] private static extern IntPtr CreateSolidBrush(int color);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr dc, int color);
        [DllImport("gdi32.dll")] private static extern int SetBkColor(IntPtr dc, int color);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string appName, string idList);
        internal static void StyleNativeControl(Control control) { SetWindowTheme(control.Handle, "DarkMode_Explorer", null); }
        private static readonly IntPtr InputBrush = CreateSolidBrush(ColorTranslator.ToWin32(Input));
        static PebrelTheme() { AppDomain.CurrentDomain.ProcessExit += delegate { DeleteObject(InputBrush); }; }
        internal static bool ColorNativeInput(ref Message message, bool enabled)
        {
            int kind = message.Msg & ~0x2000; // WinForms reflects child edit/static colors.
            if (kind != 0x0133 && kind != 0x0138 && kind != 0x0134) return false;
            SetTextColor(message.WParam, ColorTranslator.ToWin32(enabled ? Ink : Faint));
            SetBkColor(message.WParam, ColorTranslator.ToWin32(Input));
            message.Result = InputBrush;
            return true;
        }

        internal static Color Blend(Color background, Color foreground, float alpha)
        {
            return Color.FromArgb((int)Math.Round(background.R * (1 - alpha) + foreground.R * alpha),
                (int)Math.Round(background.G * (1 - alpha) + foreground.G * alpha),
                (int)Math.Round(background.B * (1 - alpha) + foreground.B * alpha));
        }

        internal static GraphicsPath Rounded(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            if (d <= 0) { path.AddRectangle(bounds); return path; }
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int length);
        internal static void ApplyTitleBar(Form form)
        {
            if (Environment.OSVersion.Version.Major < 6) return;
            int dark = 1;
            DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int));
            int background = ColorTranslator.ToWin32(Panel);
            DwmSetWindowAttribute(form.Handle, 35, ref background, sizeof(int));
            int foreground = ColorTranslator.ToWin32(Ink);
            DwmSetWindowAttribute(form.Handle, 36, ref foreground, sizeof(int));
        }
    }

    internal enum ButtonKind { Ghost, Outline, Primary, Danger }

    internal sealed class PebrelButton : Button
    {
        private bool _hover, _pressed;
        private ButtonKind _kind;
        internal ButtonKind Kind { get { return _kind; } set { _kind = value; Invalidate(); } }
        internal PebrelButton()
        {
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = PebrelTheme.Panel;
            ForeColor = PebrelTheme.Ink;
            UseVisualStyleBackColor = false;
            Padding = new Padding(12, 4, 12, 4);
            MinimumSize = new Size(32, PebrelTheme.ControlHeight);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size size = base.GetPreferredSize(proposedSize);
            return new Size(size.Width, Math.Max(PebrelTheme.ControlHeight, size.Height));
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? PebrelTheme.Panel : Parent.BackColor);
            Color fill = PebrelTheme.Panel, ink = PebrelTheme.Ink;
            if (Kind == ButtonKind.Primary) { fill = PebrelTheme.Accent; ink = PebrelTheme.Panel; }
            if (Kind == ButtonKind.Danger) ink = PebrelTheme.Danger;
            if (!Enabled) { fill = PebrelTheme.Panel; ink = PebrelTheme.Faint; }
            else if (_hover || _pressed)
                fill = PebrelTheme.Blend(fill, PebrelTheme.Ink, _pressed ? 0.14F : Kind == ButtonKind.Primary ? 0.08F : 0.045F);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = PebrelTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), PebrelTheme.Radius))
            {
                using (Brush brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
                if (Kind == ButtonKind.Outline || Kind == ButtonKind.Danger || (Focused && ShowFocusCues))
                    using (Pen pen = new Pen(Focused && ShowFocusCues ? PebrelTheme.Accent : PebrelTheme.Border)) e.Graphics.DrawPath(pen, path);
            }
            TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ink, flags);
        }
    }

    // Keep native ComboBox selection, editing, popup and keyboard behavior.
    internal sealed class PebrelComboBox : ComboBox
    {
        internal PebrelComboBox()
        {
            FlatStyle = FlatStyle.Flat;
            BackColor = PebrelTheme.Input;
            ForeColor = PebrelTheme.Ink;
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = 23;
        }
        private void ClearInactiveSelection()
        {
            if (!IsDisposed && IsHandleCreated && !ContainsFocus && DropDownStyle == ComboBoxStyle.DropDown)
            {
                SelectionStart = Text.Length;
                SelectionLength = 0;
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Native edit initialization selects text after handle creation.
            BeginInvoke(new Action(ClearInactiveSelection));
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); ClearInactiveSelection(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); ClearInactiveSelection(); }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (Brush brush = new SolidBrush(selected ? PebrelTheme.Selected : PebrelTheme.Panel)) e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, e.Bounds,
                Enabled ? PebrelTheme.Ink : PebrelTheme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
        protected override void WndProc(ref Message m)
        {
            if (PebrelTheme.ColorNativeInput(ref m, Enabled)) return;
            base.WndProc(ref m);
            if (m.Msg != 0x000F && m.Msg != 0x0318 && m.Msg != 0x0317) return;
            using (Graphics graphics = m.Msg == 0x000F ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(m.WParam))
            {
                if (DropDownStyle == ComboBoxStyle.DropDownList || !Enabled)
                {
                    using (Brush brush = new SolidBrush(PebrelTheme.Input)) graphics.FillRectangle(brush, ClientRectangle);
                    TextRenderer.DrawText(graphics, Text, Font, new Rectangle(2, 0, Math.Max(0, Width - 24), Height),
                        Enabled ? PebrelTheme.Ink : PebrelTheme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
                Rectangle arrow = new Rectangle(Math.Max(0, Width - 22), 0, 22, Height);
                using (Brush brush = new SolidBrush(PebrelTheme.Input)) graphics.FillRectangle(brush, arrow);
                using (Pen border = new Pen(PebrelTheme.Input)) graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
                int x = Width - 12, y = Height / 2;
                using (Pen pen = new Pen(Enabled ? PebrelTheme.Muted : PebrelTheme.Faint, 1.2F))
                    graphics.DrawLines(pen, new[] { new Point(x - 4, y - 2), new Point(x, y + 2), new Point(x + 4, y - 2) });
            }
        }
    }

    internal sealed class PebrelNumber : NumericUpDown
    {
        private SpinnerPaint _spinner;
        internal PebrelNumber()
        {
            BorderStyle = BorderStyle.None;
            BackColor = PebrelTheme.Input;
            ForeColor = PebrelTheme.Ink;
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            foreach (Control child in Controls)
                if (!(child is TextBox)) { _spinner = new SpinnerPaint(child); break; }
        }
        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (_spinner != null) { _spinner.ReleaseHandle(); _spinner = null; }
            base.OnHandleDestroyed(e);
        }
        protected override void WndProc(ref Message m)
        {
            if (PebrelTheme.ColorNativeInput(ref m, Enabled)) return;
            base.WndProc(ref m);
        }
        private sealed class SpinnerPaint : NativeWindow
        {
            private readonly Control _control;
            internal SpinnerPaint(Control control) { _control = control; AssignHandle(control.Handle); }
            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg != 0x000F && m.Msg != 0x0318 && m.Msg != 0x0317) return;
                using (Graphics graphics = m.Msg == 0x000F ? Graphics.FromHwnd(Handle) : Graphics.FromHdc(m.WParam))
                {
                    graphics.Clear(PebrelTheme.Input);
                    int x = _control.Width / 2;
                    using (Pen pen = new Pen(_control.Enabled ? PebrelTheme.Muted : PebrelTheme.Faint, 1.2F))
                    {
                        int y = _control.Height / 4;
                        graphics.DrawLines(pen, new[] { new Point(x - 3, y + 1), new Point(x, y - 2), new Point(x + 3, y + 1) });
                        y = _control.Height * 3 / 4;
                        graphics.DrawLines(pen, new[] { new Point(x - 3, y - 1), new Point(x, y + 2), new Point(x + 3, y - 1) });
                    }
                }
            }
        }
    }

    internal sealed class PebrelLabel : Label
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
    }

    internal sealed class PebrelInputFrame : Panel
    {
        private readonly Control _input;
        internal PebrelInputFrame(Control input)
        {
            _input = input;
            Height = 36;
            BackColor = PebrelTheme.Panel;
            Padding = new Padding(8, 3, 5, 3);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Controls.Add(input);
            input.Dock = DockStyle.Fill;
            input.GotFocus += delegate { Invalidate(); };
            input.LostFocus += delegate { Invalidate(); };
            input.EnabledChanged += delegate { Invalidate(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = PebrelTheme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), PebrelTheme.Radius))
            {
                using (Brush brush = new SolidBrush(PebrelTheme.Input)) e.Graphics.FillPath(brush, path);
                using (Pen pen = new Pen(_input.ContainsFocus ? PebrelTheme.Accent : PebrelTheme.Border)) e.Graphics.DrawPath(pen, path);
            }
        }
    }

    // Settings headings use whitespace, not GroupBox outlines (design.rs::group).
    internal sealed class PebrelGroup : GroupBox
    {
        public override Rectangle DisplayRectangle
        {
            get { return new Rectangle(Padding.Left, Padding.Top, Math.Max(0, ClientSize.Width - Padding.Horizontal), Math.Max(0, ClientSize.Height - Padding.Vertical)); }
        }
        internal PebrelGroup()
        {
            BackColor = PebrelTheme.Panel;
            ForeColor = PebrelTheme.Ink;
            Padding = new Padding(0, 32, 0, 0);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            using (Font heading = new Font(Font.FontFamily, 11F, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, Text, heading, new Point(0, 0), PebrelTheme.Strong, TextFormatFlags.NoPadding);
        }
    }

    // Left navigation keeps TabControl's selection, accessibility and arrow keys.
    internal sealed class PebrelTabs : TabControl
    {
        internal PebrelTabs()
        {
            Alignment = TabAlignment.Left;
            Multiline = true;
            SizeMode = TabSizeMode.Fixed;
            ItemSize = new Size(40, PebrelTheme.NavigationWidth);
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(PebrelTheme.Panel);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(PebrelTheme.Border)) e.Graphics.DrawLine(pen, DisplayRectangle.Left - 2, 0, DisplayRectangle.Left - 2, Height);
            for (int index = 0; index < TabCount; index++)
            {
                Rectangle bounds = GetTabRect(index);
                bounds.Inflate(-6, -2);
                bool active = index == SelectedIndex;
                if (active)
                    using (GraphicsPath path = PebrelTheme.Rounded(bounds, PebrelTheme.Radius))
                    using (Brush brush = new SolidBrush(PebrelTheme.Selected)) e.Graphics.FillPath(brush, path);
                bounds.Inflate(-10, 0);
                TextRenderer.DrawText(e.Graphics, TabPages[index].Text, Font, bounds,
                    active ? PebrelTheme.Strong : PebrelTheme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
    }
}
