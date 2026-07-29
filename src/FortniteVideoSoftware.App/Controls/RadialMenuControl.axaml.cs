using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Input;
using System;
using System.Collections.Generic;
using Avalonia.Animation;

namespace FortniteVideoSoftware.App.Controls;

public partial class RadialMenuControl : UserControl
{
    private Canvas? _canvas;
    private Border? _container;
    private List<RadialMenuItem> _items = new();
    private int _selectedIndex = -1;
    public event Action<string>? ItemSelected;
    private Point _center;

    public RadialMenuControl()
    {
        InitializeComponent();
        _canvas = this.FindControl<Canvas>("SliceCanvas");
        _container = this.FindControl<Border>("Container");
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public void AddItem(string id, string text, IBrush bg)
    {
        var item = new RadialMenuItem { Id = id, Text = text, Background = bg };
        _items.Add(item);
    }

    public void Open(Point position)
    {
        if (_items.Count == 0 || _canvas == null || _container == null) return;
        _canvas.Children.Clear();
        _center = new Point(150, 150);
        
        double angleStep = Math.PI * 2 / _items.Count;
        double radius = 100;
        
        for (int i = 0; i < _items.Count; i++)
        {
            var border = new Border
            {
                Width = 80, Height = 40, CornerRadius = new CornerRadius(20),
                Background = _items[i].Background,
                BorderBrush = SolidColorBrush.Parse("#40FFFFFF"), BorderThickness = new Thickness(1),
                Child = new TextBlock { Text = _items[i].Text, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, FontWeight = Avalonia.Media.FontWeight.Bold, Foreground = SolidColorBrush.Parse("#FFF") }
            };
            
            double angle = i * angleStep - Math.PI / 2;
            double x = _center.X + Math.Cos(angle) * radius - 40;
            double y = _center.Y + Math.Sin(angle) * radius - 20;
            
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            
            _items[i].UiElement = border;
            _canvas.Children.Add(border);
        }

        Canvas.SetLeft(this, position.X - 150);
        Canvas.SetTop(this, position.Y - 150);
        IsVisible = true;
        IsHitTestVisible = true;
        Opacity = 1;
        _container.Classes.Remove("RadialMenuClosed");
        _container.Classes.Add("RadialMenuOpen");
    }

    public void UpdateHover(Point pointerPosRelativeToMenu)
    {
        if (_items.Count == 0) return;
        double dx = pointerPosRelativeToMenu.X - _center.X;
        double dy = pointerPosRelativeToMenu.Y - _center.Y;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        
        _selectedIndex = -1;
        if (distance > 30)
        {
            double angle = Math.Atan2(dy, dx) + Math.PI / 2;
            if (angle < 0) angle += Math.PI * 2;
            
            double angleStep = Math.PI * 2 / _items.Count;
            _selectedIndex = (int)Math.Round(angle / angleStep) % _items.Count;
        }
        
        for (int i = 0; i < _items.Count; i++)
        {
            if (_items[i].UiElement is Border b)
            {
                if (i == _selectedIndex)
                {
                    b.BorderBrush = SolidColorBrush.Parse("#FFFFFFFF");
                    b.BorderThickness = new Thickness(3);
                    b.RenderTransform = new ScaleTransform(1.1, 1.1);
                }
                else
                {
                    b.BorderBrush = SolidColorBrush.Parse("#40FFFFFF");
                    b.BorderThickness = new Thickness(1);
                    b.RenderTransform = new ScaleTransform(1.0, 1.0);
                }
            }
        }
    }

    public void Close()
    {
        IsHitTestVisible = false;
        if (_container != null)
        {
            _container.Classes.Remove("RadialMenuOpen");
            _container.Classes.Add("RadialMenuClosed");
        }
        if (_selectedIndex >= 0 && _selectedIndex < _items.Count)
        {
            ItemSelected?.Invoke(_items[_selectedIndex].Id);
        }
        _selectedIndex = -1;
        var timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!IsHitTestVisible)
            {
                Opacity = 0;
                IsVisible = false;
            }
        };
        timer.Start();
    }
}

public class RadialMenuItem
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public IBrush Background { get; set; } = Brushes.Gray;
    public Control? UiElement { get; set; }
}
