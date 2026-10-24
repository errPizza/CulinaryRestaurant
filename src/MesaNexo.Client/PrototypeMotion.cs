using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;
namespace MesaNexo.Client;
// CSS reference: 140ms hover, 240ms page, 360ms dialog, 320ms drawer;
// cubic-bezier(.22,1,.36,1). Transforms do not affect layout or keyboard focus.
public static class PrototypeMotion {
    public static bool ReduceMotion { get; set; }
    public static bool SuppressAnimations { get; set; }
    public static bool Reduced => ReduceMotion || SuppressAnimations || SystemReduced();
    static bool SystemReduced() {

        if (!OperatingSystem.IsWindows()) return false;
        return SystemParametersInfo(0x1042, 0, out var enabled, 0) && !enabled;

    }
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
    static SplineEasing Ease() => new() { X1 = .22, Y1 = 1, X2 = .36, Y2 = 1 };
    public static void Enter(Control target, int milliseconds = 240, double x = 0, double y = 10) {

        if (Reduced) return;
        target.RenderTransform = TransformOperations.Parse(FormattableString.Invariant($"translate({x}px, {y}px)"));
        target.Opacity = 0;
        void Show() {

            if (Reduced) { target.Opacity = 1; target.RenderTransform = TransformOperations.Parse("translate(0px, 0px)"); return; }
            var duration = TimeSpan.FromMilliseconds(milliseconds);
            target.Transitions = new Transitions { new DoubleTransition { Property = Visual.OpacityProperty, Duration = duration, Easing = Ease() }, new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = duration, Easing = Ease() } };
            target.Opacity = 1;
            target.RenderTransform = TransformOperations.Parse("translate(0px, 0px)");

        }
        // Schedule after attachment so the initial state can render before transition.
        void Schedule() => DispatcherTimer.RunOnce(Show, TimeSpan.FromMilliseconds(16));
        if (target.IsAttachedToVisualTree()) Schedule();
        else target.AttachedToVisualTree += Attached;
        void Attached(object? sender, VisualTreeAttachmentEventArgs e) { target.AttachedToVisualTree -= Attached; Schedule(); }

    }
    public static void Button(Button button) {

        Border? surface = null;
        CancellationTokenSource? pulse = null;
        TransformOperations Scale(double value) => TransformOperations.Parse(FormattableString.Invariant($"scale({value})"));
        double Resting() => button.IsPointerOver ? 1.025 : 1;
        void Configure(Border target, int milliseconds) {

            target.Transitions = Reduced ? null : new Transitions{
                new BrushTransition{Property=Border.BackgroundProperty,Duration=TimeSpan.FromMilliseconds(140)},
                new BrushTransition{Property=Border.BorderBrushProperty,Duration=TimeSpan.FromMilliseconds(140)},
                new TransformOperationsTransition{Property=Visual.RenderTransformProperty,Duration=TimeSpan.FromMilliseconds(milliseconds),Easing=Ease()}};

        }
        void StopPulse() { pulse?.Cancel(); pulse = null; }
        void Update() {

            if (surface is null) return;
            StopPulse();
            Configure(surface, 110);
            var size = Reduced || !button.IsEffectivelyEnabled ? 1 : button.IsPressed ? 0.965 : Resting();
            surface.RenderTransform = Scale(size);

        }
        async Task Pop() {

            if (surface is not Border target || Reduced || !button.IsEffectivelyEnabled) return;
            StopPulse();
            var current = new CancellationTokenSource();
            pulse = current;
            try {

                Configure(target, 80);
                target.RenderTransform = Scale(1.06);
                await Task.Delay(90, current.Token);
                if (ReferenceEquals(surface, target) && ReferenceEquals(pulse, current)) {

                    Configure(target, 170);
                    target.RenderTransform = Scale(Reduced ? 1 : Resting());
                    await Task.Delay(180, current.Token);

                }

            } catch (OperationCanceledException) { } finally { if (ReferenceEquals(pulse, current)) pulse = null; current.Dispose(); }

        }
        button.TemplateApplied += (_, e) => {

            StopPulse();
            surface = e.NameScope.Find<Border>("PART_Border");
            if (surface is null) return;
            surface.ClipToBounds = false;
            surface.RenderTransformOrigin = RelativePoint.Center;
            surface.RenderTransform = Scale(1);
            Configure(surface, 110);

        };
        button.PropertyChanged += (_, e) => {

            // Space raises Click before resetting IsPressed; keep the release pop alive.
            if (e.Property == Avalonia.Controls.Button.IsPressedProperty && !button.IsPressed && pulse is not null && !Reduced) return;
            if (e.Property == InputElement.IsPointerOverProperty || e.Property == Avalonia.Controls.Button.IsPressedProperty || e.Property == InputElement.IsEnabledProperty) Update();

        };
        // Feedback only observes native Click; it never raises a second click or delays the action.
        button.Click += (_, e) => { if (ReferenceEquals(e.Source, button)) _ = Pop(); };
        button.DetachedFromVisualTree += (_, _) => { StopPulse(); if (surface is not null) { surface.Transitions = null; surface.RenderTransform = Scale(1); } };

    }
}
