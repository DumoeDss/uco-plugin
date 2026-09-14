#nullable enable
using System;
using System.Linq;
using com.AtelierAI.Unity.Copilot;
using com.AtelierAI.Unity.Copilot.Runtime.Utils;
using com.AtelierAI.Unity.Copilot.Utils;
using NUnit.Framework;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Editor.Tests
{
    /// <summary>
    /// COCli-07 contract tests: correlation/operation attribution from the
    /// main-thread call scope, source classification, default Console
    /// separation for the plugin diagnostics channel, loss accounting, and
    /// filter combinations on the detailed query.
    /// </summary>
    public class ConsoleDiagnosticsTests
    {
        UnityLogCollector? _collector;

        [SetUp]
        public void SetUp()
        {
            // A unique file per test isolates each case from every other
            // collector in the suite (the storage file is process-global).
            _collector = new UnityLogCollector(
                new FileLogStorage(requestedFileName:
                    "test-console-diagnostics-" + Guid.NewGuid().ToString("N") + ".txt"));
            _collector.Clear();
            PluginDiagnostics.MirrorToConsole = false;
        }

        [TearDown]
        public void TearDown()
        {
            _collector?.Clear();
            _collector?.Dispose();
            _collector = null;
            PluginDiagnostics.MirrorToConsole = false;
            LogCallScope.SetOperationOverlay(null);
        }

        UnityLogCollector Collector => _collector
            ?? throw new InvalidOperationException("collector not initialized");

        [Test]
        public void OperationScopedAttribution_TagsProductLogsInsideTheWindow()
        {
            // Outside any window: retained unattributed as unity.
            Debug.Log("background entry");

            using (LogCallScope.Push(new LogCallScope.Frame
            {
                CallId = "call-attr-1",
                CorrelationId = "trace-attr-1",
            }))
            {
                LogCallScope.SetOperationOverlay("op-attr-1");
                // Product code logging inside the owned window.
                Debug.Log("product entry in window");
            }
            LogCallScope.SetOperationOverlay(null);

            Debug.Log("background entry after window");

            Collector.Save();
            var byCorrelation = Collector.QueryDetailed(correlationId: "trace-attr-1");
            Assert.That(byCorrelation.Entries, Has.Length.EqualTo(1));
            Assert.That(byCorrelation.Entries[0].Message, Does.Contain("product entry in window"));
            Assert.That(byCorrelation.Entries[0].Source, Is.EqualTo(LogEntry.SourceProduct));
            Assert.That(byCorrelation.Entries[0].CorrelationId, Is.EqualTo("trace-attr-1"));
            Assert.That(byCorrelation.Entries[0].OperationId, Is.EqualTo("op-attr-1"));

            var byOperation = Collector.QueryDetailed(operationId: "op-attr-1");
            Assert.That(byOperation.Entries, Has.Length.EqualTo(1));

            var unattributed = Collector.QueryDetailed(source: LogEntry.SourceUnity);
            Assert.That(unattributed.Entries, Has.Length.EqualTo(2));
            Assert.That(unattributed.Entries.All(entry =>
                string.IsNullOrEmpty(entry.CorrelationId) && string.IsNullOrEmpty(entry.OperationId)), Is.True);
        }

        [Test]
        public void PluginDiagnostics_ChannelIsCollectorOnlyAndSourceClassified()
        {
            // With the sink installed (collector construction), plugin-channel
            // entries land in the collector with their emission-site source
            // and never enter the Unity-forwarded stream.
            PluginDiagnostics.Append(LogEntry.SourceBridge, LogType.Warning, "bridge diagnostic");
            PluginDiagnostics.Append(LogEntry.SourceTool, LogType.Error, "tool diagnostic");

            Collector.Save();
            var bridge = Collector.QueryDetailed(source: LogEntry.SourceBridge);
            Assert.That(bridge.Entries, Has.Length.EqualTo(1));
            Assert.That(bridge.Entries[0].Message, Does.Contain("bridge diagnostic"));

            var tool = Collector.QueryDetailed(source: LogEntry.SourceTool);
            Assert.That(tool.Entries, Has.Length.EqualTo(1));
            Assert.That(tool.Entries[0].Message, Does.Contain("tool diagnostic"));

            // The unity source never saw the channel entries.
            var unity = Collector.QueryDetailed(source: LogEntry.SourceUnity);
            Assert.That(unity.Entries.All(entry =>
                !entry.Message.Contains("bridge diagnostic") && !entry.Message.Contains("tool diagnostic")), Is.True);
        }

        [Test]
        public void PluginDiagnostics_ChannelEntriesCarryTheActiveCallIdentity()
        {
            using (LogCallScope.Push(new LogCallScope.Frame
            {
                CallId = "call-diag-1",
                CorrelationId = "trace-diag-1",
            }))
            {
                PluginDiagnostics.Append(LogEntry.SourceTool, LogType.Log, "tool entry in window");
            }

            var attributed = Collector.QueryDetailed(correlationId: "trace-diag-1");
            Assert.That(attributed.Entries, Has.Length.EqualTo(1));
            Assert.That(attributed.Entries[0].Source, Is.EqualTo(LogEntry.SourceTool));
        }

        [Test]
        public void CapacityLoss_IsVisibleInTheQueryResponse()
        {
            // A message beyond the size limit is truncated and accounted.
            var huge = new string('x', 20 * 1024);
            Debug.Log(huge);
            Collector.Save();
            var truncated = Collector.QueryDetailed();
            Assert.That(truncated.Entries, Has.Length.EqualTo(1));
            Assert.That(truncated.Entries[0].Message.Length, Is.LessThanOrEqualTo(16 * 1024));
            Assert.That(truncated.TruncatedEntries, Is.GreaterThanOrEqualTo(1));

            // A failing sink marks the channel entry as dropped and the query
            // reports it so an empty result is distinguishable from a clean run.
            var failing = new UnityLogCollector(new ThrowingStorage());
            try
            {
                PluginDiagnostics.Append(LogEntry.SourceBridge, LogType.Log, "entry into failing sink");
                var failingResult = failing.QueryDetailed();
                Assert.That(failingResult.DroppedEntries, Is.GreaterThanOrEqualTo(1));
                Assert.That(failingResult.Entries, Is.Empty);
            }
            finally
            {
                // Restore-if-current disposal hands the channel back to this
                // test's collector sink — no manual reinstall needed, and the
                // plugin's own sink is never cleared for later suites.
                failing.Dispose();
            }
        }

        [Test]
        public void FilterCombinations_ComposeWithExistingNames()
        {
            Debug.Log("old-info");
            using (LogCallScope.Push(new LogCallScope.Frame
            {
                CallId = "call-filter-1",
                CorrelationId = "trace-filter-1",
            }))
            {
                Debug.LogWarning("window-warning");
                UnityEngine.TestTools.LogAssert.Expect(LogType.Error, "window-error");
                Debug.LogError("window-error");
            }

            Collector.Save();
            // severity + correlation
            var errors = Collector.QueryDetailed(logTypeFilter: LogType.Error, correlationId: "trace-filter-1");
            Assert.That(errors.Entries, Has.Length.EqualTo(1));
            Assert.That(errors.Entries[0].Message, Does.Contain("window-error"));

            // severity alone keeps the historical behavior (all entries of that type)
            var allWarnings = Collector.QueryDetailed(logTypeFilter: LogType.Warning);
            Assert.That(allWarnings.Entries, Has.Length.EqualTo(1));

            // time boundary: everything now is after epoch start
            var sinceEpoch = Collector.QueryDetailed(sinceUnixMs: 0);
            Assert.That(sinceEpoch.Entries.Length, Is.GreaterThanOrEqualTo(3));

            // time boundary in the future excludes everything
            var future = Collector.QueryDetailed(sinceUnixMs: DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeMilliseconds());
            Assert.That(future.Entries, Is.Empty);
            Assert.That(future.DroppedEntries, Is.EqualTo(0));

            // invalid source is rejected
            Assert.Throws<ArgumentException>(() => Collector.QueryDetailed(source: "unknown-source"));
        }

        [Test]
        public void LogEntry_ToStringIncludesAttribution()
        {
            var entry = new LogEntry(
                LogType.Error, "boom", LogEntry.SourceProduct,
                "trace-fmt", "op-fmt", DateTime.Now);
            var text = entry.ToString();
            Assert.That(text, Does.Contain("product"));
            Assert.That(text, Does.Contain("trace-fmt"));
            Assert.That(text, Does.Contain("op-fmt"));
        }

        sealed class ThrowingStorage : FileLogStorage
        {
            public ThrowingStorage() : base(requestedFileName: "test-throwing-storage.txt")
            {
            }

            public override void Append(params LogEntry[] entries)
                => throw new InvalidOperationException("storage failure for loss accounting test");
        }
    }
}
