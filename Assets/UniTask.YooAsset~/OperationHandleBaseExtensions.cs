using System;
using YooAsset;
using static Cysharp.Threading.Tasks.Internal.Error;

namespace Cysharp.Threading.Tasks
{
    public static class OperationHandleBaseExtensions
    {
        public static UniTask.Awaiter GetAwaiter(this OperationHandleBase handle)
        {
            return ToUniTask(handle).GetAwaiter();
        }

        public static UniTask ToUniTask(this OperationHandleBase handle,
                                        IProgress<float>         progress = null,
                                        PlayerLoopTiming         timing   = PlayerLoopTiming.Update)
        {
            ThrowArgumentNullException(handle, nameof(handle));

            if(!handle.IsValid)
            {
                return UniTask.CompletedTask;
            }

            return new UniTask(
                OperationHandleBaserConfiguredSource.Create(
                    handle,
                    timing,
                    progress,
                    out var token
                ),
                token
            );
        }

        sealed class OperationHandleBaserConfiguredSource : IUniTaskSource,
                                                            IPlayerLoopItem,
                                                            ITaskPoolNode<OperationHandleBaserConfiguredSource>
        {
            private static TaskPool<OperationHandleBaserConfiguredSource> pool;

            private OperationHandleBaserConfiguredSource nextNode;

            public ref OperationHandleBaserConfiguredSource NextNode => ref nextNode;

            static OperationHandleBaserConfiguredSource()
            {
                TaskPool.RegisterSizeGetter(typeof(OperationHandleBaserConfiguredSource), () => pool.Size);
            }

            private readonly Action<OperationHandleBase>            continuationAction;
            private          OperationHandleBase                    handle;
            private          IProgress<float>                       progress;
            private          bool                                   completed;
            private          UniTaskCompletionSourceCore<AsyncUnit> core;

            OperationHandleBaserConfiguredSource() { continuationAction = Continuation; }

            public static IUniTaskSource Create(OperationHandleBase handle,
                                                PlayerLoopTiming    timing,
                                                IProgress<float>    progress,
                                                out short           token)
            {
                if(!pool.TryPop(out var result))
                {
                    result = new OperationHandleBaserConfiguredSource();
                }

                result.handle    = handle;
                result.progress  = progress;
                result.completed = false;
                TaskTracker.TrackActiveTask(result, 3);

                if(progress is not null)
                {
                    PlayerLoopHelper.AddAction(timing, result);
                }

                switch(handle)
                {
                    case AssetOperationHandle asset_handle:
                        asset_handle.Completed += result.continuationAction;
                        break;
                    case SceneOperationHandle scene_handle:
                        scene_handle.Completed += result.continuationAction;
                        break;
                    case SubAssetsOperationHandle sub_asset_handle:
                        sub_asset_handle.Completed += result.continuationAction;
                        break;
                }

                token = result.core.Version;

                return result;
            }

            private void Continuation(OperationHandleBase _)
            {
                switch(handle)
                {
                    case AssetOperationHandle asset_handle:
                        asset_handle.Completed -= continuationAction;
                        break;
                    case SceneOperationHandle scene_handle:
                        scene_handle.Completed -= continuationAction;
                        break;
                    case SubAssetsOperationHandle sub_asset_handle:
                        sub_asset_handle.Completed -= continuationAction;
                        break;
                }

                if(completed)
                {
                    TryReturn();
                }
                else
                {
                    completed = true;
                    if(handle.Status == EOperationStatus.Failed)
                    {
                        core.TrySetException(new Exception(handle.LastError));
                    }
                    else
                    {
                        core.TrySetResult(AsyncUnit.Default);
                    }
                }
            }

            bool TryReturn()
            {
                TaskTracker.RemoveTracking(this);
                core.Reset();
                handle   = default;
                progress = default;
                return pool.TryPush(this);
            }

            public UniTaskStatus GetStatus(short token) => core.GetStatus(token);

            public void OnCompleted(Action<object> continuation, object state, short token)
            {
                core.OnCompleted(continuation, state, token);
            }

            public void GetResult(short token) { core.GetResult(token); }

            public UniTaskStatus UnsafeGetStatus() => core.UnsafeGetStatus();

            public bool MoveNext()
            {
                if(completed)
                {
                    TryReturn();
                    return false;
                }

                if(handle.IsValid)
                {
                    progress?.Report(handle.Progress);
                }

                return true;
            }
        }
    }
}