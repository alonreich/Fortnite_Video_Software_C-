#nullable disable
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System;

namespace FortniteVideoSoftware.App
{
    public partial class DeploymentProgressWindow : Window
    {
        private ProgressBar _progressBar;
        private TextBlock _phaseText;
        private TextBlock _percentageText;
        private TextBlock _titleText;
        private TextBlock _logPathText;
        private TextBox _logTextBox;
        private Button _finishButton;

        public DeploymentProgressWindow()
        {
            InitializeComponent();
        }

        public DeploymentProgressWindow(string title) : this(title, null)
        {
        }

        public DeploymentProgressWindow(string title, string logPath) : this()
        {
            if (_titleText != null)
            {
                _titleText.Text = string.IsNullOrWhiteSpace(title) ? "Installing Fortnite Video Software" : title;
            }

            if (_logPathText != null)
            {
                _logPathText.Text = string.IsNullOrWhiteSpace(logPath) ? string.Empty : "Log: " + logPath;
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
            _progressBar = this.FindControl<ProgressBar>("ProgressBar");
            _phaseText = this.FindControl<TextBlock>("PhaseText");
            _percentageText = this.FindControl<TextBlock>("PercentageText");
            _titleText = this.FindControl<TextBlock>("TitleText");
            _logPathText = this.FindControl<TextBlock>("LogPathText");
            _logTextBox = this.FindControl<TextBox>("LogTextBox");
            _finishButton = this.FindControl<Button>("FinishButton");
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            Activate();
            Topmost = true;
        }

        public void UpdateProgress(int value)
        {
            int clamped = Math.Clamp(value, 0, 100);
            Dispatcher.UIThread.Post(() =>
            {
                if (_progressBar != null)
                {
                    _progressBar.Value = clamped;
                    if (clamped >= 100)
                    {
                        _progressBar.IsVisible = false;
                        if (_finishButton != null) _finishButton.IsVisible = true;
                    }
                }

                if (_percentageText != null)
                {
                    _percentageText.Text = clamped + "%";
                }
            });
        }

        private void OnFinishClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            Close();
        }

        public void UpdateStatus(string status)
        {
            if (string.IsNullOrWhiteSpace(status))
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_phaseText != null)
                {
                    _phaseText.Text = status;
                }

                AppendLogLine(status);
                Activate();
            });
        }

        public void AppendLogLine(string message)
        {
            if (_logTextBox == null || string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine;
            _logTextBox.Text += line;
            _logTextBox.CaretIndex = _logTextBox.Text.Length;
        }
        public async Task ShowSuccessAndCloseAsync()
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_phaseText != null)
                {
                    _phaseText.Text = "FINISHED SUCCESSFULLY";
                    _phaseText.Foreground = Avalonia.Media.Brushes.LimeGreen;
                }
                if (_progressBar != null)
                {
                    _progressBar.Value = 100;
                    _progressBar.Foreground = Avalonia.Media.Brushes.LimeGreen;
                }
                if (_titleText != null)
                {
                    _titleText.Foreground = Avalonia.Media.Brushes.LimeGreen;
                }
            });

            await Task.Delay(1000); // Wait 1 second before closing
            Dispatcher.UIThread.Post(() => Close());
        }

        public void ShowFailureAndWait(string reason)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_phaseText != null)
                {
                    _phaseText.Text = "INSTALLATION FAILED";
                    _phaseText.Foreground = Avalonia.Media.Brushes.Red;
                }
                if (_progressBar != null)
                {
                    _progressBar.Foreground = Avalonia.Media.Brushes.Red;
                    _progressBar.Value = 100;
                }
                if (_titleText != null)
                {
                    _titleText.Foreground = Avalonia.Media.Brushes.Red;
                }
                
                AppendLogLine("========================================");
                AppendLogLine("FAILURE REASON:");
                AppendLogLine(reason);
                AppendLogLine("========================================");
                
                if (_finishButton != null) 
                {
                    _finishButton.Content = "Close";
                    _finishButton.Background = Avalonia.Media.Brushes.DarkRed;
                    _finishButton.IsVisible = true;
                }
            });
        }
    }
}
