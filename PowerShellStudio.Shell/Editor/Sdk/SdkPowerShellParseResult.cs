using System;
using System.Collections.Generic;
using System.Management.Automation.Language;

namespace PowerShellStudio.Shell.Editor.Sdk
{
    public sealed class SdkPowerShellParseResult
    {
        private SdkPowerShellParseResult(
            string operationName,
            bool succeeded,
            TimeSpan duration,
            IReadOnlyList<SdkPowerShellParseError> errors,
            IReadOnlyList<SdkPowerShellTokenInfo> tokens,
            Exception? exception,
            Ast? ast)
        {
            OperationName = string.IsNullOrWhiteSpace(operationName) ? "UnnamedOperation" : operationName.Trim();
            Succeeded = succeeded;
            Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
            Errors = errors ?? Array.Empty<SdkPowerShellParseError>();
            Tokens = tokens ?? Array.Empty<SdkPowerShellTokenInfo>();
            Exception = exception;
            Ast = ast;
        }

        public string OperationName { get; }

        public bool Succeeded { get; }

        public TimeSpan Duration { get; }

        public IReadOnlyList<SdkPowerShellParseError> Errors { get; }

        public IReadOnlyList<SdkPowerShellTokenInfo> Tokens { get; }

        public bool HasErrors => Errors.Count > 0;

        public int ErrorCount => Errors.Count;

        public int TokenCount => Tokens.Count;

        public Exception? Exception { get; }

        public Ast? Ast { get; }

        public static SdkPowerShellParseResult Success(
            string operationName,
            TimeSpan duration,
            IReadOnlyList<SdkPowerShellParseError>? errors,
            IReadOnlyList<SdkPowerShellTokenInfo>? tokens,
            Ast? ast)
        {
            return new SdkPowerShellParseResult(
                operationName: operationName,
                succeeded: true,
                duration: duration,
                errors: errors?.ToArray() ?? Array.Empty<SdkPowerShellParseError>(),
                tokens: tokens?.ToArray() ?? Array.Empty<SdkPowerShellTokenInfo>(),
                exception: null,
                ast: ast);
        }

        public static SdkPowerShellParseResult Failure(
            string operationName,
            TimeSpan duration,
            Exception exception,
            IReadOnlyList<SdkPowerShellParseError>? errors = null,
            IReadOnlyList<SdkPowerShellTokenInfo>? tokens = null,
            Ast? ast = null)
        {
            ArgumentNullException.ThrowIfNull(exception);

            return new SdkPowerShellParseResult(
                operationName: operationName,
                succeeded: false,
                duration: duration,
                errors: errors?.ToArray() ?? Array.Empty<SdkPowerShellParseError>(),
                tokens: tokens?.ToArray() ?? Array.Empty<SdkPowerShellTokenInfo>(),
                exception: exception,
                ast: ast);
        }
    }
}
