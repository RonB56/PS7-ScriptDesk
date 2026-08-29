using System;
using System.Collections.Generic;
using System.Linq;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Application.Services
{
    public sealed class UiScaleService : IUiScaleService
    {
        public const int DefaultPercentage = 100;

        private static readonly int[] SupportedScalePercentages =
        {
            75, 80, 90, 100, 110, 125, 150, 175, 200
        };

        private int _currentPercentage;

        public UiScaleService(int? persistedPercentage = null)
        {
            _currentPercentage = NormalizePersistedPercentage(persistedPercentage);
        }

        public IReadOnlyList<int> SupportedPercentages => SupportedScalePercentages;

        public int CurrentPercentage => _currentPercentage;

        public double CurrentFactor => _currentPercentage / 100.0;

        public event EventHandler? ScaleChanged;

        public void SetPercentage(int percentage, string source = "User")
        {
            if (!SupportedScalePercentages.Contains(percentage))
            {
                DeveloperDiagnostics.LogWarning(
                    "UI",
                    "Rejected unsupported UI Scale percentage; retaining the current value.",
                    new Dictionary<string, object?>
                    {
                        ["requestedPercentage"] = percentage,
                        ["currentPercentage"] = _currentPercentage,
                        ["source"] = source
                    });
                return;
            }

            if (_currentPercentage == percentage)
            {
                return;
            }

            var previousPercentage = _currentPercentage;
            _currentPercentage = percentage;
            DeveloperDiagnostics.LogInfo(
                "UI",
                "UI Scale changed.",
                new Dictionary<string, object?>
                {
                    ["previousPercentage"] = previousPercentage,
                    ["percentage"] = percentage,
                    ["source"] = source
                });
            ScaleChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Increase(string source = "User")
        {
            var next = SupportedScalePercentages.FirstOrDefault(value => value > _currentPercentage);
            if (next > 0)
            {
                SetPercentage(next, source);
            }
        }

        public void Decrease(string source = "User")
        {
            var previous = SupportedScalePercentages.LastOrDefault(value => value < _currentPercentage);
            if (previous > 0)
            {
                SetPercentage(previous, source);
            }
        }

        public void Reset(string source = "User") => SetPercentage(DefaultPercentage, source);

        public int NormalizePersistedPercentage(int? percentage)
        {
            if (percentage.HasValue && SupportedScalePercentages.Contains(percentage.Value))
            {
                return percentage.Value;
            }

            if (percentage.HasValue)
            {
                DeveloperDiagnostics.LogWarning(
                    "Settings",
                    "Invalid persisted UI Scale was normalized to the default percentage.",
                    new Dictionary<string, object?>
                    {
                        ["persistedPercentage"] = percentage.Value,
                        ["defaultPercentage"] = DefaultPercentage
                    });
            }

            return DefaultPercentage;
        }
    }
}
