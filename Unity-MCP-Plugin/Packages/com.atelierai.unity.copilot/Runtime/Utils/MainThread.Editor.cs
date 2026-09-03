/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/
#if UNITY_EDITOR
#nullable enable
using System;
using System.Threading.Tasks;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEditor;

namespace com.AtelierAI.Unity.Copilot.Runtime.Utils
{
    public static class MainThreadInstaller
    {
        [InitializeOnLoadMethod]
        public static void Init()
        {
            MainThread.Instance = new UnityMainThread();
            UnityMainThread.InstallPump();
        }
    }
    public class UnityMainThread : MainThread
    {
        // Background threads must never mutate the static EditorApplication.update
        // delegate: a `+=` racing the previous hop's `-=` on the main thread can
        // drop the registration and leave the caller waiting forever. Work is
        // queued instead and drained by one permanently registered pump.
        private static readonly System.Collections.Concurrent.ConcurrentQueue<Action> Queue =
            new System.Collections.Concurrent.ConcurrentQueue<Action>();
        private static int _pumpInstalled;

        internal static void InstallPump()
        {
            if (System.Threading.Interlocked.Exchange(ref _pumpInstalled, 1) != 0)
                return;
            EditorApplication.update += Pump;
        }

        private static void Pump()
        {
            // Drain everything queued so far; work enqueued while draining runs
            // on the next tick, which keeps one hop per frame predictable.
            var count = Queue.Count;
            while (count-- > 0 && Queue.TryDequeue(out var work))
                work();
        }

        public override bool IsMainThread => MainThreadDispatcher.IsMainThread;

        public override Task RunAsync(Task task)
            => MainThreadDispatcher.IsMainThread ? task : Dispatch(() => { task.Wait(); return true; });

        public override Task<T> RunAsync<T>(Task<T> task)
            => MainThreadDispatcher.IsMainThread ? task : Dispatch(() => task.Result);

        public override Task<T> RunAsync<T>(Func<T> func)
            => MainThreadDispatcher.IsMainThread ? Task.FromResult(func()) : Dispatch(func);

        public override Task RunAsync(Action action)
        {
            if (MainThreadDispatcher.IsMainThread)
            {
                action();
                return Task.CompletedTask;
            }
            return Dispatch(() => { action(); return true; });
        }

        static Task<T> Dispatch<T>(Func<T> body)
        {
            var tcs = new TaskCompletionSource<T>();
            // EditorApplication.update runs delegates with the main thread's
            // own execution context. Capture the caller's context so
            // AsyncLocal-backed scopes (authoring transaction, invocation
            // scope, project path policy) remain visible inside the body
            // exactly as they would across an ordinary await.
            var context = System.Threading.ExecutionContext.Capture();

            void Execute()
            {
                try
                {
                    if (context == null)
                        tcs.SetResult(body());
                    else
                    {
                        T result = default!;
                        System.Threading.ExecutionContext.Run(context, _ => result = body(), null);
                        tcs.SetResult(result);
                    }
                }
                catch (Exception ex) { tcs.SetException(ex); }
            }

            InstallPump();
            Queue.Enqueue(Execute);
            return tcs.Task;
        }
    }
}
#endif
