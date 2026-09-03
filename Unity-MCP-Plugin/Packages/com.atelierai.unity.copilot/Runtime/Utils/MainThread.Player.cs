/*
┌──────────────────────────────────────────────────────────────────┐
│  Author: Ivan Murzak (https://github.com/IvanMurzak)             │
│  Repository: GitHub (https://github.com/IvanMurzak/Unity-MCP)    │
│  Copyright (c) 2025 Ivan Murzak                                  │
│  Licensed under the Apache License, Version 2.0.                 │
│  See the LICENSE file in the project root for more information.  │
└──────────────────────────────────────────────────────────────────┘
*/
#if !UNITY_EDITOR
#nullable enable
using System;
using System.Threading.Tasks;
using com.IvanMurzak.ReflectorNet.Utils;
using UnityEngine;

namespace com.AtelierAI.Unity.Copilot.Runtime.Utils
{
    public static class MainThreadInstaller
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Init() => MainThread.Instance = new UnityMainThread();
    }
    public class UnityMainThread : MainThread
    {
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
            // Flow the caller's execution context so AsyncLocal-backed scopes
            // survive the hop onto the main thread (see MainThread.Editor.cs).
            var context = System.Threading.ExecutionContext.Capture();
            MainThreadDispatcher.Enqueue(() =>
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
            });
            return tcs.Task;
        }
    }
}
#endif
