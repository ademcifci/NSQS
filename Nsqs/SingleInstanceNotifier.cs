using System;
using System.Threading;
using System.Windows.Threading;

namespace Nsqs
{
    public sealed class SingleInstanceNotifier : IDisposable
    {
        private const string ActivateEventName = "NSQS-Activate-7C4A9E2F-1B3D-4F8A-9D6E-2A5B8C1D0E3F";

        private readonly EventWaitHandle _activateEvent;
        private readonly Thread _listenerThread;
        private readonly CancellationTokenSource _cts = new();
        private readonly Dispatcher _dispatcher;
        private readonly Action _onActivate;

        private SingleInstanceNotifier(Dispatcher dispatcher, Action onActivate)
        {
            _dispatcher = dispatcher;
            _onActivate = onActivate;
            _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            _listenerThread = new Thread(ListenLoop)
            {
                IsBackground = true,
                Name = "NSQS-SingleInstance"
            };
            _listenerThread.Start();
        }

        public static bool TrySignalExistingInstance()
        {
            try
            {
                using var existing = EventWaitHandle.OpenExisting(ActivateEventName);
                existing.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
        }

        public static SingleInstanceNotifier StartListening(Dispatcher dispatcher, Action onActivate) =>
            new(dispatcher, onActivate);

        private void ListenLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (!_activateEvent.WaitOne(500))
                        continue;

                    _dispatcher.BeginInvoke(_onActivate);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _activateEvent.Dispose();
            _cts.Dispose();
        }
    }
}
