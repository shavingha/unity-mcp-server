using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Nurture.MCP.Editor
{
    public static class TaskExtensions
    {
        public static async Task<T> Run<T>(
            this SynchronizationContext context,
            Func<Task<T>> action,
            CancellationToken cancellationToken = default
        )
        {
            Debug.Log("[MCP TaskExtensions.Run<T>] Starting async Run");
            TaskCompletionSource<T> tcs1 = new TaskCompletionSource<T>();

            context.Post(
                _ =>
                {
                    Debug.Log("[MCP TaskExtensions.Run<T>] Inside Post callback");
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Debug.Log("[MCP TaskExtensions.Run<T>] Cancellation requested");
                        tcs1.TrySetCanceled();
                    }
                    else
                    {
                        Debug.Log("[MCP TaskExtensions.Run<T>] Starting action execution");
                        // 不使用 async void，而是显式处理 Task
                        ExecuteActionAsync(action, tcs1);
                    }
                },
                null
            );

            Debug.Log("[MCP TaskExtensions.Run<T>] Waiting for tcs1.Task");
            var result = await tcs1.Task;
            Debug.Log("[MCP TaskExtensions.Run<T>] tcs1.Task completed");
            return result;
        }

        private static async void ExecuteActionAsync<T>(Func<Task<T>> action, TaskCompletionSource<T> tcs)
        {
            try
            {
                Debug.Log("[MCP ExecuteActionAsync] Starting action");
                var result = await action();
                Debug.Log("[MCP ExecuteActionAsync] Action completed, setting result");
                tcs.TrySetResult(result);
                Debug.Log("[MCP ExecuteActionAsync] Result set");
            }
            catch (Exception e)
            {
                Debug.Log($"[MCP ExecuteActionAsync] Exception: {e.Message}");
                Debug.LogException(e);
                tcs.TrySetException(e);
            }
        }

        public static async Task<T> Run<T>(
            this SynchronizationContext context,
            Func<T> action,
            CancellationToken cancellationToken = default
        )
        {
            Debug.Log("[MCP TaskExtensions.Run<T> SYNC] Starting");
            TaskCompletionSource<T> tcs1 = new TaskCompletionSource<T>();

            context.Post(
                _ =>
                {
                    Debug.Log("[MCP TaskExtensions.Run<T> SYNC] Inside Post callback");
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Debug.Log("[MCP TaskExtensions.Run<T> SYNC] Cancelled");
                        tcs1.TrySetCanceled();
                    }
                    else
                    {
                        try
                        {
                            Debug.Log("[MCP TaskExtensions.Run<T> SYNC] Executing action");
                            var result = action();
                            Debug.Log("[MCP TaskExtensions.Run<T> SYNC] Action completed, setting result");
                            tcs1.TrySetResult(result);
                            Debug.Log("[MCP TaskExtensions.Run<T> SYNC] Result set");
                        }
                        catch (Exception e)
                        {
                            Debug.Log($"[MCP TaskExtensions.Run<T> SYNC] Exception: {e.Message}");
                            Debug.LogException(e);
                            tcs1.TrySetException(e);
                        }
                    }
                },
                null
            );

            Debug.Log("[MCP TaskExtensions.Run<T> SYNC] Waiting for tcs1.Task");
            var finalResult = await tcs1.Task;
            Debug.Log("[MCP TaskExtensions.Run<T> SYNC] tcs1.Task completed, returning");
            return finalResult;
        }
    }
}
