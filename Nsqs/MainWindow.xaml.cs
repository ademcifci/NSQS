using System;
using System.Windows;
using System.Windows.Interop;

namespace Nsqs
{
    public partial class MainWindow : Window
    {
        private bool _suppressActivate;

        public MainWindow()
        {
            InitializeComponent();
            Icon = IconFactory.CreateWindowIconSource();
            Title = App.ProductName;
            ShowInTaskbar = true;
            ShowActivated = false;
            WindowState = WindowState.Minimized;
        }

        public event Action? ActivateRequested;

        public void PrepareForTaskbar()
        {
            _suppressActivate = true;
            try
            {
                if (!IsVisible)
                    Show();

                WindowState = WindowState.Minimized;
            }
            finally
            {
                _suppressActivate = false;
            }
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);

            if (_suppressActivate)
                return;

            ActivateRequested?.Invoke();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            DarkTitleBar.Apply(hwnd, ThemeHelper.IsSystemDarkTheme());
        }
    }
}
