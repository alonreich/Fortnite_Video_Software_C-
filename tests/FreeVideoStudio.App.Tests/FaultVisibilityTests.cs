
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FreeVideoStudio.App.Abstractions;
using FreeVideoStudio.App.Controls;
using FreeVideoStudio.App.Services;
using FreeVideoStudio.Core.Abstractions;
using Xunit;

namespace FreeVideoStudio.App.Tests;

/// <summary>LOGVIS_01 — a Recoverable fault leaves a production breadcrumb, once per call site per 30s.</summary>
public sealed class FaultVisibilityTests
{
    private sealed class SilentNotifier : IUserNotifier
    {
        public void Notify(string text, NoticeKind kind = NoticeKind.Info) { }
        public void Alert(string title, string message) { }
        public Task<bool> ConfirmAsync(string title, string message, string confirmText, string cancelText) => Task.FromResult(true);
    }

    [Fact]
    public void RecoverableFaultIsLoggedOutsideDevModeAndThrottledPerCallSite()
    {
        Assert.False(RuntimeLog.IsDevMode);

        var lines = new List<string>();
        void Capture(string l) { lock (lines) lines.Add(l); }
        RuntimeLog.LogAppended += Capture;
        try
        {
            var sink = new UserFacingFaultSink(new SilentNotifier());
            string site = $"Probe.cs:{Guid.NewGuid():N} Member()";
            for (int i = 0; i < 5; i++)
                sink.Report(new Fault(FaultTier.Recoverable, "TEST", string.Empty, $"{site} — IOException: boom {i}", null));

            List<string> hits;
            lock (lines) hits = lines.Where(l => l.Contains("[RECOVERABLE]") && l.Contains(site)).ToList();
            Assert.Single(hits);
            Assert.Contains(FaultCounters.Describe().Split('\n'), l => l.StartsWith("Recoverable/TEST"));
        }
        finally
        {
            RuntimeLog.LogAppended -= Capture;
        }
    }
}
