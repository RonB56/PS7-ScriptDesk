using System;
using System.Collections.Generic;

namespace PS7ScriptDesk.Application.Interfaces
{
    public interface IUiScaleService
    {
        IReadOnlyList<int> SupportedPercentages { get; }

        int CurrentPercentage { get; }

        double CurrentFactor { get; }

        event EventHandler? ScaleChanged;

        void SetPercentage(int percentage, string source = "User");

        void Increase(string source = "User");

        void Decrease(string source = "User");

        void Reset(string source = "User");

        int NormalizePersistedPercentage(int? percentage);
    }
}
