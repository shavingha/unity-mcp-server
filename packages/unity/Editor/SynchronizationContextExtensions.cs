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
            TaskCompletionSource<T> tcs1 = new TaskCompletionSource<T>();

            context.Post(
                _ =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs1.TrySetCanceled();
                    }
                    else
                    {
                        ExecuteActionAsync(action, tcs1);
                    }
                },
                null
            );

            return await tcs1.Task;
        }

        private static async void ExecuteActionAsync<T>(Func<Task<T>> action, TaskCompletionSource<T> tcs)
        {
            try
            {
                var result = await action();
                tcs.TrySetResult(result);
            }
            catch (Exception e)
            {
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
            TaskCompletionSource<T> tcs1 = new TaskCompletionSource<T>();

            context.Post(
                _ =>
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        tcs1.TrySetCanceled();
                    }
                    else
                    {
                        try
                        {
                            tcs1.TrySetResult(action());
                        }
                        catch (Exception e)
                        {
                            Debug.LogException(e);
                            tcs1.TrySetException(e);
                        }
                    }
                },
                null
            );

            return await tcs1.Task;
        }
    }
}
