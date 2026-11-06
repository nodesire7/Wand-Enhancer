using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WandEnhancer.Fork.Installer
{
    internal sealed class AccordionGrid : Grid
    {
        protected override Size MeasureOverride(Size availableSize)
        {
            var measured = base.MeasureOverride(availableSize);
            return new Size(double.IsInfinity(availableSize.Width) ? measured.Width : Math.Min(measured.Width, availableSize.Width),
                double.IsInfinity(availableSize.Height) ? measured.Height : Math.Min(measured.Height, availableSize.Height));
        }
    }

    internal sealed class AccordionMotion
    {
        private const double HeaderHeight = 36;
        private readonly Grid host;
        private readonly Expander[] sections;
        private bool changing;
        internal AccordionMotion(Grid host, params Expander[] sections)
        {
            this.host = host; this.sections = sections;
            foreach (var section in sections)
            {
                section.Expanded += Changed; section.Collapsed += Changed;
            }
            host.SizeChanged += (sender, args) => Layout(false);
            host.Loaded += (sender, args) => Layout(false);
        }
        private void Changed(object sender, RoutedEventArgs args)
        {
            if (changing || !ReferenceEquals(sender, args.OriginalSource)) return;
            changing = true;
            var selected = (Expander)sender;
            if (selected.IsExpanded) foreach (var other in sections.Where(item => item != selected)) other.IsExpanded = false;
            changing = false;
            Layout(SystemParameters.ClientAreaAnimation && InputManager.Current.MostRecentInputDevice is MouseDevice);
        }
        internal void Layout(bool animate)
        {
            if (host.ActualHeight <= 0) return;
            foreach (var section in sections)
            {
                section.ApplyTemplate();
                var row = host.RowDefinitions[Grid.GetRow(section)];
                double target = section.IsExpanded ? Math.Max(HeaderHeight, host.ActualHeight - HeaderHeight * (sections.Length - 1)) : HeaderHeight;
                double from = row.ActualHeight;
                row.BeginAnimation(RowDefinition.HeightProperty, null);
                row.Height = new GridLength(target);
                var body = section.Template.FindName("Body", section) as FrameworkElement;
                if (body == null) continue;
                body.Visibility = Visibility.Visible;
                body.IsHitTestVisible = section.IsExpanded;
                body.IsEnabled = section.IsExpanded;
                double opacity = body.Opacity;
                body.BeginAnimation(UIElement.OpacityProperty, null);
                body.Opacity = section.IsExpanded ? 1 : 0;
                var toggle = section.Template.FindName("HeaderToggle", section) as ToggleButton;
                toggle?.ApplyTemplate();
                var chevron = toggle?.Template.FindName("Arrow", toggle) as FrameworkElement;
                var rotation = chevron?.RenderTransform as RotateTransform;
                if (rotation != null && rotation.IsFrozen) { rotation = rotation.Clone(); chevron.RenderTransform = rotation; }
                double previousAngle = rotation?.Angle ?? 0;
                if (rotation != null) rotation.BeginAnimation(RotateTransform.AngleProperty, null);
                if (!animate)
                {
                    if (rotation != null) rotation.Angle = section.IsExpanded ? 180 : 0;
                    if (!section.IsExpanded) body.Visibility = Visibility.Collapsed;
                    continue;
                }
                var duration = TimeSpan.FromMilliseconds(200);
                var height = new AccordionHeightAnimation { From = from, To = target, Duration = duration, FillBehavior = FillBehavior.Stop };
                height.Completed += (sender, args) => { if (!section.IsExpanded) body.Visibility = Visibility.Collapsed; };
                row.BeginAnimation(RowDefinition.HeightProperty, height, HandoffBehavior.SnapshotAndReplace);
                body.BeginAnimation(UIElement.OpacityProperty, Fade(opacity, body.Opacity), HandoffBehavior.SnapshotAndReplace);
                if (rotation != null)
                {
                    double angle = previousAngle;
                    rotation.Angle = section.IsExpanded ? 180 : 0;
                    rotation.BeginAnimation(RotateTransform.AngleProperty, Fade(angle, rotation.Angle), HandoffBehavior.SnapshotAndReplace);
                }
            }
        }
        private static DoubleAnimationUsingKeyFrames Fade(double from, double to)
        {
            var animation = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(200), FillBehavior = FillBehavior.Stop };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), new KeySpline(0.23, 1, 0.32, 1)));
            return animation;
        }
    }

    internal sealed class AccordionHeightAnimation : AnimationTimeline
    {
        public static readonly DependencyProperty FromProperty = DependencyProperty.Register(nameof(From), typeof(double), typeof(AccordionHeightAnimation));
        public static readonly DependencyProperty ToProperty = DependencyProperty.Register(nameof(To), typeof(double), typeof(AccordionHeightAnimation));
        public double From { get => (double)GetValue(FromProperty); set => SetValue(FromProperty, value); }
        public double To { get => (double)GetValue(ToProperty); set => SetValue(ToProperty, value); }
        public override Type TargetPropertyType => typeof(GridLength);
        protected override Freezable CreateInstanceCore() => new AccordionHeightAnimation();
        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
        {
            double progress = new KeySpline(0.23, 1, 0.32, 1).GetSplineProgress(animationClock.CurrentProgress ?? 1);
            return new GridLength(Math.Max(0, From + (To - From) * progress));
        }
    }
}
