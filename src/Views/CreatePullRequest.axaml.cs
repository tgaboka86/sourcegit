using System;

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SourceGit.Views
{
    public partial class CreatePullRequest : ChromelessWindow
    {
        public CreatePullRequest()
        {
            InitializeComponent();
            CloseOnESC = true;
        }

        protected override async void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            if (DataContext is ViewModels.CreatePullRequest model)
                await model.InitializeAsync();
        }

        protected override void OnClosed(EventArgs e)
        {
            (DataContext as ViewModels.CreatePullRequest)?.Dispose();
            base.OnClosed(e);
        }

        private async void OnSignIn(object sender, RoutedEventArgs e) { if (DataContext is ViewModels.CreatePullRequest model) await model.SignInAsync(); }
        private async void OnSignOut(object sender, RoutedEventArgs e) { if (DataContext is ViewModels.CreatePullRequest model) await model.SignOutAsync(); }
        private async void OnCreate(object sender, RoutedEventArgs e) { if (DataContext is ViewModels.CreatePullRequest model) await model.SubmitAsync(); }
        private async void OnRefresh(object sender, RoutedEventArgs e) { if (DataContext is ViewModels.CreatePullRequest model) await model.RefreshAsync(); }
        private void OnAddReviewer(object sender, RoutedEventArgs e) { (DataContext as ViewModels.CreatePullRequest)?.AddReviewer(); }
        private void OnRemoveReviewer(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: ViewModels.PullRequestReviewer reviewer })
                (DataContext as ViewModels.CreatePullRequest)?.RemoveReviewer(reviewer);
        }
        private void OnOpenResult(object sender, RoutedEventArgs e) { (DataContext as ViewModels.CreatePullRequest)?.OpenResult(); }
        private void OnOpenRepository(object sender, RoutedEventArgs e) { (DataContext as ViewModels.CreatePullRequest)?.OpenRepository(); }
        private void OnClearRecovery(object sender, RoutedEventArgs e) { (DataContext as ViewModels.CreatePullRequest)?.ClearRecovery(); }
        private void OnClose(object sender, RoutedEventArgs e) { Close(); }
    }
}
