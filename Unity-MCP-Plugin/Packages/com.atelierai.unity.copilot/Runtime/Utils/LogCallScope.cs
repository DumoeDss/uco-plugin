/*
┌──────────────────────────────────────────────────────────────────┐
│  Call-identity scope read at log emission (COCli-07).             │
└──────────────────────────────────────────────────────────────────┘
*/

#nullable enable
using System;
using System.Threading;
using com.IvanMurzak.ReflectorNet.Utils;

namespace com.AtelierAI.Unity.Copilot.Runtime.Utils
{
    /// <summary>
    /// The tool-call identity of the active serialized-lane execution. The
    /// Editor execution scheduler publishes the frame when a serialized
    /// (main-thread) lease is granted and withdraws it on release; the log
    /// collector reads <see cref="Current"/> at emission so entries produced
    /// on the main thread inside an owned execution window carry that call's
    /// correlation and operation ids. Parallel background read leases never
    /// publish a frame, and threaded product logs keep their unattributed
    /// defaults — a documented limitation, not a heuristic.
    /// </summary>
    public static class LogCallScope
    {
        static readonly object Gate = new();

        static Frame? s_serializedFrame;
        static string? s_operationOverlay;

        /// <summary>Active serialized-lane frame; null when none or not on the main thread.</summary>
        public static Frame? Current
        {
            get
            {
                if (!MainThread.Instance.IsMainThread) return null;
                var frame = Volatile.Read(ref s_serializedFrame);
                if (frame == null) return null;
                var overlay = Volatile.Read(ref s_operationOverlay);
                return overlay == null
                    ? frame
                    : new Frame
                    {
                        CallId = frame.CallId,
                        CorrelationId = frame.CorrelationId,
                        OperationId = overlay,
                    };
            }
        }

        /// <summary>
        /// Overlay the durable operation id onto the active frame (set by a
        /// tool that just created its operation inside the execution window).
        /// Cleared with an empty argument.
        /// </summary>
        public static void SetOperationOverlay(string? operationId)
            => Volatile.Write(ref s_operationOverlay, string.IsNullOrEmpty(operationId) ? null : operationId);

        /// <summary>
        /// Publish a serialized-lane frame and return the withdraw handle.
        /// The serialized lane is exclusive, so at most one frame is active.
        /// </summary>
        public static IDisposable Push(Frame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            lock (Gate)
            {
                s_serializedFrame = frame;
            }
            return new Withdraw(frame);
        }

        public sealed class Frame
        {
            public string? CallId { get; set; }
            public string? CorrelationId { get; set; }
            public string? OperationId { get; set; }
        }

        sealed class Withdraw : IDisposable
        {
            readonly Frame _frame;
            int _disposed;

            public Withdraw(Frame frame) => _frame = frame;

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
                lock (Gate)
                {
                    // A mis-ordered withdraw can never clear a newer call's frame.
                    if (ReferenceEquals(s_serializedFrame, _frame))
                        s_serializedFrame = null;
                }
            }
        }
    }
}
