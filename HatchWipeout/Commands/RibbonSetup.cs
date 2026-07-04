using System;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(HatchWipeout.Commands.RibbonSetup))]

namespace HatchWipeout.Commands
{
    public class RibbonSetup : IExtensionApplication
    {
        private const string TabId = "TH_TOOLS_TAB";
        private const string TabTitle = "TH Tools";
        private const string PanelTitle = "Hatch Wipeout";
        private readonly RibbonCommandHandler _cmdHandler = new RibbonCommandHandler();

        public void Initialize()
        {
            try
            {
                Application.Idle += OnApplicationIdle;
                Application.SystemVariableChanged += OnSystemVariableChanged;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error initializing plugin: " + ex.Message);
            }
        }

        public void Terminate()
        {
            try
            {
                Application.Idle -= OnApplicationIdle;
                Application.SystemVariableChanged -= OnSystemVariableChanged;
            }
            catch
            {
            }
        }

        private void OnApplicationIdle(object sender, EventArgs e)
        {
            if (ComponentManager.Ribbon != null)
            {
                Application.Idle -= OnApplicationIdle;
                CreateRibbon();
            }
        }

        private void OnSystemVariableChanged(object sender, SystemVariableChangedEventArgs e)
        {
            if (e.Name.Equals("WSCURRENT", StringComparison.OrdinalIgnoreCase) && ComponentManager.Ribbon != null)
            {
                CreateRibbon();
            }
        }

        private void CreateRibbon()
        {
            try
            {
                RibbonControl ribbon = ComponentManager.Ribbon;
                if (ribbon == null) return;

                RibbonTab rtb = ribbon.FindTab(TabId);
                if (rtb == null)
                {
                    rtb = new RibbonTab { Title = TabTitle, Id = TabId };
                    ribbon.Tabs.Add(rtb);
                    rtb.IsActive = true;
                }
                else
                {
                    rtb.IsActive = true;
                }

                // Xóa panel cũ nếu đã tồn tại để tránh trùng lặp khi đổi Workspace
                RibbonPanel existingPanel = null;
                foreach (RibbonPanel p in rtb.Panels)
                {
                    if (p.Source != null && p.Source.Title.Equals(PanelTitle, StringComparison.OrdinalIgnoreCase))
                    {
                        existingPanel = p;
                        break;
                    }
                }
                if (existingPanel != null)
                {
                    rtb.Panels.Remove(existingPanel);
                }

                // Tạo Panel mới
                RibbonPanelSource rps = new RibbonPanelSource { Title = PanelTitle, Id = "TH_HATCH_WIPEOUT_PANEL" };
                RibbonPanel rp = new RibbonPanel { Source = rps };

                // Tạo Button TH
                RibbonButton btnHatch = new RibbonButton
                {
                    Id = "TH_BTN",
                    Text = "\nBlock Hatch",
                    ShowText = true,
                    ShowImage = true,
                    Size = RibbonItemSize.Large,
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    CommandParameter = "\x03\x03" + "TH ",
                    CommandHandler = _cmdHandler,
                    LargeImage = GetTextBitmap(32, "BH"),
                    Image = GetTextBitmap(16, "BH")
                };

                // Tạo Button TW
                RibbonButton btnWipeout = new RibbonButton
                {
                    Id = "TW_BTN",
                    Text = "\nBlock Wipeout",
                    ShowText = true,
                    ShowImage = true,
                    Size = RibbonItemSize.Large,
                    Orientation = System.Windows.Controls.Orientation.Vertical,
                    CommandParameter = "\x03\x03" + "TW ",
                    CommandHandler = _cmdHandler,
                    LargeImage = GetTextBitmap(32, "BW"),
                    Image = GetTextBitmap(16, "BW")
                };

                rps.Items.Add(btnHatch);
                rps.Items.Add(new RibbonSeparator());
                rps.Items.Add(btnWipeout);
                rtb.Panels.Add(rp);
            }
            catch (System.Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?.Editor.WriteMessage($"\n[HatchWipeout] Error loading ribbon: {ex.Message}\n");
            }
        }

        // Tạo Icon động dựa trên Text (32x32 hoặc 16x16) với thiết kế đồng bộ của TH Tools
        private System.Windows.Media.ImageSource GetTextBitmap(int size, string text)
        {
            try
            {
                System.Windows.Media.DrawingVisual visual = new System.Windows.Media.DrawingVisual();
                using (System.Windows.Media.DrawingContext dc = visual.RenderOpen())
                {
                    // Màu nền Accent Blue (#2563EB)
                    dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)), null, new System.Windows.Rect(0, 0, size, size));

                    // Viền trắng mảnh xung quanh
                    dc.DrawRectangle(null, new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, size == 32 ? 1.0 : 0.5), new System.Windows.Rect(0.5, 0.5, size - 1, size - 1));

                    double fontSize = size == 32 ? 14 : 9;
                    System.Windows.Media.FormattedText ft = new System.Windows.Media.FormattedText(
                        text,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily("Segoe UI"), System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal),
                        fontSize,
                        System.Windows.Media.Brushes.White,
                        1.0);

                    // Vẽ text vào chính giữa tâm
                    dc.DrawText(ft, new System.Windows.Point((size - ft.Width) / 2, (size - ft.Height) / 2));
                }

                System.Windows.Media.Imaging.RenderTargetBitmap rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(visual);
                return rtb;
            }
            catch
            {
                return null;
            }
        }
    }

    public class RibbonCommandHandler : ICommand
    {
        public event EventHandler CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object parameter) => true;

        public void Execute(object parameter)
        {
            string cmd = null;
            if (parameter is RibbonButton btn)
                cmd = btn.CommandParameter as string;
            else if (parameter is string s)
                cmd = s;

            if (!string.IsNullOrEmpty(cmd))
            {
                Document doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                // Tách: lệnh cancel (\x1B\x1B) đi trước, tên command đi sau (buffer riêng)
                // Loại bỏ prefix \x03 nếu có, chỉ lấy tên lệnh thực
                string cleanCmd = cmd.Replace("\x03", "").Trim();
                if (string.IsNullOrEmpty(cleanCmd)) return;

                doc.SendStringToExecute("\x1B\x1B", true, false, false);
                doc.SendStringToExecute(cleanCmd + "\n", true, false, false);
            }
        }
    }
}
