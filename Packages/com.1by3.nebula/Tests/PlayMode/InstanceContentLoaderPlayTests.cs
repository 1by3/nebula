using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-367 in play mode, where <see cref="InstanceScenes.Prepare"/> really creates the part's content and moves the
    /// container into its own physics scene: the loaders under the container are still found, and report through
    /// <see cref="InstanceScenes.ContentState"/> as the content builds over several frames.
    /// </summary>
    public class InstanceContentLoaderPlayTests
    {
        private sealed class FrameBuilder : MonoBehaviour, IInstanceContentLoader
        {
            public int FramesLeft = 3;
            public InstanceContentState State => FramesLeft > 0 ? InstanceContentState.Loading : InstanceContentState.Ready;
            public float Progress => 1f - FramesLeft / 3f;
            private void Update() { if (FramesLeft > 0) FramesLeft--; }
        }

        [UnityTest]
        public IEnumerator ContentBuiltOverSeveralFramesReportsReadyWhenDone()
        {
            var part = ContainerRegistry.RegisterRuntime(91101, new Bounds(Vector3.zero, Vector3.one * 10), new InstanceContainerInfo { InstanceId = 91101 });
            var plain = ContainerRegistry.RegisterRuntime(91102, new Bounds(Vector3.zero, Vector3.one * 10), new InstanceContainerInfo { InstanceId = 91102 });
            try
            {
                new GameObject("builder").transform.SetParent(part.transform, false);
                var builder = part.transform.GetChild(0).gameObject.AddComponent<FrameBuilder>();
                Assert.That(InstanceScenes.Prepare(part), Is.True);
                Assert.That(InstanceScenes.Prepare(plain), Is.True);
                Assert.That(InstanceScenes.ContentState(plain), Is.EqualTo(InstanceContentState.Ready), "no loaders: ready at once");
                Assert.That(InstanceScenes.ContentState(part), Is.EqualTo(InstanceContentState.Loading));
                Assert.That(InstanceScenes.ContentProgress(part), Is.EqualTo(0f));

                int frames = 0;
                while (InstanceScenes.ContentState(part) == InstanceContentState.Loading && frames < 30)
                {
                    yield return null;
                    frames++;
                }
                Assert.That(InstanceScenes.ContentState(part), Is.EqualTo(InstanceContentState.Ready));
                Assert.That(frames, Is.GreaterThanOrEqualTo(3), "ready only after the builder's frames");
                Assert.That(InstanceScenes.ContentProgress(part), Is.EqualTo(1f));

                Object.Destroy(builder);
                yield return null;
                Assert.That(InstanceScenes.ContentState(part), Is.EqualTo(InstanceContentState.Ready), "a destroyed loader holds nothing back");
            }
            finally
            {
                ContainerRegistry.UnregisterRuntime(91101);
                ContainerRegistry.UnregisterRuntime(91102);
            }
        }
    }
}
