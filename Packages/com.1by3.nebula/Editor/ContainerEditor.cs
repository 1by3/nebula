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
