using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using b1_chat_console.ViewModels;

namespace b1_chat_console.Views;

public partial class MeshTopologyCardView : UserControl
{
    public MeshTopologyCardView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MeshTopologyViewModel oldVm)
            {
                oldVm.HopWaveRequested -= PlayHopWave;
                oldVm.TalkPulseRequested -= PlayTalkPulse;
            }
            if (e.NewValue is MeshTopologyViewModel newVm)
            {
                newVm.HopWaveRequested += PlayHopWave;
                newVm.TalkPulseRequested += PlayTalkPulse;
            }
        };
    }

    // One-shot expanding/fading ring from the master's position, played on every
    // outgoing anim broadcast (see MeshTopologyViewModel.OnAnimSent).
    private void PlayHopWave()
    {
        Dispatcher.Invoke(() =>
        {
            var transform = (ScaleTransform)((TransformGroup)HopWaveRing.RenderTransform).Children[0];
            var sb = new Storyboard();

            var opacity = new DoubleAnimation(0.8, 0, TimeSpan.FromSeconds(1.1));
            Storyboard.SetTarget(opacity, HopWaveRing);
            Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));
            sb.Children.Add(opacity);

            var scaleX = new DoubleAnimation(1, 6.5, TimeSpan.FromSeconds(1.1));
            Storyboard.SetTarget(scaleX, transform);
            Storyboard.SetTargetProperty(scaleX, new PropertyPath(ScaleTransform.ScaleXProperty));
            sb.Children.Add(scaleX);

            var scaleY = new DoubleAnimation(1, 6.5, TimeSpan.FromSeconds(1.1));
            Storyboard.SetTarget(scaleY, transform);
            Storyboard.SetTargetProperty(scaleY, new PropertyPath(ScaleTransform.ScaleYProperty));
            sb.Children.Add(scaleY);

            sb.Begin();
        });
    }

    // Rhythmic pulse on the master node for the TALK animation's known duration —
    // not audio-reactive (no amplitude data available from the DFPlayer).
    private void PlayTalkPulse(int durationMs)
    {
        Dispatcher.Invoke(() =>
        {
            var transform = (ScaleTransform)((TransformGroup)TalkPulseRing.RenderTransform).Children[0];
            var sb = new Storyboard { RepeatBehavior = new RepeatBehavior(TimeSpan.FromMilliseconds(Math.Max(durationMs, 400))) };

            var opacity = new DoubleAnimation(0, 0.7, TimeSpan.FromSeconds(0.35)) { AutoReverse = true };
            Storyboard.SetTarget(opacity, TalkPulseRing);
            Storyboard.SetTargetProperty(opacity, new PropertyPath(OpacityProperty));
            sb.Children.Add(opacity);

            var scaleX = new DoubleAnimation(1, 1.5, TimeSpan.FromSeconds(0.35)) { AutoReverse = true };
            Storyboard.SetTarget(scaleX, transform);
            Storyboard.SetTargetProperty(scaleX, new PropertyPath(ScaleTransform.ScaleXProperty));
            sb.Children.Add(scaleX);

            var scaleY = new DoubleAnimation(1, 1.5, TimeSpan.FromSeconds(0.35)) { AutoReverse = true };
            Storyboard.SetTarget(scaleY, transform);
            Storyboard.SetTargetProperty(scaleY, new PropertyPath(ScaleTransform.ScaleYProperty));
            sb.Children.Add(scaleY);

            sb.Begin();
        });
    }
}
