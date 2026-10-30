using CommunityToolkit.Mvvm.ComponentModel;

namespace SourceGit.ViewModels
{
    public class SelfUpdate : ObservableObject
    {
        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        public string Status
        {
            get => _status;
            set => SetProperty(ref _status, value);
        }

        public object Data
        {
            get => _data;
            set => SetProperty(ref _data, value);
        }

        private object _data = null;
        private bool _isBusy;
        private string _status;
    }
}
