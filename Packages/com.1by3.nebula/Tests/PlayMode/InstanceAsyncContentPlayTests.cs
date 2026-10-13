using System.Collections;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Nebula.Tests
{
    /// <summary>
    /// NEB-428 (<c>docs/portals.md</c> §14, item 3) in play mode: <see cref="InstanceScenes.Prepare"/> no longer loads and instantiates a
    /// part's whole prefab inside the caller's tick. It creates the physics scene, starts <c>Resources.LoadAsync</c>, and reports the content as
    /// <see cref="InstanceContentState.Loading"/> until <see cref="InstanceScenes.ContentState"/>, asked a frame at a time, has seen the load and the
    /// instantiation finish; a missing resource shows as <see cref="InstanceContentState.Failed"/>; and <see cref="InstanceScenes.Complete"/> is the
    /// synchronous fallback for a placement nobody warmed.
    /// </summary>
    public class InstanceAsyncContentPlayTests
    {
        private const string Folder = "Assets/NebulaAsyncContentTest";
        private const string Resource = "NebulaAsyncContentHall";

        // The prefab is made through the Editor's asset API by reflection: this assembly does not reference the Editor.
        private static object Editor(string type, string method, params object[] args)
        {
            System.Type found = null;
            foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                found = assembly.GetType(type);
                if (found != null) break;
            }
            Assert.That(found, Is.Not.Null, type + " (these tests run in the Editor)");
            var types = new System.Type[args.Length];
            for (int i = 0; i < args.Length; i++) types[i] = args[i].GetType();
            var call = found.GetMethod(method, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static, null, types, null);
            Assert.That(call, Is.Not.Null, type + "." + method);
            return call.Invoke(null, args);
        }

        [UnitySetUp]
        public IEnumerator MakePrefab()
        {
            const string Assets = "UnityEditor.AssetDatabase";
            Editor(Assets, "DeleteAsset", Folder);
            Editor(Assets, "CreateFolder", "Assets", "NebulaAsyncContentTest");
            Editor(Assets, "CreateFolder", Folder, "Resources");
            var hall = new GameObject("hall");
            hall.transform.localPosition = new Vector3(3f, 0f, 4f);
            hall.AddComponent<NebulaAsyncContentTurn>();
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "floor";
            floor.transform.SetParent(hall.transform, false);
            Editor("UnityEditor.PrefabUtility", "SaveAsPrefabAsset", hall, Folder + "/Resources/" + Resource + ".prefab");
            Object.DestroyImmediate(hall);
            Editor(Assets, "Refresh");
            yield return null;
        }

        [UnityTearDown]
        public IEnumerator DeletePrefab()
        {
            Editor("UnityEditor.AssetDatabase", "DeleteAsset", Folder);
            yield return null;
        }

        private static Container Part(ulong id, string resource) =>
            ContainerRegistry.RegisterRuntime(id, new Bounds(Vector3.zero, Vector3.one * 10), new InstanceContainerInfo { InstanceId = id, ContentResource = resource });

        [UnityTest]
        public IEnumerator ContentIsLoadedAndInstantiatedAFrameAtATimeAndReportsReadyWhenDone()
        {
            var part = Part(92001, Resource);
            try
            {
                Assert.That(InstanceScenes.Prepare(part), Is.True, "accepted");
                Assert.That(InstanceScenes.PhysicsFor(92001).IsValid(), Is.True, "the scope's physics scene exists at once");
                Assert.That(part.transform.childCount, Is.EqualTo(0), "but nothing was instantiated inside the call");
                Assert.That(InstanceScenes.ContentState(part), Is.EqualTo(InstanceContentState.Loading));
                Assert.That(InstanceScenes.ContentProgress(part), Is.LessThan(1f));

                int frames = 0;
                while (InstanceScenes.ContentState(part) == InstanceContentState.Loading && frames < 300) { yield return null; frames++; }
                Assert.That(InstanceScenes.ContentState(part), Is.EqualTo(InstanceContentState.Ready), $"ready after {frames} frames");
                Assert.That(part.transform.childCount, Is.EqualTo(1), "the content stands under the container");
                Assert.That(part.transform.GetChild(0).Find("floor"), Is.Not.Null);
                Assert.That(InstanceScenes.ContentProgress(part), Is.EqualTo(1f));
                Assert.That(InstanceScenes.Prepare(part), Is.True, "preparing again is a no-op");
                Assert.That(part.transform.childCount, Is.EqualTo(1), "and creates nothing twice");
            }
            finally { ContainerRegistry.UnregisterRuntime(92001); }
        }

        [UnityTest]
        public IEnumerator AMissingResourceIsReportedFailedWhenTheLoadFinishes()
        {
            var part = Part(92002, "NebulaNoSuchHall");
            try
            {
                Assert.That(InstanceScenes.Prepare(part), Is.True, "a missing resource is only discovered when the load is done");
                LogAssert.Expect(LogType.Warning, new Regex("is missing"));
                int frames = 0;
                while (InstanceScenes.ContentState(part) == InstanceContentState.Loading && frames < 300) { yield return null; frames++; }
                Assert.That(InstanceScenes.ContentState(part), Is.EqualTo(InstanceContentState.Failed));
                Assert.That(InstanceScenes.Prepare(part), Is.False, "and it stays failed");
                Assert.That(InstanceScenes.Complete(part), Is.False);
            }
            finally { ContainerRegistry.UnregisterRuntime(92002); }
        }

        [UnityTest]
        public IEnumerator CompleteFinishesTheContentNowForAPlacementNobodyWarmed()
        {
            var part = Part(92003, Resource);
            try
            {
                Assert.That(InstanceScenes.Prepare(part), Is.True);
                Assert.That(InstanceScenes.ContentState(part), Is.EqualTo(InstanceContentState.Loading));
                Assert.That(InstanceScenes.Complete(part), Is.True);
                Assert.That(InstanceScenes.ContentState(part), Is.EqualTo(InstanceContentState.Ready), "ready in the same frame");
                Assert.That(part.transform.childCount, Is.EqualTo(1));
                Assert.That(part.transform.GetChild(0).Find("floor"), Is.Not.Null);
            }
            finally { ContainerRegistry.UnregisterRuntime(92003); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator ContentWithNoResourceIsReadyAtOnce()
        {
            var part = Part(92004, "");
            try
            {
                Assert.That(InstanceScenes.Prepare(part), Is.True);
                Assert.That(InstanceScenes.ContentState(part), Is.EqualTo(InstanceContentState.Ready));
            }
            finally { ContainerRegistry.UnregisterRuntime(92004); }
            yield return null;
        }

        [UnityTest]
        public IEnumerator ReleasingAPartWhileItsContentLoadsLeavesNothingBehind()
        {
            var part = Part(92005, Resource);
            Assert.That(InstanceScenes.Prepare(part), Is.True);
            ContainerRegistry.UnregisterRuntime(92005);
            for (int i = 0; i < 30; i++) yield return null;
            Assert.That(InstanceScenes.ContentState(part), Is.Not.EqualTo(InstanceContentState.Loading), "nothing is left loading for a container that is gone");
        }

        [UnityTest]
        public IEnumerator ContentLandsAtItsLocalPoseUnderAContainerThatMovedAfterTheLoadStarted()
        {
            var part = Part(92006, Resource);
            try
            {
                Assert.That(InstanceScenes.Prepare(part), Is.True);
                // The container moves to its place in its frame while the content is still loading.
                part.transform.SetPositionAndRotation(new Vector3(1200f, 40f, -300f), Quaternion.Euler(0f, 30f, 0f));
                int frames = 0;
                while (InstanceScenes.ContentState(part) == InstanceContentState.Loading && frames < 300) { yield return null; frames++; }
                Assert.That(InstanceScenes.ContentState(part), Is.EqualTo(InstanceContentState.Ready));

                var content = part.transform.GetChild(0);
                Assert.That(content.localPosition, Is.EqualTo(new Vector3(3f, 0f, 4f)), "at the prefab's pose in the container's space, not as a world pose");
                Assert.That((content.position - part.transform.TransformPoint(new Vector3(3f, 0f, 4f))).magnitude, Is.LessThan(1e-3f), "wherever the container stands");
                Assert.That(Quaternion.Angle(content.localRotation, Quaternion.Euler(0f, 90f, 0f)), Is.LessThan(0.01f), "and the turn its Awake made is kept");
            }
            finally { ContainerRegistry.UnregisterRuntime(92006); }
        }
    }
}
