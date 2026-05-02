using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;

namespace PowerShellStudio.Shell.Editor.Sdk
{
    public sealed class SdkPowerShellTaskResult
    {
        private SdkPowerShellTaskResult(
            bool succeeded,
            bool timedOut,
            bool canceled,
            string operationName,
            TimeSpan duration,
            IReadOnlyList<PSObject> output,
            IReadOnlyList<string> errors,
            Exception? exception)
        {
            Succeeded = succeeded;
            TimedOut = timedOut;
            Canceled = canceled;
            OperationName = string.IsNullOrWhiteSpace(operationName) ? "UnnamedOperation" : operationName.Trim();
            Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
            Output = output ?? Array.Empty<PSObject>();
            Errors = errors ?? Array.Empty<string>();
            Exception = exception;
        }

        public bool Succeeded { get; }

        public bool TimedOut { get; }

        public bool Canceled { get; }

        public string OperationName { get; }

        public TimeSpan Duration { get; }

        public IReadOnlyList<PSObject> Output { get; }

        public IReadOnlyList<string> Errors { get; }

        public Exception? Exception { get; }

        public int OutputCount => Output.Count;

        public int ErrorCount => Errors.Count;

        public static SdkPowerShellTaskResult Success(
            string operationName,
            TimeSpan duration,
            IReadOnlyList<PSObject>? output,
            IReadOnlyList<string>? errors = null)
        {
            var safeOutput = output?.ToArray() ?? Array.Empty<PSObject>();
            var safeErrors = errors?.ToArray() ?? Array.Empty<string>();
            return new SdkPowerShellTaskResult(
                succeeded: safeErrors.Length == 0,
                timedOut: false,
                canceled: false,
                operationName: operationName,
                duration: duration,
                output: safeOutput,
                errors: safeErrors,
                exception: null);
        }

        public static SdkPowerShellTaskResult Failure(
            string operationName,
            TimeSpan duration,
            IReadOnlyList<PSObject>? output,
            IReadOnlyList<string>? errors,
            Exception? exception = null)
        {
            return new SdkPowerShellTaskResult(
                succeeded: false,
                timedOut: false,
                canceled: false,
                operationName: operationName,
                duration: duration,
                output: output?.ToArray() ?? Array.Empty<PSObject>(),
                errors: errors?.ToArray() ?? Array.Empty<string>(),
                exception: exception);
        }

        public static SdkPowerShellTaskResult Timeout(
            string operationName,
            TimeSpan duration,
            IReadOnlyList<PSObject>? output,
            IReadOnlyList<string>? errors,
            Exception? exception = null)
        {
            return new SdkPowerShellTaskResult(
                succeeded: false,
                timedOut: true,
                canceled: false,
                operationName: operationName,
                duration: duration,
                output: output?.ToArray() ?? Array.Empty<PSObject>(),
                errors: errors?.ToArray() ?? Array.Empty<string>(),
                exception: exception);
        }

        public static SdkPowerShellTaskResult FromCanceled(
            string operationName,
            TimeSpan duration,
            IReadOnlyList<PSObject>? output,
            IReadOnlyList<string>? errors,
            Exception? exception = null)
        {
            return new SdkPowerShellTaskResult(
                succeeded: false,
                timedOut: false,
                canceled: true,
                operationName: operationName,
                duration: duration,
                output: output?.ToArray() ?? Array.Empty<PSObject>(),
                errors: errors?.ToArray() ?? Array.Empty<string>(),
                exception: exception);
        }
    }
}
