using Ember.Collision;
using UnityEditor;
using UnityEngine;

namespace Ember.Navigation.Editor
{
    /// <summary>
    /// 标注体的场景视图编辑：画出世界 AABB，并给中心一个可拖拽手柄。
    ///
    /// 颜色即语义 —— 强制可行走 / 强制阻挡 / 仅改代价，在场景里一眼可辨；
    /// 按代价乘数插值亮度，越慢越暗。
    /// </summary>
    [CustomEditor(typeof(NavAnnotationVolume))]
    public sealed class NavAnnotationVolumeEditor : UnityEditor.Editor
    {
        private SerializedProperty m_Center;
        private SerializedProperty m_Size;

        private void OnEnable()
        {
            m_Center = serializedObject.FindProperty("m_Center");
            m_Size = serializedObject.FindProperty("m_Size");
        }

        private void OnSceneGUI()
        {
            var volume = (NavAnnotationVolume)target;
            Aabb bounds = volume.Bounds;

            Vector3 center = bounds.Center;
            Vector3 size = bounds.Size;

            Handles.color = ColorFor(volume);
            Handles.DrawWireCube(center, size);

            EditorGUI.BeginChangeCheck();
            Vector3 moved = Handles.FreeMoveHandle(center, Quaternion.identity, HandleUtility.GetHandleSize(center) * 0.15f,
                Vector3.zero, Handles.SphereHandleCap);
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.Update();
                // 手柄拖的是世界中心，组件字段存的是相对本物体的偏移。
                m_Center.vector3Value = moved - volume.transform.position;
                serializedObject.ApplyModifiedProperties();
            }
        }

        private static Color ColorFor(NavAnnotationVolume volume)
        {
            switch (volume.Walkable)
            {
                case NavAnnotationVolume.WalkableMode.ForceWalkable:
                    return new Color(0.25f, 0.9f, 0.35f, 0.9f);

                case NavAnnotationVolume.WalkableMode.ForceBlocked:
                    return new Color(0.95f, 0.3f, 0.25f, 0.9f);

                default:
                {
                    // 仅改代价：绿→红按乘数插值（1 为中性灰）。
                    float t = Mathf.Clamp01((volume.CostMultiplier - 0.5f) / 2.5f);
                    Color slow = new(0.95f, 0.75f, 0.2f, 0.9f);
                    Color fast = new(0.3f, 0.7f, 0.95f, 0.9f);
                    return Color.Lerp(fast, slow, t);
                }
            }
        }
    }
}
