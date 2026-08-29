using System;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Application.Services
{
    public static class UiScaleServiceHost
    {
        private static IUiScaleService _current = new UiScaleService();

        public static IUiScaleService Current => _current;

        public static event EventHandler? CurrentChanged;

        public static void SetCurrent(IUiScaleService service)
        {
            ArgumentNullException.ThrowIfNull(service);

            if (ReferenceEquals(_current, service))
            {
                return;
            }

            _current = service;
            CurrentChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
