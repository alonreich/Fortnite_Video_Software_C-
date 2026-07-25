using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using System;
using System.Threading.Tasks;
using Avalonia.Animation;
using Avalonia.Styling;

namespace FortniteVideoSoftware.App.Controls;

public partial class AmbientDropzoneControl : UserControl
{
    private bool _isPulsing = false;
    private Border? _container;
    private Border? _pulseBorder;

    public AmbientDropzoneControl()
    {
        InitializeComponent();
        _container = this.FindControl<Border>("ContainerBorder");
        _pulseBorder = this.FindControl<Border>("PulseBorder");
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void Activate()
    {
        IsHitTestVisible = true;
        Opacity = 1;
        if (_container != null)
        {
            _container.Classes.Remove("DropzoneInactive");
            _container.Classes.Add("DropzoneActive");
        }
        StartPulse();
    }

    public void Deactivate()
    {
        IsHitTestVisible = false;
        if (_container != null)
        {
            _container.Classes.Remove("DropzoneActive");
            _container.Classes.Add("DropzoneInactive");
        }
        _isPulsing = false;
        Task.Delay(300).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(() => Opacity = 0));
    }

    private async void StartPulse()
    {
        if (_isPulsing || _pulseBorder == null) return;
        _isPulsing = true;
        
        var transform = _pulseBorder.RenderTransform as ScaleTransform;
        if (transform == null)
        {
            transform = new ScaleTransform(1.0, 1.0);
            _pulseBorder.RenderTransform = transform;
        }

        while (_isPulsing)
        {
            await AnimateScale(transform, 1.0, 1.05, TimeSpan.FromMilliseconds(400));
            if (!_isPulsing) break;
            await AnimateScale(transform, 1.05, 1.0, TimeSpan.FromMilliseconds(400));
        }
        transform.ScaleX = 1.0;
        transform.ScaleY = 1.0;
    }

    private async Task AnimateScale(ScaleTransform t, double from, double to, TimeSpan duration)
    {
        int steps = 20;
        int delay = (int)(duration.TotalMilliseconds / steps);
        double diff = to - from;
        for (int i = 1; i <= steps; i++)
        {
            if (!_isPulsing) break;
            double progress = (double)i / steps;
            // cubic ease out
            double ease = 1 - Math.Pow(1 - progress, 3);
            double val = from + (diff * ease);
            t.ScaleX = val;
            t.ScaleY = val;
            await Task.Delay(delay);
        }
    }
}
