using System;
using PS7ScriptDesk.Shell.Debug;
using Xunit;

namespace PS7ScriptDesk.Tests
{
    public sealed class DebuggerInspectionTests
    {
        [Fact]
        public void FrameIdentityIsStableWithinPauseAndChangesAcrossGeneration()
        {
            var sessionId = Guid.NewGuid();
            var first = DebugFrameIdentity.Create(sessionId, 4, 0, "Outer", "C:\\scripts\\demo.ps1", 12, "Outer", true);
            var same = DebugFrameIdentity.Create(sessionId, 4, 0, "Outer", "C:\\scripts\\demo.ps1", 12, "Outer", true);
            var nextPause = DebugFrameIdentity.Create(sessionId, 5, 0, "Outer", "C:\\scripts\\demo.ps1", 12, "Outer", true);

            Assert.Equal(first.FrameId, same.FrameId);
            Assert.NotEqual(first.FrameId, nextPause.FrameId);
            Assert.True(first.IsCurrentFrame);
            Assert.True(first.IsSelectedInspectionFrame);
        }

        [Fact]
        public void StaleInspectionResultsAreRejectedBySessionOrPauseGeneration()
        {
            var sessionId = Guid.NewGuid();

            Assert.True(DebugInspectionResultGuard.IsCurrent(sessionId, 2, sessionId, 2));
            Assert.False(DebugInspectionResultGuard.IsCurrent(sessionId, 2, sessionId, 1));
            Assert.False(DebugInspectionResultGuard.IsCurrent(sessionId, 2, Guid.NewGuid(), 2));
            Assert.False(DebugInspectionResultGuard.IsCurrent(sessionId, 2, sessionId, 2, "old", "new"));
        }

        [Fact]
        public void VariablesAreExplicitlyReadOnlyCurrentScopeMetadata()
        {
            var variable = new DebugVariableInfo("items", "Object[]", "Collection Count=3 Type=Object[]")
            {
                SessionId = Guid.NewGuid(),
                PauseGeneration = 7,
                Scope = "Current",
                FrameId = "session/7/0",
                HasChildren = true,
                IsExpandable = false,
                IsTruncated = false,
                LoadState = "Loaded"
            };

            Assert.Equal("Current", variable.Scope);
            Assert.True(variable.HasChildren);
            Assert.False(variable.IsExpandable);
            Assert.Equal(variable.Value, variable.DisplayValue);
        }
    }
}
