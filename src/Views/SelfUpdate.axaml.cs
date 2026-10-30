using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;

namespace SourceGit.Views
{
    public class UpdateInfoView : TextEditor
    {
        protected override Type StyleKeyOverride => typeof(TextEditor);

        public UpdateInfoView() : base(new TextArea(), new TextDocument())
        {
            IsReadOnly = true;
            ShowLineNumbers = false;
            WordWrap = true;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            TextArea.TextView.Margin = new Thickness(4, 0);
            TextArea.TextView.Options.EnableHyperlinks = false;
            TextArea.TextView.Options.EnableEmailHyperlinks = false;
            TextArea.TextView.Options.AllowScrollBelowDocument = false;
        }

        protected override void OnLoaded(RoutedEventArgs e)
        {
            base.OnLoaded(e);

            if (_textMate == null)
            {
                _textMate = Models.TextMateHelper.CreateForEditor(this);
                Models.TextMateHelper.SetGrammarByFileName(_textMate, "README.md");
            }
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);

            if (_textMate != null)
            {
                _textMate.Dispose();
                _textMate = null;
            }

            GC.Collect();
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);

            if (DataContext is Models.Version ver)
                Text = ver.ReleaseHistoryUnavailable
                    ? $"{App.Text("SelfUpdate.ReleaseHistoryUnavailable")}\n\n{ver.Body}"
                    : ver.ReleaseNotes ?? ver.Body;
        }

        private TextMate.Installation _textMate = null;
    }

    public partial class SelfUpdate : ChromelessWindow
    {
        public SelfUpdate()
        {
            CloseOnESC = true;
            InitializeComponent();
        }

        private void CloseWindow(object _1, RoutedEventArgs _2)
        {
            Close();
        }

        private void OpenReleaseInBrowser(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Models.Version ver })
                Native.OS.OpenBrowser(ver.ReleaseUrl);
            e.Handled = true;
        }

        private async void DownloadAndInstall(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is not Button { DataContext: Models.Version ver } || DataContext is not ViewModels.SelfUpdate vm || !_installGate.Wait(0))
                return;

            vm.IsBusy = true;
            _downloadCancellation = new CancellationTokenSource();
            var cancellationToken = _downloadCancellation.Token;
            try
            {
                vm.Status = App.Text("SelfUpdate.Downloading");
                using var installer = new Models.UpdateInstaller();
                var progress = new Progress<double>(percent =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                        vm.Status = $"{App.Text("SelfUpdate.Downloading")} {percent:F0}%";
                });
                await installer.PrepareAsync(ver.WindowsPackage, progress, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                vm.Status = App.Text("SelfUpdate.Verified");

                var confirm = new Confirm();
                confirm.SetData($"{App.Text("SelfUpdate.ConfirmInstall")}\n\n{System.IO.Path.GetDirectoryName(Environment.ProcessPath)}", Models.ConfirmButtonType.OkCancel);
                if (await confirm.ShowDialog<bool>(this) && !cancellationToken.IsCancellationRequested)
                {
                    await installer.InstallAndRestartAsync();
                    App.Quit(0);
                }
            }
            catch (OperationCanceledException)
            {
                vm.Status = App.Text("SelfUpdate.Cancelled");
            }
            catch (Exception ex)
            {
                vm.Status = $"{App.Text("SelfUpdate.InstallFailed")} {ex.Message}";
            }
            finally
            {
                _downloadCancellation.Dispose();
                _downloadCancellation = null;
                vm.IsBusy = false;
                _installGate.Release();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _downloadCancellation?.Cancel();
            base.OnClosed(e);
        }

        private void IgnoreThisVersion(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: Models.Version ver })
                ViewModels.Preferences.Instance.IgnoreUpdateTag = ver.TagName;

            Close();
            e.Handled = true;
        }

        private CancellationTokenSource _downloadCancellation;
        private static readonly SemaphoreSlim _installGate = new(1, 1);
    }
}
