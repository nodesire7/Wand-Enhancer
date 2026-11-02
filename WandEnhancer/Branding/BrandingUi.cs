using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WandEnhancer.Core;
using WandEnhancer.View.MainWindow;

namespace WandEnhancer.Branding
{
    /// <summary>
    /// Adds the fork's build information on top of the stock screen at runtime. Nothing here
    /// rewrites upstream XAML, so syncing upstream cannot remove the branding.
    /// </summary>
    internal static class BrandingUi
    {
        public static void Apply(Window window)
        {
            if (window == null)
                return;

            window.Title = BrandingInfo.Product + " " + Constants.Version;
            ApplyFooterText();
            AddHeaderBadge(window);
            AddStartupLog(window);
        }

        // The footer runs are DynamicResource lookups, so overriding the keys also covers a
        // language switch: direct Application.Resources entries outrank the locale dictionary.
        private static void ApplyFooterText()
        {
            var resources = Application.Current == null ? null : Application.Current.Resources;
            if (resources == null)
                return;

            resources["mw_made_by"] = BrandingInfo.Credit;
            resources["mw_star_hint"] = BrandingInfo.Contacts;
        }

        private static void AddHeaderBadge(Window window)
        {
            if (!(window.FindName("VersionLabel") is TextBlock versionLabel))
                return;

            // VersionLabel sits in the title StackPanel; skipping the badge keeps the app usable
            // if upstream ever moves or renames it.
            if (!(versionLabel.Parent is Panel host))
                return;

            int index = host.Children.IndexOf(versionLabel) + 1;
            host.Children.Insert(index, CreatePill(
                window, BrandingInfo.Badge, BrandingInfo.Tooltip, BrandingInfo.ForumUrl, 10));
            host.Children.Insert(index + 1, CreatePill(
                window, "TG", "TG: " + BrandingInfo.Telegram, BrandingInfo.TelegramUrl, 6));
        }

        private static Border CreatePill(Window window, string text, string tooltip, string url, double leftMargin)
        {
            var pill = new Border
            {
                Background = window.TryFindResource("Secondary") as Brush,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(leftMargin, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand,
                ToolTip = tooltip,
                Child = new TextBlock
                {
                    Text = text,
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = window.TryFindResource("AccentForeground") as Brush
                }
            };
            pill.MouseLeftButtonUp += (sender, args) => Open(url);
            return pill;
        }

        private static void AddStartupLog(Window window)
        {
            var main = window as MainWindow;
            if (main == null)
                return;

            main.ViewModel.LogList.Insert(0, new LogEntry
            {
                LogType = ELogType.Info,
                Message = "[INFO] " + BrandingInfo.LogLine + " | build " + Constants.Build
            });
        }

        private static void Open(string url)
        {
            try
            {
                Process.Start(url);
            }
            catch (Exception)
            {
                // A missing browser association must not take the window down.
            }
        }
    }
}
