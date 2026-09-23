using UnityEditor;
using UnityEngine;

namespace Nebula.Editor
{
    /// <summary>Inspector for <see cref="Container"/>: the default fields plus a button that refits the box to the object's bounds.</summary>
    [CustomEditor(typeof(Container)), CanEditMultipleObjects]
    public sealed class ContainerEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            if (targets.Length == 1)
            {
                var container = (Container)target;
                string kind = container.FrameMode == ContainerFrameMode.Entity
                    ? "Carried by the entity on this object: it registers when the entity spawns and moves with it."
                    : "Fixed: baked with the scene or registered at runtime.";
                EditorGUILayout.HelpBox(kind, MessageType.None);
                string problem = Container.PlacementProblem(container, out bool error);
                if (problem != null) EditorGUILayout.HelpBox(problem, error ? MessageType.Error : MessageType.Warning);
            }
            DrawDefaultInspector();
            EditorGUILayout.Space();
            if (GUILayout.Button(new GUIContent("Fit Size and Center to Bounds", "Wrap the box around the enabled renderers (or colliders) on this object and its children.")))
            {
                foreach (var t in targets)
                {
                    var container = (Container)t;
                    Undo.RecordObject(container, "Fit Container to Bounds");
                    if (!container.FitToBounds())
                    {
                        Debug.LogWarning($"[nebula] container '{container.ContainerId}' has no renderers or colliders to fit to", container);
                        continue;
                    }
                    EditorUtility.SetDirty(container);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(container);
                }
                SceneView.RepaintAll();
            }
        }
    }
}
